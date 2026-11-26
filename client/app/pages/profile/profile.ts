import { I18nService } from '../../core/i18n/i18n.service';
import { Component, computed, inject } from '@angular/core';
import { AuthService } from '../../core/services/auth.service';
import { ChangePasswordPage } from '../change-password/change-password';

@Component({
  selector: 'app-profile-page',
  imports: [ChangePasswordPage],
  templateUrl: './profile.html',
  styleUrl: './profile.less',
})
export class ProfilePage {
  readonly i18n = inject(I18nService);
  get dictionary() { return this.i18n.dictionary(); }
  private readonly auth = inject(AuthService);

  readonly user = this.auth.user;
  readonly roleLabel = computed(() => {
    const role = this.user()?.role;
    const labels: Record<string, string> = {
      operador: this.dictionary.account.roleOperator,
      validador: this.dictionary.account.roleValidator,
      administrador: this.dictionary.account.roleAdministrator,
    };
    return role ? labels[role] ?? role : this.dictionary.account.roleUnavailable;
  });
  readonly createdAtLabel = computed(() => {
    const value = this.user()?.createdAt;
    if (!value) return this.dictionary.account.notAvailable;
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) return value;
    return new Intl.DateTimeFormat(this.i18n.locale(), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(parsed);
  });
}
