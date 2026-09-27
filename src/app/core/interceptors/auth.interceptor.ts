import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const token = auth.token;
  return next(token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401 && !request.url.endsWith('/auth/login')) void auth.expireSession();
      return throwError(() => error);
    }),
  );
};
