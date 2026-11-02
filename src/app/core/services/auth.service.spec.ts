import { HttpErrorResponse, provideHttpClient, withXsrfConfiguration } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { CaseInput } from '../models/detection.model';
import { DetectionService } from './detection.service';
import { AuthService } from './auth.service';

const user = {
  id: '1c504531-ab30-49d4-a860-ae69fdbb7b11',
  username: 'operador',
  displayName: 'Operador Uno',
  role: 'operador',
  createdAt: '2026-10-01T10:00:00.000Z',
  permissions: ['incidents:read', 'incidents:write'],
  mustChangePassword: false,
};

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    localStorage.setItem('notes_token', 'legacy-token');
    localStorage.setItem('notes_user', JSON.stringify(user));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(
          withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
        ),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
    document.cookie = 'XSRF-TOKEN=; Max-Age=0; path=/';
  });

  it('starts unknown and ignores legacy bearer and cached-user values', () => {
    expect(service.state()).toBe('unknown');
    expect(service.user()).toBeNull();
    expect(service.authenticated()).toBe(false);
  });

  it('shares one pending /me request between concurrent guards', async () => {
    const first = service.ensureSession();
    const second = service.ensureSession();
    http.expectOne('/api/auth/me').flush(user);

    await expect(first).resolves.toBe(true);
    await expect(second).resolves.toBe(true);
    expect(service.state()).toBe('authenticated');
    expect(service.user()).toEqual(user);
  });

  it('replaces cached role and permissions with the refreshed server session', async () => {
    const first = service.ensureSession();
    http.expectOne('/api/auth/me').flush({ ...user, role: 'administrador', permissions: ['users:provision'] });
    await first;
    const refreshed = service.ensureSession(true);
    http.expectOne('/api/auth/me').flush(user);
    await expect(refreshed).resolves.toBe(true);
    expect(service.user()?.role).toBe('operador');
    expect(service.user()?.permissions).not.toContain('users:provision');
  });

  it('discards a late session response after logout or expiration', async () => {
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const pending = service.ensureSession();
    const request = http.expectOne('/api/auth/me');
    await service.expireSession();
    request.flush(user);
    await expect(pending).resolves.toBe(false);
    expect(service.state()).toBe('anonymous');
    expect(service.user()).toBeNull();
  });

  it('does not let a late failure clear a newer session or its pending request', async () => {
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const old = service.ensureSession();
    const oldRequest = http.expectOne('/api/auth/me');
    await service.expireSession();
    const fresh = service.ensureSession(true);
    const freshRequest = http.expectOne('/api/auth/me');
    oldRequest.flush(null, { status: 401, statusText: 'Unauthorized' });
    await old;
    const same = service.ensureSession(true);
    http.expectNone('/api/auth/me');
    freshRequest.flush({ ...user, username: 'cuenta-nueva' });
    await expect(fresh).resolves.toBe(true);
    await expect(same).resolves.toBe(true);
    expect(service.user()?.username).toBe('cuenta-nueva');
    expect(service.state()).toBe('authenticated');
  });

  it('ignores late failure after a newer account has authenticated', async () => {
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const old = service.ensureSession();
    const oldRequest = http.expectOne('/api/auth/me');
    await service.expireSession();
    const fresh = service.ensureSession(true);
    http.expectOne('/api/auth/me').flush({ ...user, username: 'cuenta-nueva' });
    await fresh;
    oldRequest.flush(null, { status: 401, statusText: 'Unauthorized' });
    await expect(old).resolves.toBe(true);
    expect(service.user()?.username).toBe('cuenta-nueva');
    expect(service.state()).toBe('authenticated');
  });

  it('marks a rejected cookie as anonymous', async () => {
    const result = service.ensureSession();
    http.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(result).resolves.toBe(false);
    expect(service.state()).toBe('anonymous');
    expect(service.user()).toBeNull();
  });

  it('distinguishes an unavailable API from an anonymous session', async () => {
    const result = service.ensureSession();
    http.expectOne('/api/auth/me').error(new ProgressEvent('network error'));

    await expect(result).resolves.toBe(false);
    expect(service.state()).toBe('unavailable');
  });

  it('obtains CSRF before posting credentials and keeps session data in memory', async () => {
    const result = service.login('operador', 'una contraseña extensa');
    http.expectOne('/api/auth/csrf').flush(null);
    document.cookie = 'XSRF-TOKEN=csrf-test; path=/';
    await Promise.resolve();
    const request = http.expectOne('/api/auth/login');
    expect(request.request.body).toEqual({ username: 'operador', password: 'una contraseña extensa' });
    expect(request.request.headers.has('Authorization')).toBe(false);
    expect(request.request.headers.get('X-XSRF-TOKEN')).toBe('csrf-test');
    request.flush({ user });

    await expect(result).resolves.toBeUndefined();
    expect(service.user()).toEqual(user);
    expect(service.state()).toBe('authenticated');
    expect(localStorage.getItem('notes_token')).toBeNull();
  });

  it('preserves the current session while reauthenticating and keeps the route context', async () => {
    const session = service.ensureSession();
    http.expectOne('/api/auth/me').flush({ ...user, mustChangePassword: true });
    await session;
    expect(service.user()).toEqual({ ...user, mustChangePassword: true });

    const result = service.reauthenticate('bienvenido123');
    http.expectOne('/api/auth/csrf').flush(null);
    await Promise.resolve();
    const request = http.expectOne('/api/auth/reauth');
    expect(request.request.body).toEqual({ password: 'bienvenido123' });
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expect(result).resolves.toBeUndefined();
    expect(service.state()).toBe('authenticated');
    expect(service.user()).toEqual({ ...user, mustChangePassword: true });
  });

  it('clears the session on password change and navigates without a returnUrl', async () => {
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const session = service.ensureSession();
    http.expectOne('/api/auth/me').flush({ ...user, mustChangePassword: true });
    await session;

    const result = service.changePassword('viejaClave', 'nuevaClave123', 'nuevaClave123');
    http.expectOne('/api/auth/csrf').flush(null);
    await Promise.resolve();
    const request = http.expectOne('/api/auth/change-password');
    expect(request.request.body).toEqual({
      currentPassword: 'viejaClave',
      newPassword: 'nuevaClave123',
      confirmation: 'nuevaClave123',
    });
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expect(result).resolves.toBeUndefined();
    expect(service.state()).toBe('anonymous');
    expect(router.navigate).toHaveBeenCalledWith(['/login'], { queryParams: undefined });
  });

  it('does not claim a remote logout when the server is unavailable', async () => {
    const session = service.ensureSession();
    http.expectOne('/api/auth/me').flush(user);
    await session;
    const navigation = vi.spyOn(router, 'navigate');

    const result = service.logout();
    http.expectOne('/api/auth/csrf').flush(null);
    await Promise.resolve();
    http.expectOne('/api/auth/logout').flush(
      { error: 'No se pudo cerrar la sesión. Reintente.' },
      { status: 503, statusText: 'Unavailable' },
    );

    await expect(result).rejects.toBeInstanceOf(HttpErrorResponse);
    expect(service.state()).toBe('authenticated');
    expect(service.user()).toEqual(user);
    expect(navigation).not.toHaveBeenCalled();
  });

  it('clears a confirmed logout and treats 401 as an already expired session', async () => {
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    for (const status of [204, 401]) {
      const session = service.ensureSession(true);
      http.expectOne('/api/auth/me').flush(user);
      await session;
      const result = service.logout();
      http.expectOne('/api/auth/csrf').flush(null);
      await Promise.resolve();
      const request = http.expectOne('/api/auth/logout');
      status === 204
        ? request.flush(null, { status: 204, statusText: 'No Content' })
        : request.flush(null, { status: 401, statusText: 'Unauthorized' });
      await expect(result).resolves.toBeUndefined();
      expect(service.state()).toBe('anonymous');
    }
  });

  it('does not silently delete an unclaimed legacy detection archive on logout', async () => {
    const legacyKey = 'gds-detection-cases-v1';
    const legacyArchive = '{"version":1,"cases":[{"id":"account-a"}]}';
    localStorage.setItem(legacyKey, legacyArchive);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const session = service.ensureSession();
    http.expectOne('/api/auth/me').flush(user);
    await session;

    const result = service.logout();
    http.expectOne('/api/auth/csrf').flush(null);
    await Promise.resolve();
    http.expectOne('/api/auth/logout').flush(null, { status: 204, statusText: 'No Content' });
    await result;

    expect(localStorage.getItem(legacyKey)).toBe(legacyArchive);
  });

  it('keeps account B from reading or modifying account A legacy cases after logout', async () => {
    const legacyKey = 'gds-detection-cases-v1';
    const legacyArchive = '{"version":1,"cases":[{"id":"account-a","title":"Caso A"}]}';
    localStorage.setItem(legacyKey, legacyArchive);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const accountA = service.ensureSession();
    http.expectOne('/api/auth/me').flush(user);
    await accountA;

    const logout = service.logout();
    http.expectOne('/api/auth/csrf').flush(null);
    await Promise.resolve();
    http.expectOne('/api/auth/logout').flush(null, { status: 204, statusText: 'No Content' });
    await logout;
    const accountB = service.ensureSession(true);
    http.expectOne('/api/auth/me').flush({ ...user, id: '22222222-2222-2222-2222-222222222222', username: 'cuenta-b' });
    await accountB;

    const detection = new DetectionService();
    const input: CaseInput = {
      title: 'Intento B',
      state: 'michoacan',
      municipality: 'Zona',
      latitude: 19.4,
      longitude: -101.2,
      soil: 'andosol',
      season: 'rainy',
      priority: 'medium',
      owner: 'Cuenta B',
      description: 'No debe alcanzar el archivo de A.',
    };
    expect(detection.cases()).toEqual([]);
    expect(() => detection.create(input)).toThrow('retirado');
    expect(localStorage.getItem(legacyKey)).toBe(legacyArchive);
  });
});
