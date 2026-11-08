import { HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { AuthService } from '../../core/services/auth.service';
import { UserAdministrationService } from '../../core/services/user-administration.service';
import { AdminUsersPage } from './admin-users';

const user = { id: '1', username: 'ana', displayName: 'Ana', role: 'operador', isActive: true, mustChangePassword: true, createdAt: '', updatedAt: '', managementVersion: '7' };

describe('AdminUsersPage', () => {
  let fixture: ComponentFixture<AdminUsersPage>;
  let component: AdminUsersPage;
  const auth = { user: () => ({ id: 'admin-1', role: 'administrador', permissions: ['users:provision'] }), reauthenticate: vi.fn() };
  const admin = {
    listUsers: vi.fn(), listRoles: vi.fn(), createUser: vi.fn(), changeRole: vi.fn(), changeStatus: vi.fn(), resetPassword: vi.fn(),
  };

  beforeEach(async () => {
    vi.resetAllMocks();
    auth.reauthenticate.mockResolvedValue(undefined);
    admin.listUsers.mockReturnValue(of({ items: [user], page: 1, pageSize: 25, total: 1 }));
    admin.listRoles.mockReturnValue(of([{ code: 'operador', label: 'Operador' }]));
    await TestBed.configureTestingModule({ imports: [AdminUsersPage], providers: [
      { provide: AuthService, useValue: auth }, { provide: UserAdministrationService, useValue: admin },
    ] }).compileComponents();
    fixture = TestBed.createComponent(AdminUsersPage);
    component = fixture.componentInstance;
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('confirms and reauthenticates a reset before exposing the temporary password', async () => {
    admin.resetPassword.mockReturnValue(of({ temporaryPassword: 'temporary-secret' }));
    component.adminPassword.setValue('admin-secret');
    (component as any).requestReset(user);
    expect(admin.resetPassword).not.toHaveBeenCalled();
    await (component as any).confirmReset();
    expect(auth.reauthenticate).toHaveBeenCalledWith('admin-secret');
    expect(admin.resetPassword).toHaveBeenCalledWith('1', '7', expect.any(String));
    expect(component.temporaryPassword()).toBe('temporary-secret');
    component.closePasswordDialog();
    expect(component.temporaryPassword()).toBe('');
  });

  it('retains the same request key after an uncertain response and warns when the result cannot be recovered', async () => {
    admin.resetPassword.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })))
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'reset_result_unavailable' } })));
    (component as any).requestReset(user);
    component.adminPassword.setValue('secret');
    await (component as any).confirmReset();
    const key = admin.resetPassword.mock.calls[0][2];
    (component as any).requestReset(user);
    component.adminPassword.setValue('secret');
    await (component as any).confirmReset();
    expect(admin.resetPassword.mock.calls[1][2]).toBe(key);
    expect(component.error()).toContain('nueva solicitud');
    expect((component as any).pendingReset()).toBeNull();
  });

  it('does not offer password reset for the current user or inactive accounts', () => {
    component.users.set([{ ...user, id: 'admin-1' }, { ...user, id: 'inactive', isActive: false }, user]);
    fixture.detectChanges();
    const rows = fixture.nativeElement.querySelectorAll('tbody tr');
    expect(rows[0].textContent).not.toContain('Restablecer contraseña');
    expect(rows[1].textContent).not.toContain('Restablecer contraseña');
    expect(rows[2].textContent).toContain('Restablecer contraseña');
  });

  it('does not send a reset when confirmation is cancelled or reauthentication fails', async () => {
    (component as any).requestReset(user);
    (component as any).cancelResetConfirmation();
    expect(admin.resetPassword).not.toHaveBeenCalled();
    (component as any).requestReset(user);
    component.adminPassword.setValue('bad');
    auth.reauthenticate.mockRejectedValueOnce(new Error('invalid'));
    await (component as any).confirmReset();
    expect(admin.resetPassword).not.toHaveBeenCalled();
    expect(component.adminPassword.value).toBe('');
  });

  it('renders actual role codes and sends q/inactive filter names', async () => {
    expect(fixture.nativeElement.querySelector('select[formControlName="role"] option[value="operador"]')?.textContent).toContain('Operador');
    component.filters.setValue({ search: ' Ana ', role: '', status: 'inactive' });
    await component.submitFilters();
    expect(admin.listUsers).toHaveBeenLastCalledWith(expect.objectContaining({ q: 'Ana', status: 'inactive' }));
  });

  it('keeps each assigned role selected whether users or role options arrive first', () => {
    const adminUser = { ...user, id: '2', role: 'administrador' };
    const roles = [{ code: 'operador', label: 'Operador' }, { code: 'administrador', label: 'Administrador' }];
    component.roles.set([]);
    component.users.set([adminUser, user]);
    fixture.detectChanges();
    component.roles.set(roles);
    fixture.detectChanges();
    let selects = fixture.nativeElement.querySelectorAll('tbody tr td:nth-child(2) select') as NodeListOf<HTMLSelectElement>;
    expect(Array.from(selects, select => select.value)).toEqual(['administrador', 'operador']);

    component.users.set([]);
    fixture.detectChanges();
    component.users.set([adminUser, user]);
    fixture.detectChanges();
    selects = fixture.nativeElement.querySelectorAll('tbody tr td:nth-child(2) select');
    expect(Array.from(selects, select => select.value)).toEqual(['administrador', 'operador']);
    component.users.set([{ ...adminUser, role: 'operador' }, { ...user, role: 'administrador' }]);
    fixture.detectChanges();
    expect(Array.from(selects, select => select.value)).toEqual(['operador', 'administrador']);
  });

  it('does not display another role when the assigned role is absent from the catalog', () => {
    component.users.set([{ ...user, role: 'desconocido' }]);
    fixture.detectChanges();
    const select = fixture.nativeElement.querySelector('tbody tr td:nth-child(2) select') as HTMLSelectElement;
    expect(select.value).toBe('desconocido');
    expect(select.disabled).toBe(true);
    expect(select.selectedOptions[0].textContent).toContain('desconocido');
  });

  it('shows a role loading failure and allows retrying', async () => {
    admin.listRoles.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 503 })));
    await component.loadRoles();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-role-error]')?.textContent).toContain('roles');
    admin.listRoles.mockReturnValueOnce(of([{ code: 'operador', label: 'Operador' }]));
    fixture.nativeElement.querySelector('[data-role-error] button').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-role-error]')).toBeNull();
    expect(component.roles()[0].code).toBe('operador');
  });

  it('requires reauthentication before sending a versioned change and removes the password', async () => {
    admin.changeStatus.mockReturnValue(of(user));
    component.adminPassword.setValue('secret');
    await component.changeStatus(user, 'inactive');
    await component.confirmChange();
    expect(auth.reauthenticate).toHaveBeenCalledWith('secret');
    expect(component.adminPassword.value).toBe('');
    expect(admin.changeStatus).toHaveBeenCalledWith('1', { isActive: false, expectedManagementVersion: '7' });
    expect(auth.reauthenticate.mock.invocationCallOrder[0]).toBeLessThan(admin.changeStatus.mock.invocationCallOrder[0]);
  });

  it('never sends a mutation if reauthentication fails', async () => {
    auth.reauthenticate.mockRejectedValue(new HttpErrorResponse({ status: 401 }));
    component.adminPassword.setValue('wrong');
    await component.changeRole(user, 'administrador');
    await component.confirmChange();
    expect(admin.changeRole).not.toHaveBeenCalled();
    expect(component.adminPassword.value).toBe('');
  });

  it('refreshes stale data and retains the conflict message', async () => {
    admin.changeRole.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'stale_management_version' } })));
    component.adminPassword.setValue('secret');
    await component.changeRole(user, 'administrador');
    await component.confirmChange();
    expect(admin.listUsers).toHaveBeenCalledTimes(2);
    expect(component.error()).toContain('cambió');
  });

  it('does not lock a new creation when the administrator password is missing or rejected', async () => {
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    await component.submitCreate();
    expect(component.pendingCreation()).toBeNull();
    expect(admin.createUser).not.toHaveBeenCalled();
    auth.reauthenticate.mockRejectedValueOnce(new HttpErrorResponse({ status: 401 }));
    component.adminPassword.setValue('incorrecta');
    await component.submitCreate();
    expect(component.pendingCreation()).toBeNull();
    expect(admin.createUser).not.toHaveBeenCalled();
  });

  it('retains an uncertain creation when reauthentication of a retry fails', async () => {
    admin.createUser.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    const pending = component.pendingCreation();
    auth.reauthenticate.mockRejectedValueOnce(new HttpErrorResponse({ status: 401 }));
    component.adminPassword.setValue('incorrecta');
    await component.submitCreate();
    expect(component.pendingCreation()).toEqual(pending);
    expect(admin.createUser).toHaveBeenCalledTimes(1);
  });

  it('explains rejected username and allows correcting the first rejected creation', async () => {
    admin.createUser.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: {
      code: 'validation_failed', fieldErrors: { username: ['invalid'] },
    } })));
    component.createForm.setValue({ username: 'ana dos', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(component.error()).toContain('3 a 64');
    expect(component.pendingCreation()).toBeNull();
    component.createForm.controls.username.setValue('ana.dos');
    admin.createUser.mockReturnValueOnce(of(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'temporary' } })));
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(component.temporaryPassword()).toBe('temporary');
  });

  it('preserves an uncertain creation when a subsequent attempt is rejected', async () => {
    admin.createUser.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })))
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: {
        code: 'validation_failed', fieldErrors: { username: ['invalid'] },
      } })));
    component.createForm.setValue({ username: 'ana.dos', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    const pending = component.pendingCreation();
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(component.pendingCreation()).toEqual(pending);
    expect(admin.createUser.mock.calls[1][1]).toBe(pending?.key);
  });

  it('reuses the creation key after an uncertain network failure', async () => {
    admin.createUser.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })))
      .mockReturnValueOnce(of(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'temporary' } })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(admin.createUser.mock.calls[0][1]).toBe(admin.createUser.mock.calls[1][1]);
    expect(component.temporaryPassword()).toBe('temporary');
  });
  it('keeps an uncertain intent until it is explicitly cancelled', async () => {
    admin.createUser.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 0 })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    const key = admin.createUser.mock.calls[0][1];
    component.createForm.setValue({ username: 'otra', displayName: 'Otra', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(admin.createUser).toHaveBeenCalledTimes(1);
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(admin.createUser.mock.calls[1][1]).toBe(key);
    component.cancelCreation();
    expect(component.pendingCreation()).toBeNull();
  });

  it('clears a definitive expired receipt and explains that the password cannot be recovered', async () => {
    admin.createUser.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'idempotency_result_expired' } })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(component.pendingCreation()).toBeNull();
    expect(component.error()).toContain('contraseña temporal');
  });

  it('does not cancel an in-flight creation and retains its returned password', async () => {
    const response = new Subject<HttpResponse<unknown>>();
    admin.createUser.mockReturnValue(response);
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    const pending = component.submitCreate();
    await Promise.resolve();
    component.cancelCreation();
    expect(component.pendingCreation()).not.toBeNull();
    response.next(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'late-secret' } }));
    response.complete();
    await pending;
    expect(component.temporaryPassword()).toBe('late-secret');
  });

  it('preserves the intent and retries the same key if the response omits the password', async () => {
    admin.createUser.mockReturnValueOnce(of(new HttpResponse({ status: 201, body: { user } })))
      .mockReturnValueOnce(of(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'recovered' } })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(component.pendingCreation()).not.toBeNull();
    expect(component.error()).toContain('contraseña temporal');
    expect(component.createForm.controls.username.value).toBe('ana2');
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(admin.createUser.mock.calls[0][1]).toBe(admin.createUser.mock.calls[1][1]);
    expect(component.temporaryPassword()).toBe('recovered');
  });

  it('clears temporary password and intent on closing the password dialog', async () => {
    admin.createUser.mockReturnValue(of(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'temporary' } })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[aria-labelledby="temporary-password-title"]').textContent).toContain('temporary');
    component.closePasswordDialog();
    fixture.detectChanges();
    expect(component.temporaryPassword()).toBe('');
    expect(component.pendingCreation()).toBeNull();
    expect(fixture.nativeElement.querySelector('[aria-labelledby="temporary-password-title"]').textContent).not.toContain('temporary');
  });

  it('copies the temporary password and reports success inside the dialog', async () => {
    const copy = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: copy } });
    admin.createUser.mockReturnValue(of(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'temporary' } })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    await component.copyTemporaryPassword();
    fixture.detectChanges();
    expect(copy).toHaveBeenCalledWith('temporary');
    expect(fixture.nativeElement.querySelector('[aria-labelledby="temporary-password-title"]').textContent).toContain('Contraseña copiada');
    component.closePasswordDialog();
  });

  it('preserves the original key when an uncertain request is followed by a CSRF rejection', async () => {
    admin.createUser.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })))
      .mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 400, error: { code: 'csrf_invalid' } })))
      .mockReturnValueOnce(of(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'recovered' } })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    for (let attempt = 0; attempt < 3; attempt++) {
      component.adminPassword.setValue('secret');
      await component.submitCreate();
    }
    expect(admin.createUser.mock.calls.map(call => call[1])).toEqual(Array(3).fill(admin.createUser.mock.calls[0][1]));
    expect(component.temporaryPassword()).toBe('recovered');
    component.closePasswordDialog();
  });

  it('asks for confirmation before changing an account and cancellation sends nothing', async () => {
    component.adminPassword.setValue('secret');
    await component.changeRole(user, 'administrador');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[aria-labelledby="confirm-change-title"]').textContent).toContain('Ana');
    expect(admin.changeRole).not.toHaveBeenCalled();
    component.cancelChange();
    expect(admin.changeRole).not.toHaveBeenCalled();
    expect(component.adminPassword.value).toBe('');
  });

  it('tells the administrator to retry CSRF failures without reloading', async () => {
    admin.createUser.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { code: 'csrf_invalid' } })));
    component.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    component.adminPassword.setValue('secret');
    await component.submitCreate();
    expect(component.error()).toContain('sin recargar');
    expect(component.pendingCreation()).not.toBeNull();
  });

});

