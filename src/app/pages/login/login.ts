import { HttpErrorResponse } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { safeReturnUrl } from '../../core/guards/auth.guard';
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
    username: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  constructor(
    private readonly auth: AuthService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
  ) {}
  get sessionUnavailable(): boolean {
    return this.auth.state() === 'unavailable';
  }
  async submit() {
    if (this.form.invalid || this.loading()) return;
    this.loading.set(true);
    this.error.set('');
    try {
      const { username, password } = this.form.getRawValue();
      await this.auth.login(username, password);
      const returnUrl = safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl'));
      await this.router.navigateByUrl(returnUrl ?? '/incidencias');
    } catch (error) {
      this.error.set(
        error instanceof HttpErrorResponse
          ? error.status === 401
            ? 'Usuario o contraseña incorrectos.'
            : error.status === 429
              ? 'Se alcanzó el límite de intentos. Espera un minuto antes de reintentar.'
              : error.status === 0
                ? 'No fue posible conectar con el servidor. Verifica la red e intenta de nuevo.'
                : 'No fue posible iniciar sesión. Intenta de nuevo.'
          : 'No fue posible iniciar sesión. Intenta de nuevo.',
      );
    } finally {
      this.loading.set(false);
    }
  }
}
