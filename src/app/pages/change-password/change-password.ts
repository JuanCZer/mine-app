import { HttpErrorResponse } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';

const passwordLength: ValidatorFn = (control) => {
  const value = control.value as string;
  if (!value) return null;
  const length = Array.from(value).length;
  return length >= 15 && length <= 128 ? null : { passwordLength: true };
};

@Component({
  selector: 'app-change-password',
  imports: [ReactiveFormsModule],
  templateUrl: './change-password.html',
  styleUrl: './change-password.less',
})
export class ChangePasswordPage {
  readonly loading = signal(false);
  readonly error = signal('');

  readonly form = new FormGroup({
    currentPassword: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required],
    }),
    newPassword: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, passwordLength],
    }),
    confirmation: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required],
    }),
  });

  constructor(
    private readonly auth: AuthService,
    private readonly router: Router,
  ) {}

  async submit(): Promise<void> {
    if (this.loading()) return;

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set(this.form.controls.newPassword.hasError('passwordLength')
        ? 'La contraseña debe tener entre 15 y 128 caracteres.'
        : 'Completa todos los campos para cambiar la contraseña.');
      return;
    }

    const { currentPassword, newPassword, confirmation } = this.form.getRawValue();

    if (newPassword !== confirmation) {
      this.error.set('La confirmación debe coincidir exactamente con la nueva contraseña.');
      this.form.controls.confirmation.markAsTouched();
      return;
    }

    this.loading.set(true);
    this.error.set('');

    try {
      await this.auth.changePassword(currentPassword, newPassword, confirmation);
    } catch (error) {
      this.error.set(this.describeError(error));
    } finally {
      this.loading.set(false);
    }
  }

  private describeError(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'No se pudo cambiar la contraseña. Inténtalo de nuevo.';
    }

    const body = error.error && typeof error.error === 'object' ? error.error : {};
    const code = typeof (body as { code?: string }).code === 'string' ? (body as { code: string }).code : '';

    switch (code) {
      case 'current_password_invalid':
        return 'La contraseña actual no coincide con la que tienes registrada.';
      case 'password_reuse':
        return 'La nueva contraseña ya se ha utilizado antes. Elige otra distinta.';
      case 'csrf_invalid':
        return 'La sesión ha caducado o el formulario ya no es válido. Vuelve a intentarlo.';
      case 'validation_failed':
        return 'La nueva contraseña no cumple las reglas del sistema.';
      case 'authentication_required':
        return 'Necesitas iniciar sesión para cambiar la contraseña.';
      default:
        return error.status === 413
          ? 'El formulario supera el tamaño permitido para esta operación.'
          : error.status === 429
            ? 'Se ha alcanzado el límite de intentos. Espera antes de volver a intentarlo.'
            : error.status === 0
              ? 'No fue posible conectar con el servidor. Verifica la red e intenta de nuevo.'
              : 'No se pudo cambiar la contraseña. Inténtalo de nuevo.';
    }
  }
}
