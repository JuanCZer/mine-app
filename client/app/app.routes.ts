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
        title: 'Incidencias · 4 fases',
        loadComponent: () => import('./pages/notes/notes').then((m) => m.NotesPage),
        canDeactivate: [(component: { canLeave: () => boolean }) => component.canLeave()],
      },
      {
        path: 'historial',
        title: 'Historial de incidencias',
        loadComponent: () => import('./pages/record/record').then((m) => m.RecordPage),
      },
      { path: 'record', pathMatch: 'full', redirectTo: 'historial' },
      {
        path: 'mapa/incidencia/:id',
        title: 'Ficha de incidencia',
        loadComponent: () => import('./pages/map-incident/map-incident').then((m) => m.MapIncidentPage),
      },
      {
        path: 'mapa',
        title: 'Mapa de incidencias',
        loadComponent: () => import('./pages/map/map').then((m) => m.MapPage),
      },
      {
        path: 'cuenta/perfil',
        title: 'Perfil de usuario',
        loadComponent: () => import('./pages/profile/profile').then((m) => m.ProfilePage),
      },
      {
        path: 'cuenta/contrasena',
        title: 'Cambiar contraseña',
        loadComponent: () =>
          import('./pages/change-password/change-password').then((m) => m.ChangePasswordPage),
      },
      {
        path: 'administracion/usuarios/nuevo',
        title: 'Crear usuario',
        canActivate: [adminGuard],
        canDeactivate: [(component: { canLeave: () => boolean }) => component.canLeave()],
        data: { mode: 'create' },
        loadComponent: () =>
          import('./pages/admin-users/admin-users').then((m) => m.AdminUsersPage),
      },
      {
        path: 'administracion/usuarios',
        title: 'Administración de usuarios',
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
    title: 'Acceso',
    canActivate: [guestGuard],
    loadComponent: () => import('./pages/login/login').then((m) => m.LoginPage),
  },
  { path: '**', redirectTo: 'incidencias' },
];
