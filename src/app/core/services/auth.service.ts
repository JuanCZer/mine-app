import { HttpClient } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { LoginResponse, User } from '../models/auth.model';

const API_URL = 'http://localhost:5068/api';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly tokenKey = 'notes_token';
  private readonly userKey = 'notes_user';
  private readonly userState = signal<User | null>(this.readUser());
  readonly user = this.userState.asReadonly();
  readonly authenticated = computed(() => !!this.token && !!this.userState());

  constructor(private readonly http: HttpClient, private readonly router: Router) {}
  get token(): string | null { return localStorage.getItem(this.tokenKey); }

  async login(username: string, password: string): Promise<void> {
    const response = await firstValueFrom(this.http.post<LoginResponse>(`${API_URL}/auth/login`, { username, password }));
    localStorage.setItem(this.tokenKey, response.token);
    localStorage.setItem(this.userKey, JSON.stringify(response.user));
    this.userState.set(response.user);
  }

  async logout(): Promise<void> {
    try { await firstValueFrom(this.http.post(`${API_URL}/auth/logout`, {})); } catch { /* cierre local */ }
    await this.expireSession();
  }

  async expireSession(): Promise<void> {
    localStorage.removeItem(this.tokenKey);
    localStorage.removeItem(this.userKey);
    this.userState.set(null);
    await this.router.navigate(['/login']);
  }

  private readUser(): User | null {
    try { return JSON.parse(localStorage.getItem(this.userKey) ?? 'null'); } catch { return null; }
  }
}
