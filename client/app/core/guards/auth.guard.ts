import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

export function safeReturnUrl(value: string | null | undefined): string | null {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) return null;
  let decoded: string;
  try {
    decoded = decodeURIComponent(value);
  } catch {
    return null;
  }
  if (decoded.startsWith('//') || decoded.includes('\\') || /[\u0000-\u001f]/.test(decoded)) return null;
  const path = decoded.split(/[?#]/, 1)[0].replace(/\/+$/, '') || '/';
  return path === '/login' ? null : value;
}

export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const path = state.url.split(/[?#]/, 1)[0].replace(/\/+$/, '');
  if (await auth.ensureSession(path === '/cuenta/perfil')) return true;
  const returnUrl = safeReturnUrl(state.url);
  return router.createUrlTree(['/login'], {
    queryParams: returnUrl ? { returnUrl } : undefined,
  });
};

export const adminGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!(await auth.ensureSession(true))) {
    const returnUrl = safeReturnUrl(state.url);
    return router.createUrlTree(['/login'], {
      queryParams: returnUrl ? { returnUrl } : undefined,
    });
  }
  const user = auth.user();
  return user?.role === 'administrador' && (user.permissions ?? []).includes('users:provision')
    ? true
    : router.createUrlTree(['/incidencias']);
};

export const passwordChangeGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!(await auth.ensureSession())) {
    const returnUrl = safeReturnUrl(state.url);
    return router.createUrlTree(['/login'], {
      queryParams: returnUrl ? { returnUrl } : undefined,
    });
  }
  const user = auth.user();
  if (!user?.mustChangePassword) return true;
  if (state.url.startsWith('/cuenta/contrasena')) return true;
  return router.createUrlTree(['/cuenta/contrasena']);
};

export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return (await auth.ensureSession())
    ? router.createUrlTree(['/incidencias'])
    : true;
};
