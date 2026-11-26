import { I18nService } from '../../core/i18n/i18n.service';
import { Title } from '@angular/platform-browser';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, signal } from '@angular/core';
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
  readonly i18n = inject(I18nService);
  get dictionary() { return this.i18n.dictionary(); }
  private readonly title = inject(Title);
  readonly loading = signal(false);
  readonly error = this.i18n.message();
  readonly form = new FormGroup({
    username: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  constructor(
    private readonly auth: AuthService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
  ) {
    effect(() => this.title.setTitle(this.dictionary.account.loginTitle));
  }
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
            ? { key: 'account.incorrectCredentials' }
            : error.status === 429
              ? { key: 'account.loginRateLimit' }
              : error.status === 0
                ? { key: 'account.networkError' }
                : { key: 'account.loginError' }
          : { key: 'account.loginError' },
      );
    } finally {
      this.loading.set(false);
    }
  }
}
