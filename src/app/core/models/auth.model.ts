export interface User { id: string; username: string; displayName: string; }
export interface LoginResponse { token: string; user: User; }
