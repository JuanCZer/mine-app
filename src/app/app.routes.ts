import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'incidencias' },
  {
    path: '',
    loadComponent: () => import('./layout/app-layout').then((m) => m.AppLayout),
    children: [
      {
        path: 'deteccion',
        title: 'Centro de detección',
        loadComponent: () => import('./pages/detection/detection').then((m) => m.DetectionPage),
      },
      {
        path: 'deteccion/:id',
        title: 'Expediente de detección',
        loadComponent: () => import('./pages/detection/case-detail').then((m) => m.CaseDetailPage),
        canDeactivate: [
          (component: { dirty: () => boolean }) =>
            !component.dirty() ||
            confirm('Hay cambios de revisión sin guardar. ¿Salir del expediente?'),
        ],
      },
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
        path: 'mapa',
        title: 'Mapa de incidencias',
        loadComponent: () => import('./pages/map/map').then((m) => m.MapPage),
      },
    ],
  },
  { path: 'notas', pathMatch: 'full', redirectTo: 'incidencias' },
  { path: 'login', redirectTo: 'incidencias' },
  { path: '**', redirectTo: 'incidencias' },
];
