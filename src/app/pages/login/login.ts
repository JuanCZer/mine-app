import { HttpErrorResponse } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule],
  templateUrl: './login.html',
  styleUrl: './login.less',
})
export class LoginPage {
  readonly loading = signal(false);
  readonly error = signal('');
  readonly form = new FormGroup({
    username: new FormControl('demo', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('Demo123!', { nonNullable: true, validators: [Validators.required] }),
  });
  constructor(
    private readonly auth: AuthService,
    private readonly router: Router,
  ) {}
  async submit() {
    if (this.form.invalid || this.loading()) return;
    this.loading.set(true);
    this.error.set('');
    try {
      const { username, password } = this.form.getRawValue();
      await this.auth.login(username, password);
      await this.router.navigate(['/incidencias']);
    } catch (error) {
      this.error.set(
        error instanceof HttpErrorResponse && error.status === 401
          ? 'Usuario o contraseña incorrectos.'
          : 'No fue posible conectar con el servidor.',
      );
    } finally {
      this.loading.set(false);
    }
  }
}
