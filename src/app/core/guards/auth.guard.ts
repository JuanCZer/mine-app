import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.authenticated() ? true : inject(Router).createUrlTree(['/login']);
};
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.authenticated() ? inject(Router).createUrlTree(['/incidencias']) : true;
};
