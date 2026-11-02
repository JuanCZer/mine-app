import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { adminGuard, authGuard, guestGuard, passwordChangeGuard, safeReturnUrl } from './auth.guard';

describe('authentication guards', () => {
  const route = {} as ActivatedRouteSnapshot;

  function configure(result: boolean) {
    const auth = { ensureSession: vi.fn().mockResolvedValue(result) };
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: auth }],
    });
    return auth;
  }

  it('waits for the verified session and preserves a local return URL', async () => {
    const auth = configure(false);
    const state = { url: '/incidencias?note=123#fase-1' } as RouterStateSnapshot;
    const result = await TestBed.runInInjectionContext(() => authGuard(route, state));

    expect(auth.ensureSession).toHaveBeenCalledOnce();
    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe(
      '/login?returnUrl=%2Fincidencias%3Fnote%3D123%23fase-1',
    );
  });

  it('lets a verified session enter private routes', async () => {
    configure(true);
    await expect(
      TestBed.runInInjectionContext(() =>
        authGuard(route, { url: '/mapa' } as RouterStateSnapshot),
      ),
    ).resolves.toBe(true);
  });

  it('redirects authenticated guests and leaves anonymous guests on login', async () => {
    const auth = configure(true);
    const authenticated = await TestBed.runInInjectionContext(() =>
      guestGuard(route, { url: '/login' } as RouterStateSnapshot),
    );
    expect(TestBed.inject(Router).serializeUrl(authenticated as never)).toBe('/incidencias');

    auth.ensureSession.mockResolvedValue(false);
    await expect(
      TestBed.runInInjectionContext(() =>
        guestGuard(route, { url: '/login' } as RouterStateSnapshot),
      ),
    ).resolves.toBe(true);
  });

  it('rejects admin routes without the required permission', async () => {
    const auth = {
      ensureSession: vi.fn().mockResolvedValue(true),
      user: vi.fn().mockReturnValue({ permissions: ['incidents:read'] }),
    };
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: auth }],
    });

    const result = await TestBed.runInInjectionContext(() =>
      adminGuard(route, { url: '/administracion/usuarios' } as RouterStateSnapshot),
    );

    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe('/incidencias');
  });

  it('refreshes the server session before checking admin access', async () => {
    const auth = {
      ensureSession: vi.fn().mockResolvedValue(true),
      user: vi.fn().mockReturnValue({ role: 'operador', permissions: ['users:provision'] }),
    };
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth }] });
    const result = await TestBed.runInInjectionContext(() => adminGuard(route, { url: '/administracion/usuarios' } as RouterStateSnapshot));
    expect(auth.ensureSession).toHaveBeenCalledWith(true);
    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe('/incidencias');
  });

  it('allows administration only when refreshed role and permission both match', async () => {
    const auth = {
      ensureSession: vi.fn().mockResolvedValue(true),
      user: vi.fn().mockReturnValue({ role: 'administrador', permissions: ['users:provision'] }),
    };
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth }] });
    await expect(TestBed.runInInjectionContext(() => adminGuard(route, { url: '/administracion/usuarios' } as RouterStateSnapshot))).resolves.toBe(true);
    auth.user.mockReturnValue({ role: undefined, permissions: ['users:provision'] });
    const result = await TestBed.runInInjectionContext(() => adminGuard(route, { url: '/administracion/usuarios' } as RouterStateSnapshot));
    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe('/incidencias');
  });

  it('refreshes the account before displaying the profile', async () => {
    const auth = configure(true);
    await TestBed.runInInjectionContext(() => authGuard(route, { url: '/cuenta/perfil?view=basic' } as RouterStateSnapshot));
    expect(auth.ensureSession).toHaveBeenCalledWith(true);
  });

  it('forces a password-change flow to the account page when the user is required to rotate it', async () => {
    const auth = {
      ensureSession: vi.fn().mockResolvedValue(true),
      user: vi.fn().mockReturnValue({ mustChangePassword: true }),
    };
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: auth }],
    });

    const result = await TestBed.runInInjectionContext(() =>
      passwordChangeGuard(route, { url: '/mapa' } as RouterStateSnapshot),
    );

    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe('/cuenta/contrasena');
  });

  it('accepts only application-local return destinations', () => {
    expect(safeReturnUrl('/mapa?zoom=4')).toBe('/mapa?zoom=4');
    expect(safeReturnUrl('//evil.example/steal')).toBeNull();
    expect(safeReturnUrl('https://evil.example/steal')).toBeNull();
    expect(safeReturnUrl('/login')).toBeNull();
    expect(safeReturnUrl('/%2f%2fevil.example')).toBeNull();
  });
  it('redirects a profile visit to login without a verified session', async () => {
    configure(false);
    const result = await TestBed.runInInjectionContext(() => authGuard(route, { url: '/cuenta/perfil' } as RouterStateSnapshot));
    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe('/login?returnUrl=%2Fcuenta%2Fperfil');
  });

  it('redirects forced password change away from profile', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { ensureSession: vi.fn().mockResolvedValue(true), user: vi.fn().mockReturnValue({ mustChangePassword: true }) } }] });
    const result = await TestBed.runInInjectionContext(() => passwordChangeGuard(route, { url: '/cuenta/perfil' } as RouterStateSnapshot));
    expect(TestBed.inject(Router).serializeUrl(result as never)).toBe('/cuenta/contrasena');
  });

});
