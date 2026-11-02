export type AdminUserStatus = 'active' | 'inactive';

export interface AdminUserSummary {
  id: string;
  username: string;
  displayName: string;
  role: string;
  isActive: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  updatedAt: string;
  managementVersion: string;
}

export interface AdminUserQuery {
  q?: string;
  page?: number;
  pageSize?: number;
  role?: string;
  status?: AdminUserStatus;
}

export interface AdminUserPage {
  items: AdminUserSummary[];
  page: number;
  pageSize: number;
  total: number;
}

export interface AdminRoleOption {
  code: string;
  label: string;
}

export interface CreateAdminUserCommand {
  username: string;
  displayName: string;
  role: string;
}

export interface CreateAdminUserResponse {
  user: AdminUserSummary;
  temporaryPassword: string;
}

export interface ChangeRoleCommand {
  role: string;
  expectedManagementVersion: string;
}

export interface ChangeStatusCommand {
  isActive: boolean;
  expectedManagementVersion: string;
}
