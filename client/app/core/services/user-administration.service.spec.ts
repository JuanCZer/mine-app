import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { UserAdministrationService } from './user-administration.service';
import { firstValueFrom } from 'rxjs';

describe('UserAdministrationService', () => {
  let service: UserAdministrationService;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(UserAdministrationService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('sends only supplied directory filters using backend names', async () => {
    const result = firstValueFrom(service.listUsers({ q: 'ana', page: 2, pageSize: 10, status: 'inactive' }));
    const req = http.expectOne(r => r.url === '/api/admin/users');
    expect(req.request.params.get('q')).toBe('ana');
    expect(req.request.params.get('status')).toBe('inactive');
    expect(req.request.params.has('role')).toBe(false);
    req.flush({ items: [], page: 2, pageSize: 10, total: 0 });
    expect((await result).total).toBe(0);
  });

  it('reads role codes and sends creation idempotency key', async () => {
    const roles = firstValueFrom(service.listRoles());
    http.expectOne('/api/admin/roles').flush([{ code: 'operador', label: 'Operador' }]);
    expect((await roles)[0].code).toBe('operador');
    const created = firstValueFrom(service.createUser({ username: 'ana', displayName: 'Ana', role: 'operador' }, 'b62f76f1-5564-4616-9496-34a120c75b7c'));
    const req = http.expectOne('/api/admin/users');
    expect(req.request.headers.get('Idempotency-Key')).toBe('b62f76f1-5564-4616-9496-34a120c75b7c');
    expect(req.request.body).toEqual({ username: 'ana', displayName: 'Ana', role: 'operador' });
    req.flush({ user: { id: '1' }, temporaryPassword: 'temporary' }, { status: 201, statusText: 'Created' });
    expect((await created).body?.temporaryPassword).toBe('temporary');
  });

  it('sends an idempotent password reset with the observed version', async () => {
    const result = firstValueFrom(service.resetPassword('u-2', '7', 'b62f76f1-5564-4616-9496-34a120c75b7c'));
    const req = http.expectOne('/api/admin/users/u-2/password-reset');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ expectedManagementVersion: '7' });
    expect(req.request.headers.get('Idempotency-Key')).toBe('b62f76f1-5564-4616-9496-34a120c75b7c');
    req.flush({ temporaryPassword: 'temporary' });
    expect((await result).temporaryPassword).toBe('temporary');
  });

  it('uses PUT and includes the observed management version for mutations', async () => {
    const changeRole = firstValueFrom(service.changeRole('u-2', { role: 'administrador', expectedManagementVersion: '7' }));
    const role = http.expectOne('/api/admin/users/u-2/role');
    expect(role.request.method).toBe('PUT');
    expect(role.request.body).toEqual({ role: 'administrador', expectedManagementVersion: '7' });
    role.flush({ id: 'u-2' });
    await changeRole;
    const changeStatus = firstValueFrom(service.changeStatus('u-2', { isActive: false, expectedManagementVersion: '8' }));
    const status = http.expectOne('/api/admin/users/u-2/status');
    expect(status.request.method).toBe('PUT');
    expect(status.request.body).toEqual({ isActive: false, expectedManagementVersion: '8' });
    status.flush({ id: 'u-2' });
    await changeStatus;
  });
});
