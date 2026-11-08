import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AdminRoleOption,
  AdminUserPage,
  AdminUserQuery,
  AdminUserSummary,
  ChangeRoleCommand,
  ChangeStatusCommand,
  CreateAdminUserCommand,
  CreateAdminUserResponse,
} from '../models/user-administration.model';

@Injectable({ providedIn: 'root' })
export class UserAdministrationService {
  constructor(private readonly http: HttpClient) {}

  listUsers(query: AdminUserQuery): Observable<AdminUserPage> {
    const params: Record<string, string> = {};
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== '') params[key] = String(value);
    }
    return this.http.get<AdminUserPage>('/api/admin/users', { params });
  }

  listRoles(): Observable<AdminRoleOption[]> {
    return this.http.get<AdminRoleOption[]>('/api/admin/roles');
  }

  createUser(
    cmd: CreateAdminUserCommand,
    key: string,
  ): Observable<HttpResponse<CreateAdminUserResponse>> {
    return this.http.post<CreateAdminUserResponse>('/api/admin/users', cmd, {
      headers: { 'Idempotency-Key': key },
      observe: 'response',
    });
  }

  resetPassword(id: string, expectedManagementVersion: string, key: string): Observable<{ temporaryPassword: string }> {
    return this.http.post<{ temporaryPassword: string }>(`/api/admin/users/${encodeURIComponent(id)}/password-reset`,
      { expectedManagementVersion }, { headers: { 'Idempotency-Key': key } });
  }

  changeRole(id: string, cmd: ChangeRoleCommand): Observable<AdminUserSummary> {
    return this.http.put<AdminUserSummary>(`/api/admin/users/${id}/role`, cmd);
  }

  changeStatus(id: string, cmd: ChangeStatusCommand): Observable<AdminUserSummary> {
    return this.http.put<AdminUserSummary>(`/api/admin/users/${id}/status`, cmd);
  }
}
