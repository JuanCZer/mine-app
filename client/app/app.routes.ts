import { Routes } from '@angular/router';
import { adminGuard, authGuard, guestGuard, passwordChangeGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'incidencias' },
  {
    path: '',
    loadComponent: () => import('./layout/app-layout').then((m) => m.AppLayout),
    canActivateChild: [authGuard, passwordChangeGuard],
    children: [
      {
        path: 'incidencias',
        data: { titleKey: 'account.incidentsTitle' },
        loadComponent: () => import('./pages/notes/notes').then((m) => m.NotesPage),
        canDeactivate: [(component: { canLeave: () => boolean }) => component.canLeave()],
      },
      {
        path: 'historial',
        data: { titleKey: 'account.historyTitle' },
        loadComponent: () => import('./pages/record/record').then((m) => m.RecordPage),
      },
      { path: 'record', pathMatch: 'full', redirectTo: 'historial' },
      {
        path: 'mapa/incidencia/:id',
        data: { titleKey: 'account.incidentDetailsTitle' },
        loadComponent: () => import('./pages/map-incident/map-incident').then((m) => m.MapIncidentPage),
      },
      {
        path: 'mapa',
        data: { titleKey: 'account.mapTitle' },
        loadComponent: () => import('./pages/map/map').then((m) => m.MapPage),
      },
      {
        path: 'cuenta/perfil',
        data: { titleKey: 'account.profileTitle' },
        loadComponent: () => import('./pages/profile/profile').then((m) => m.ProfilePage),
      },
      {
        path: 'cuenta/contrasena',
        data: { titleKey: 'account.changePassword' },
        loadComponent: () =>
          import('./pages/change-password/change-password').then((m) => m.ChangePasswordPage),
      },
      {
        path: 'administracion/usuarios/nuevo',
        data: { titleKey: 'account.createUser', mode: 'create' },
        canActivate: [adminGuard],
        canDeactivate: [(component: { canLeave: () => boolean }) => component.canLeave()],
        loadComponent: () =>
          import('./pages/admin-users/admin-users').then((m) => m.AdminUsersPage),
      },
      {
        path: 'administracion/usuarios',
        data: { titleKey: 'account.usersTitle' },
        canActivate: [adminGuard],
        canDeactivate: [(component: { canLeave: () => boolean }) => component.canLeave()],
        loadComponent: () =>
          import('./pages/admin-users/admin-users').then((m) => m.AdminUsersPage),
      },
    ],
  },
  { path: 'notas', pathMatch: 'full', redirectTo: 'incidencias' },
  { path: 'deteccion', pathMatch: 'full', redirectTo: 'incidencias' },
  { path: 'deteccion/:id', pathMatch: 'full', redirectTo: 'incidencias' },
  {
    path: 'login',
    data: { titleKey: 'account.loginTitle' },
    canActivate: [guestGuard],
    loadComponent: () => import('./pages/login/login').then((m) => m.LoginPage),
  },
  { path: '**', redirectTo: 'incidencias' },
];
