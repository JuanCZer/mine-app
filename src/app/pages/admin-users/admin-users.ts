import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, ElementRef, HostListener, inject, signal, ViewChild } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../../core/services/auth.service';
import { UserAdministrationService } from '../../core/services/user-administration.service';
import {
  AdminRoleOption,
  AdminUserQuery,
  AdminUserStatus,
  AdminUserSummary,
} from '../../core/models/user-administration.model';

@Component({
  selector: 'app-admin-users',
  imports: [CommonModule, ReactiveFormsModule],
  templateUrl: './admin-users.html',
  styleUrl: './admin-users.less',
})
export class AdminUsersPage {
  readonly createMode = inject(ActivatedRoute, { optional: true })?.snapshot.data['mode'] === 'create';
  readonly loading = signal(false);
  readonly submitting = signal(false);
  readonly roles = signal<AdminRoleOption[]>([]);
  readonly rolesError = signal('');
  readonly users = signal<AdminUserSummary[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(25);
  readonly error = signal('');
  readonly success = signal('');
  readonly temporaryPassword = signal('');
  readonly copyMessage = signal('');
  readonly adminPassword = new FormControl('', { nonNullable: true });
  readonly pendingCreation = signal<{ intent: string; key: string } | null>(null);
  readonly pendingReset = signal<{ user: AdminUserSummary; key: string } | null>(null);
  readonly resetConfirmation = signal<AdminUserSummary | null>(null);
  @ViewChild('resetDialog') private resetDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('passwordDialog') private passwordDialog?: ElementRef<HTMLDialogElement>;
  @ViewChild('confirmDialog') private confirmDialog?: ElementRef<HTMLDialogElement>;
  readonly pendingChange = signal<{ user: AdminUserSummary; role?: string; status?: AdminUserStatus } | null>(null);
  private changeTrigger?: HTMLElement;
  private creationGeneration = 0;
  private dialogTrigger?: HTMLElement;
  readonly canManageUsers = computed(
    () => this.auth.user()?.role === 'administrador' && (this.auth.user()?.permissions ?? []).includes('users:provision'),
  );

  readonly filters = new FormGroup({
    search: new FormControl('', { nonNullable: true }),
    role: new FormControl('', { nonNullable: true }),
    status: new FormControl<'active' | 'inactive' | ''>('', { nonNullable: true }),
  });

  readonly createForm = new FormGroup({
    username: new FormControl('', { nonNullable: true }),
    displayName: new FormControl('', { nonNullable: true }),
    role: new FormControl('', { nonNullable: true }),
  });

  constructor(
    private readonly admin: UserAdministrationService,
    private readonly auth: AuthService,
  ) {
    this.loadRoles();
    if (!this.createMode) this.loadUsers();
  }

  async loadUsers(): Promise<void> {
    if (!this.canManageUsers()) {
      this.users.set([]);
      this.total.set(0);
      return;
    }

    this.loading.set(true);
    this.error.set('');

    const query: AdminUserQuery = {
      q: this.filters.controls.search.value.trim() || undefined,
      page: this.page(),
      pageSize: this.pageSize(),
      role: this.filters.controls.role.value || undefined,
      status: (this.filters.controls.status.value || undefined) as AdminUserStatus | undefined,
    };

    try {
      const response = await firstValueFrom(this.admin.listUsers(query));
      this.users.set(response?.items ?? []);
      this.total.set(response?.total ?? 0);
    } catch (error) {
      this.error.set(this.describeError(error));
    } finally {
      this.loading.set(false);
    }
  }

  async loadRoles(): Promise<void> {
    this.rolesError.set('');
    try {
      const roles = await firstValueFrom(this.admin.listRoles());
      this.roles.set(roles ?? []);
      if (!roles?.length) this.rolesError.set('No hay roles disponibles. Revisa el catálogo y vuelve a intentar.');
    } catch {
      this.roles.set([]);
      this.rolesError.set('No fue posible cargar los roles. Vuelve a intentar.');
    }
  }

  hasRole(code: string): boolean {
    return this.roles().some(role => role.code === code);
  }

  canLeave(): boolean {
    if (this.submitting() || this.pendingCreation() || this.pendingReset() || this.temporaryPassword()) {
      this.error.set('Resuelve la solicitud pendiente y guarda la contraseña temporal antes de salir.');
      return false;
    }
    return true;
  }

  @HostListener('window:beforeunload', ['$event'])
  protectPendingCreation(event: BeforeUnloadEvent): void {
    if (this.submitting() || this.pendingCreation() || this.pendingReset() || this.temporaryPassword()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }

  async submitFilters(): Promise<void> {
    this.page.set(1);
    await this.loadUsers();
  }

  async submitCreate(): Promise<void> {
    if (!this.canManageUsers() || this.submitting() || this.temporaryPassword()) return;
    const { username, displayName, role } = this.createForm.getRawValue();
    const command = { username: username.trim(), displayName: displayName.trim(), role };
    if (!command.username || !command.displayName || !role) {
      this.error.set('Completa nombre de usuario, nombre visible y rol antes de crear.');
      return;
    }

    const intent = JSON.stringify(command);
    const existing = this.pendingCreation();
    if (existing && existing.intent !== intent) {
      this.error.set('Hay una creación pendiente. Reinténtala con los mismos datos o cancélala antes de cambiarlos.');
      return;
    }
    const generation = this.creationGeneration;
    this.dialogTrigger = document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
    const isCurrent = () => generation === this.creationGeneration;
    await this.performMutation(async () => {
      // No request is uncertain until reauthentication has succeeded and the
      // creation is about to be sent. Preserve any prior uncertain intent.
      if (!existing) this.pendingCreation.set({ intent, key: crypto.randomUUID() });
      const key = this.pendingCreation()!.key;
      try {
        const response = await firstValueFrom(this.admin.createUser(command, key));
        if (!isCurrent()) return;
        const password = response.body?.temporaryPassword ?? '';
        if (typeof password !== 'string' || !password) {
          this.error.set('La respuesta no incluyó la contraseña temporal. Reintenta la misma solicitud para recuperarla.');
          return;
        }
        this.temporaryPassword.set(password);
        this.success.set('Usuario creado con contraseña temporal.');
        this.createForm.reset({ username: '', displayName: '', role: '' });
        const dialog = this.passwordDialog?.nativeElement;
        if (dialog && !dialog.open) {
          if (typeof dialog.showModal === 'function') dialog.showModal();
          else dialog.setAttribute('open', '');
        }
      } catch (error) {
        const code = error instanceof HttpErrorResponse && typeof error.error?.code === 'string' ? error.error.code : '';
        if (isCurrent() && error instanceof HttpErrorResponse && error.status === 409 &&
            ['username_conflict', 'idempotency_key_reused', 'idempotency_result_expired'].includes(code)) {
          this.pendingCreation.set(null);
        }
        // A rejection of the first request precedes persistence. A later rejection
        // cannot establish whether an earlier, uncertain attempt committed.
        if (isCurrent() && !existing && error instanceof HttpErrorResponse &&
            error.status === 400 && code === 'validation_failed') {
          this.pendingCreation.set(null);
        }
        throw error;
      }
    }, isCurrent);
  }

  canReset(user: AdminUserSummary): boolean {
    return this.canManageUsers() && !!this.auth.user()?.id && user.isActive && user.id !== this.auth.user()?.id
      && (!this.pendingReset() || this.pendingReset()!.user.id === user.id);
  }

  requestReset(user: AdminUserSummary): void {
    if (!this.canReset(user) || this.submitting() || this.temporaryPassword()) return;
    if (this.pendingReset() && this.pendingReset()!.user.id !== user.id) return;
    this.changeTrigger = document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
    this.resetConfirmation.set(user);
    const dialog = this.resetDialog?.nativeElement;
    if (dialog && !dialog.open) {
      if (typeof dialog.showModal === 'function') dialog.showModal();
      else dialog.setAttribute('open', '');
    }
  }

  cancelResetConfirmation(): void {
    this.resetConfirmation.set(null);
    this.adminPassword.reset('');
    this.closeResetDialog();
  }

  private closeResetDialog(): void {
    const dialog = this.resetDialog?.nativeElement;
    if (dialog?.open && typeof dialog.close === 'function') dialog.close();
    else dialog?.removeAttribute('open');
    this.changeTrigger?.focus();
    this.changeTrigger = undefined;
  }

  async confirmReset(): Promise<void> {
    const selected = this.resetConfirmation();
    if (!selected || !this.canReset(selected) || this.submitting()) return;
    const existing = this.pendingReset();
    if (existing && existing.user.id !== selected.id) return;
    const reset = existing ?? { user: selected, key: crypto.randomUUID() };
    this.resetConfirmation.set(null);
    this.closeResetDialog();
    this.dialogTrigger = document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
    await this.performMutation(async () => {
      this.pendingReset.set(reset);
      try {
        const response = await firstValueFrom(this.admin.resetPassword(reset.user.id, reset.user.managementVersion, reset.key));
        if (!response || typeof response.temporaryPassword !== 'string' || !response.temporaryPassword) {
          this.error.set('No se recibió la contraseña temporal. Reintenta la misma solicitud.');
          return;
        }
        this.pendingReset.set(null);
        this.temporaryPassword.set(response.temporaryPassword);
        this.success.set(`Contraseña temporal generada para ${reset.user.username}.`);
        const dialog = this.passwordDialog?.nativeElement;
        if (dialog && !dialog.open) {
          if (typeof dialog.showModal === 'function') dialog.showModal();
          else dialog.setAttribute('open', '');
        }
      } catch (error) {
        const code = error instanceof HttpErrorResponse ? error.error?.code : '';
        if (['reset_result_unavailable', 'stale_management_version', 'idempotency_key_reused', 'user_inactive', 'user_not_found'].includes(code)) {
          this.pendingReset.set(null);
        }
        throw error;
      }
    });
  }

  async discardPendingReset(): Promise<void> {
    if (!this.pendingReset() || this.submitting()) return;
    this.pendingReset.set(null);
    this.adminPassword.reset('');
    await this.loadUsers();
    this.error.set('La solicitud anterior pudo procesarse. Revisa el usuario y crea una nueva solicitud si es necesario.');
  }

  async changeRole(user: AdminUserSummary, role: string): Promise<void> {
    if (!role || role === user.role || this.submitting() || !this.canManageUsers()) return;
    this.openChangeConfirmation({ user, role });
  }

  async changeStatus(user: AdminUserSummary, status: AdminUserStatus): Promise<void> {
    if (!['active', 'inactive'].includes(status) || (status === 'active') === user.isActive || this.submitting() || !this.canManageUsers()) return;
    this.openChangeConfirmation({ user, status });
  }

  private openChangeConfirmation(change: { user: AdminUserSummary; role?: string; status?: AdminUserStatus }): void {
    this.changeTrigger = document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
    this.pendingChange.set(change);
    const dialog = this.confirmDialog?.nativeElement;
    if (dialog && !dialog.open) {
      if (typeof dialog.showModal === 'function') dialog.showModal();
      else dialog.setAttribute('open', '');
    }
  }

  cancelChange(): void {
    this.pendingChange.set(null);
    this.adminPassword.reset('');
    this.closeChangeDialog();
  }

  async confirmChange(): Promise<void> {
    const change = this.pendingChange();
    if (!change) return;
    this.pendingChange.set(null);
    this.closeChangeDialog();
    const { user, role, status } = change;
    await this.performMutation(async () => {
      if (role) {
        await firstValueFrom(this.admin.changeRole(user.id, { role, expectedManagementVersion: user.managementVersion }));
        this.success.set(`Rol actualizado para ${user.username}.`);
      } else if (status) {
        await firstValueFrom(this.admin.changeStatus(user.id, {
          isActive: status === 'active', expectedManagementVersion: user.managementVersion,
        }));
        this.success.set(`Estado actualizado para ${user.username}.`);
      }
    });
  }

  private closeChangeDialog(): void {
    const dialog = this.confirmDialog?.nativeElement;
    if (dialog?.open && typeof dialog.close === 'function') dialog.close();
    else dialog?.removeAttribute('open');
    this.changeTrigger?.focus();
    this.changeTrigger = undefined;
  }

  selectRole(user: AdminUserSummary, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const role = select.value;
    select.value = user.role;
    void this.changeRole(user, role);
  }

  selectStatus(user: AdminUserSummary, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const status = select.value as AdminUserStatus;
    select.value = user.isActive ? 'active' : 'inactive';
    void this.changeStatus(user, status);
  }

  private async performMutation(operation: () => Promise<void>, isCurrent: () => boolean = () => true): Promise<void> {
    if (!this.canManageUsers() || this.submitting()) return;
    const password = this.adminPassword.value;
    this.adminPassword.reset('');
    if (!password) {
      this.error.set('Confirma tu contraseña de administrador antes de realizar la operación.');
      return;
    }
    this.submitting.set(true);
    this.error.set('');
    this.success.set('');
    this.temporaryPassword.set('');
    try {
      await this.auth.reauthenticate(password);
      if (!isCurrent()) return;
      await operation();
      if (isCurrent() && !this.createMode && !this.error()) await this.loadUsers();
    } catch (error) {
      if (!isCurrent()) return;
      const message = this.describeError(error);
      if (!this.createMode && error instanceof HttpErrorResponse && error.status === 409) await this.loadUsers();
      this.error.set(message);
    } finally {
      this.submitting.set(false);
    }
  }

  cancelCreation(): void {
    if (!this.pendingCreation() || this.temporaryPassword() || this.submitting()) return;
    this.creationGeneration++;
    this.pendingCreation.set(null);
    this.adminPassword.reset('');
    this.error.set('');
    this.success.set('Solicitud descartada. Si ya se procesó, verifica el listado antes de crearla de nuevo.');
  }

  closePasswordDialog(): void {
    const dialog = this.passwordDialog?.nativeElement;
    if (dialog?.open && typeof dialog.close === 'function') dialog.close();
    else dialog?.removeAttribute('open');
    this.creationGeneration++;
    this.temporaryPassword.set('');
    this.copyMessage.set('');
    this.pendingCreation.set(null);
    this.createForm.reset({ username: '', displayName: '', role: '' });
    this.dialogTrigger?.focus();
    this.dialogTrigger = undefined;
  }

  async copyTemporaryPassword(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.temporaryPassword());
      this.copyMessage.set('Contraseña copiada.');
    } catch {
      this.copyMessage.set('No fue posible copiar la contraseña. Selecciónala y cópiala manualmente.');
    }
  }

