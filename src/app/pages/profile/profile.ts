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
  private readonly auth = inject(AuthService);

  readonly user = this.auth.user;
  readonly roleLabel = computed(() => this.user()?.role || 'Rol no disponible');
  readonly createdAtLabel = computed(() => {
    const value = this.user()?.createdAt;
    if (!value) return 'No disponible';
    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) return value;
    return new Intl.DateTimeFormat('es-MX', {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(parsed);
  });
}
