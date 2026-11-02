import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ApiFailureKind } from '../models/auth.model';
import { AuthService } from '../services/auth.service';

export function apiFailureKind(error: HttpErrorResponse): ApiFailureKind {
  if (error.status === 0) return 'network';
  if (error.status === 401) return 'unauthorized';
  if (error.status === 403) return 'forbidden';
  if (error.status === 429) return 'rate-limited';
  return 'server';
}

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  if (!(request.url === '/api' || request.url.startsWith('/api/'))) return next(request);
  const auth = inject(AuthService);
  return next(request).pipe(
    catchError((error: HttpErrorResponse) => {
      auth.handleApiFailure(apiFailureKind(error), request.url, error);
      return throwError(() => error);
    }),
  );
};
