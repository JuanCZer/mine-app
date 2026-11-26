import { I18nService, Message } from '../../core/i18n/i18n.service';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
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
  readonly i18n = inject(I18nService);
  get dictionary() { return this.i18n.dictionary(); }
  readonly loading = signal(false);
  readonly error = this.i18n.message();

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
        ? { key: 'account.passwordLength' }
        : { key: 'account.passwordRequired' });
      return;
    }

    const { currentPassword, newPassword, confirmation } = this.form.getRawValue();

    if (newPassword !== confirmation) {
      this.error.set({ key: 'account.passwordConfirmation' });
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

  private describeError(error: unknown): Message {
    if (!(error instanceof HttpErrorResponse)) {
      return { key: 'account.passwordError' };
    }

    const body = error.error && typeof error.error === 'object' ? error.error : {};
    const code = typeof (body as { code?: string }).code === 'string' ? (body as { code: string }).code : '';

    switch (code) {
      case 'current_password_invalid':
        return { key: 'account.currentPasswordInvalid' };
      case 'password_reuse':
        return { key: 'account.passwordReuse' };
      case 'csrf_invalid':
        return { key: 'account.passwordCsrf' };
      case 'validation_failed':
        return { key: 'account.passwordRules' };
      case 'authentication_required':
        return { key: 'account.passwordAuthentication' };
      default:
        return error.status === 413
          ? { key: 'account.formTooLarge' }
          : error.status === 429
            ? { key: 'account.passwordRateLimit' }
            : error.status === 0
              ? { key: 'account.networkError' }
              : { key: 'account.passwordError' };
    }
  }
}