describe('AdminUsersPage creation screen', () => {
  it('disables cancellation while the creation request is in flight', async () => {
    const response = new Subject<HttpResponse<unknown>>();
    const admin = { listRoles: () => of([{ code: 'operador', label: 'Operador' }]), createUser: () => response };
    await TestBed.configureTestingModule({ imports: [AdminUsersPage], providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { data: { mode: 'create' } } } },
      { provide: AuthService, useValue: { user: () => ({ role: 'administrador', permissions: ['users:provision'] }), reauthenticate: vi.fn().mockResolvedValue(undefined) } },
      { provide: UserAdministrationService, useValue: admin },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(AdminUsersPage);
    await fixture.whenStable();
    const page = fixture.componentInstance;
    page.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    page.adminPassword.setValue('secret');
    const pending = page.submitCreate();
    await Promise.resolve();
    fixture.detectChanges();
    const cancel = [...fixture.nativeElement.querySelectorAll('button')].find((button: any) => button.textContent.includes('Cancelar solicitud pendiente')) as HTMLButtonElement;
    expect(cancel.disabled).toBe(true);
    response.next(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'temporary' } }));
    response.complete();
    await pending;
  });

  it('does not request the user directory when a user is created', async () => {
    const admin = { listRoles: vi.fn().mockReturnValue(of([{ code: 'operador', label: 'Operador' }])), listUsers: vi.fn(), createUser: vi.fn().mockReturnValue(of(new HttpResponse({ status: 201, body: { user, temporaryPassword: 'temporary' } }))) };
    await TestBed.configureTestingModule({ imports: [AdminUsersPage], providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { data: { mode: 'create' } } } },
      { provide: AuthService, useValue: { user: () => ({ role: 'administrador', permissions: ['users:provision'] }), reauthenticate: vi.fn().mockResolvedValue(undefined) } },
      { provide: UserAdministrationService, useValue: admin },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(AdminUsersPage);
    await fixture.whenStable();
    const page = fixture.componentInstance;
    page.createForm.setValue({ username: 'ana2', displayName: 'Ana Dos', role: 'operador' });
    page.adminPassword.setValue('secret');
    await page.submitCreate();
    expect(page.temporaryPassword()).toBe('temporary');
    expect(admin.listUsers).not.toHaveBeenCalled();
  });

  it('shows creation only and protects an unresolved request or temporary password', async () => {
    const admin = { listRoles: vi.fn().mockReturnValue(of([{ code: 'operador', label: 'Operador' }])), listUsers: vi.fn() };
    await TestBed.configureTestingModule({ imports: [AdminUsersPage], providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { data: { mode: 'create' } } } },
      { provide: AuthService, useValue: { user: () => ({ role: 'administrador', permissions: ['users:provision'] }) } },
      { provide: UserAdministrationService, useValue: admin },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(AdminUsersPage);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.create-form')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('table')).toBeNull();
    expect(admin.listUsers).not.toHaveBeenCalled();
    const page = fixture.componentInstance;
    expect(page.canLeave()).toBe(true);
    page.pendingCreation.set({ intent: '{}', key: 'request-key' });
    expect(page.canLeave()).toBe(false);
    page.pendingCreation.set(null);
    page.temporaryPassword.set('temporary');
    expect(page.canLeave()).toBe(false);
  });

  it('does not offer administration to an operator even if the permission is present', async () => {
    await TestBed.configureTestingModule({ imports: [AdminUsersPage], providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { data: { mode: 'create' } } } },
      { provide: AuthService, useValue: { user: () => ({ role: 'operador', permissions: ['users:provision'] }) } },
      { provide: UserAdministrationService, useValue: { listRoles: () => of([]) } },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(AdminUsersPage);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.create-form')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('No tienes permisos');
  });
});
