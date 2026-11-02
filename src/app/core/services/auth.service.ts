import { HttpClient } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiFailureKind, ApiError, AuthState, LoginResponse, User } from '../models/auth.model';

const API_URL = '/api/auth';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly userState = signal<User | null>(null);
  private readonly authState = signal<AuthState>('unknown');
  private readonly failureState = signal<ApiFailureKind | null>(null);
  private sessionRequest?: Promise<boolean>;
  private sessionGeneration = 0;

  readonly user = this.userState.asReadonly();
  readonly state = this.authState.asReadonly();
  readonly lastFailure = this.failureState.asReadonly();
  readonly authenticated = computed(
    () => this.authState() === 'authenticated' && this.userState() !== null,
  );

  constructor(
    private readonly http: HttpClient,
    private readonly router: Router,
  ) {
    this.removeLegacySession();
  }

  ensureSession(force = false): Promise<boolean> {
    if (!force) {
      if (this.authenticated()) return Promise.resolve(true);
      if (this.authState() === 'anonymous') return Promise.resolve(false);
    }
    if (this.sessionRequest) return this.sessionRequest;

    const generation = this.sessionGeneration;
    const request = firstValueFrom(this.http.get<User>(`${API_URL}/me`))
      .then((user) => {
        if (generation !== this.sessionGeneration) return this.authenticated();
        this.setAuthenticated(user);
        return true;
      })
      .catch((error: unknown) => {
        if (generation !== this.sessionGeneration) return this.authenticated();
        this.userState.set(null);
        this.authState.set(this.stateAfterFailure(error));
        return false;
      })
      .finally(() => {
        if (this.sessionRequest === request) this.sessionRequest = undefined;
      });
    this.sessionRequest = request;
    return request;
  }

  async login(username: string, password: string): Promise<void> {
    this.invalidateSessionRequest();
    try {
      await firstValueFrom(this.http.get<void>(`${API_URL}/csrf`));
      const response = await firstValueFrom(
        this.http.post<LoginResponse>(`${API_URL}/login`, { username, password }),
      );
      this.setAuthenticated(response.user);
    } catch (error) {
      this.userState.set(null);
      this.authState.set(this.stateAfterFailure(error));
      throw error;
    }
  }

  async reauthenticate(password: string): Promise<void> {
    await firstValueFrom(this.http.get<void>(`${API_URL}/csrf`));
    await firstValueFrom(this.http.post<void>(`${API_URL}/reauth`, { password }));
    const currentUser = this.userState();
    if (currentUser) {
      this.setAuthenticated({ ...currentUser, mustChangePassword: currentUser.mustChangePassword });
    }
  }

  async changePassword(currentPassword: string, newPassword: string, confirmation: string): Promise<void> {
    await firstValueFrom(this.http.get<void>(`${API_URL}/csrf`));
    await firstValueFrom(
      this.http.post<void>(`${API_URL}/change-password`, {
        currentPassword,
        newPassword,
        confirmation,
      }),
    );
    await this.expireSession({ preserveReturnUrl: false });
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.get<void>(`${API_URL}/csrf`));
    try {
      await firstValueFrom(this.http.post<void>(`${API_URL}/logout`, {}));
    } catch (error) {
      if (!this.isStatus(error, 401)) throw error;
    }
    await this.expireSession();
  }

  async expireSession(options: { preserveReturnUrl?: boolean } = {}): Promise<void> {
    this.invalidateSessionRequest();
    this.userState.set(null);
    this.authState.set('anonymous');
    const current = this.router.url;
    const preserveReturnUrl = options.preserveReturnUrl ?? true;
    const queryParams = preserveReturnUrl && current && current !== '/' && !current.startsWith('/login')
      ? { returnUrl: current }
      : undefined;
    await this.router.navigate(['/login'], { queryParams });
  }

  handleApiFailure(kind: ApiFailureKind, requestUrl: string, error?: unknown): void {
    this.failureState.set(kind);
    if (kind === 'unauthorized' && !requestUrl.startsWith(`${API_URL}/`)) {
      void this.expireSession();
    }
    if (kind === 'forbidden') {
      const apiError = this.parseError(error);
      if (apiError?.code === 'password_change_required' && this.router.url !== '/cuenta/contrasena') {
        void this.router.navigateByUrl('/cuenta/contrasena');
      }
    }
  }

  private invalidateSessionRequest(): void {
    this.sessionGeneration++;
    this.sessionRequest = undefined;
  }

  private setAuthenticated(user: User): void {
    this.userState.set(user);
    this.authState.set('authenticated');
    this.failureState.set(null);
  }

  private parseError(error: unknown): ApiError | null {
    if (!error || typeof error !== 'object') return null;
    const candidate = error as { error?: unknown; status?: number; body?: { code?: string } };
    const body = candidate.body ?? (candidate as { error?: { code?: string } }).error;
    if (!body || typeof body !== 'object') return null;
    const code = (body as { code?: string }).code;
    if (!code) return null;
    return {
      error: String((body as { error?: string }).error ?? ''),
      code: code as ApiError['code'],
      fieldErrors: (body as { fieldErrors?: Record<string, string[]> }).fieldErrors,
      traceId: (body as { traceId?: string }).traceId,
    };
  }

  private isStatus(error: unknown, status: number): boolean {
    return typeof error === 'object' && error !== null && 'status' in error && error.status === status;
  }

  private stateAfterFailure(error: unknown): AuthState {
    if (typeof error !== 'object' || error === null || !('status' in error)) return 'unavailable';
    const status = Number(error.status);
    return status === 0 || status >= 500 ? 'unavailable' : 'anonymous';
  }

  private removeLegacySession(): void {
    try {
      localStorage.removeItem('notes_token');
      localStorage.removeItem('notes_user');
    } catch {
      // The cookie-backed session does not depend on browser storage.
    }
  }
}
