export type AuthState = 'unknown' | 'authenticated' | 'anonymous' | 'unavailable';

export type ApiFailureKind =
  | 'unauthorized'
  | 'forbidden'
  | 'rate-limited'
  | 'network'
  | 'server';

export type ApiErrorCode =
  | 'validation_failed'
  | 'authentication_required'
  | 'permission_denied'
  | 'csrf_invalid'
  | 'current_password_invalid'
  | 'password_reuse'
  | 'password_change_required'
  | 'admin_reauthentication_required'
  | 'reauthentication_failed'
  | 'credential_state_changed'
  | 'username_conflict'
  | 'stale_management_version'
  | 'rate_limited'
  | 'service_unavailable'
  | 'idempotency_key_reused';

export interface ApiError {
  error: string;
  code: ApiErrorCode;
  fieldErrors?: Record<string, string[]>;
  traceId?: string;
}

export interface User {
  id: string;
  username: string;
  displayName: string;
  role?: string | null;
  createdAt?: string | null;
  permissions: string[];
  mustChangePassword: boolean;
}

export interface LoginResponse {
  user: User;
}
