import { Component, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

@Component({
  selector: 'app-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app-layout.html',
  styleUrl: './app-layout.less',
})
export class AppLayout {
  menuOpen = false;
  readonly pageTitle = signal('Incidencias');
  readonly isDetection = signal(false);
  constructor(router: Router) {
    const update = (url: string) => {
      this.isDetection.set(url.startsWith('/deteccion'));
      this.pageTitle.set(
        url.startsWith('/deteccion/')
          ? 'Expediente de detección'
          : url.startsWith('/deteccion')
            ? 'Centro de detección'
            : url.startsWith('/mapa')
              ? 'Mapa de incidencias'
              : url.startsWith('/historial')
                ? 'Historial de incidencias'
                : 'Incidencias · 4 fases',
      );
    };
    update(router.url);
    router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((event) => update(event.urlAfterRedirects));
  }
}