  nextPage(): void {
    if ((this.page() * this.pageSize()) >= this.total()) return;
    this.page.update((value) => value + 1);
    void this.loadUsers();
  }

  previousPage(): void {
    if (this.page() <= 1) return;
    this.page.update((value) => value - 1);
    void this.loadUsers();
  }

  private describeError(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const body = error.error && typeof error.error === 'object' ? error.error : {};
      const code = typeof (body as { code?: string }).code === 'string' ? (body as { code: string }).code : '';
      if (code === 'reauthentication_failed') return 'No fue posible confirmar la contraseña de administrador.';
      if (code === 'admin_reauthentication_required') return 'Confirma de nuevo tu contraseña de administrador.';
      if (code === 'username_conflict') return 'El nombre de usuario ya existe.';
      if (code === 'stale_management_version') return 'La cuenta cambió. Revisa los datos actualizados antes de reintentar.';
      if (code === 'self_management_forbidden') return 'No puedes modificar tu propia cuenta administrativa.';
      if (code === 'last_active_administrator') return 'Debe permanecer al menos un administrador activo.';
      if (code === 'reset_result_unavailable') return 'La contraseña temporal anterior no está disponible. Revisa el usuario actualizado y realiza una nueva solicitud.';
      if (code === 'user_inactive') return 'El usuario está inactivo. Actívalo antes de restablecer su contraseña.';
      if (code === 'idempotency_result_expired') return 'El resultado expiró y la contraseña temporal ya no se puede recuperar. Revisa el listado antes de continuar.';
      if (code === 'idempotency_key_reused') return 'Esta solicitud ya se utilizó con otros datos. Revisa el listado antes de continuar.';
      if (error.status === 409) return 'La solicitud entró en conflicto. Revisa los datos antes de reintentar.';
      if (error.status === 429) return 'Se alcanzó el límite de intentos. Inténtalo más tarde.';
      if (code === 'permission_denied') return 'No tienes permisos para gestionar usuarios.';
      if (code === 'authentication_required') return 'Debes iniciar sesión otra vez para continuar.';
      if (code === 'csrf_invalid') return 'No se pudo validar la solicitud. Vuelve a confirmar tu contraseña y reintenta sin recargar la página.';
      if (code === 'validation_failed') {
        const fields = (body as { fieldErrors?: unknown }).fieldErrors;
        if (fields && typeof fields === 'object' && !Array.isArray(fields)) {
          const errors = fields as Record<string, unknown>;
          const messages: string[] = [];
          if (Object.hasOwn(errors, 'username')) messages.push('El usuario debe tener de 3 a 64 caracteres: letras sin acentos, números, punto, guion o guion bajo.');
          if (Object.hasOwn(errors, 'displayName')) messages.push('El nombre visible debe tener de 1 a 120 caracteres sin caracteres de control.');
          if (Object.hasOwn(errors, 'role')) messages.push('Selecciona un rol válido del catálogo.');
          if (Object.hasOwn(errors, 'idempotencyKey')) messages.push('No se pudo validar el identificador de la solicitud.');
          if (messages.length) return messages.join(' ');
        }
        return 'La solicitud de administración no es válida.';
      }
      if (error.status === 403) return 'Tu perfil no puede gestionar usuarios.';
    }
    return 'No se pudo completar la operación de administración.';
  }
}
