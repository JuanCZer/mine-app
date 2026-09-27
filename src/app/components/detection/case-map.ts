import {
  AfterViewInit,
  Component,
  ElementRef,
  OnDestroy,
  ViewChild,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import mapboxgl from 'mapbox-gl';
import { MAPBOX_TOKEN } from '../../core/config/mapbox.config';
import { DetectionCase, PRIORITY_LABELS, stageLabel } from '../../core/models/detection.model';

@Component({
  selector: 'app-case-map',
  template: `
    <section aria-label="Mapa de expedientes">
      <div class="heading">
        <div>
          <span>VISTA GEOGRÁFICA</span>
          <h3>Ubicaciones registradas</h3>
        </div>
        <small>{{ cases().length }} puntos</small>
      </div>
      <div
        #mapContainer
        class="map"
        role="region"
        aria-label="Mapa de ubicaciones georreferenciadas"
      ></div>
      @if (error()) {
        <p class="error" role="status">
          {{ error() }} Las coordenadas siguen disponibles en la tabla de expedientes.
        </p>
      }
      <div class="legend">
        <span><i class="high"></i> Alta</span><span><i class="medium"></i> Media</span
        ><span><i class="low"></i> Baja</span
        ><small>Prioridad declarada · no representa áreas certificadas</small>
      </div>
    </section>
  `,
  styles: `
    section {
      border: 1px solid #dfe5d8;
      border-radius: 12px;
      overflow: hidden;
      background: #fff;
    }
    .heading {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 21px 24px;
    }
    .heading span {
      color: #89927e;
      font-size: 8px;
      letter-spacing: 0.16em;
    }
    h3 {
      color: #33452e;
      font-size: 14px;
      margin: 5px 0 0;
    }
    small {
      color: #889180;
      font-size: 10px;
    }
    .map {
      height: 340px;
      background: #e8ede3;
    }
    .legend {
      padding: 14px 24px;
      display: flex;
      align-items: center;
      gap: 18px;
      font-size: 10px;
      color: #718063;
      flex-wrap: wrap;
    }
    .legend small {
      margin-left: auto;
    }
    i {
      display: inline-block;
      width: 7px;
      height: 7px;
      border-radius: 50%;
      margin-right: 5px;
    }
    .high {
      background: #b35843;
    }
    .medium {
      background: #bd9441;
    }
    .low {
      background: #6c8a52;
    }
    .error {
      padding: 10px 24px;
      font-size: 11px;
      color: #926736;
      line-height: 1.6;
    }
    section { border-color: var(--line); background: var(--surface); }.heading span, small { color: var(--text-muted); }.heading span { font-size: 10px; font-weight: 700; }.heading h3 { color: var(--text); font-size: 16px; }.map { background: var(--surface-input); }.legend { border-top: 1px solid var(--line); color: var(--text-secondary); font-size: 12px; }.high { background: var(--danger); }.medium { background: var(--warning); }.low { background: var(--success); }.error { color: #ffd28e; }
  `,
})
export class CaseMap implements AfterViewInit, OnDestroy {
  @ViewChild('mapContainer') container!: ElementRef<HTMLElement>;
  readonly cases = input<DetectionCase[]>([]);
  readonly opened = output<string>();
  readonly error = signal('');
  private map?: mapboxgl.Map;
  private markers: mapboxgl.Marker[] = [];
  constructor() {
    effect(() => {
      const items = this.cases();
      if (this.map?.isStyleLoaded()) this.draw(items);
    });
  }
  ngAfterViewInit() {
    try {
      if (!MAPBOX_TOKEN) throw new Error('Token no configurado');
      this.map = new mapboxgl.Map({
        container: this.container.nativeElement,
        accessToken: MAPBOX_TOKEN,
        style: 'mapbox://styles/mapbox/dark-v11',
        center: [-102.5, 23.6],
        zoom: 4,
        attributionControl: true,
      });
      this.map.addControl(new mapboxgl.NavigationControl(), 'top-right');
      this.map.on('load', () => {
        this.error.set('');
        this.draw(this.cases());
      });
      this.map.on('error', () =>
        this.error.set(
          'No se pudo cargar parte del mapa. Revisa la conexión y la configuración de Mapbox.',
        ),
      );
    } catch {
      this.error.set('El mapa no está disponible en este navegador o falta su configuración.');
    }
  }
  ngOnDestroy() {
    this.markers.forEach((marker) => marker.remove());
    this.map?.remove();
  }
  private draw(items: DetectionCase[]) {
    if (!this.map) return;
    this.markers.forEach((marker) => marker.remove());
    this.markers = [];
    const bounds = new mapboxgl.LngLatBounds();
    for (const item of items) {
      const popupContent = document.createElement('div');
      const title = document.createElement('strong');
      title.textContent = item.title;
      const info = document.createElement('p');
      info.textContent = `${stageLabel(item.stage)} · Prioridad ${PRIORITY_LABELS[item.priority].toLowerCase()}`;
      const link = document.createElement('button');
      link.type = 'button';
      link.textContent = 'Abrir expediente →';
      link.addEventListener('click', () => this.opened.emit(item.id));
      popupContent.append(title, info, link);
      const color = { high: '#f07878', medium: '#f0bb62', low: '#58c48d' }[item.priority];
      const marker = new mapboxgl.Marker({ color })
        .setLngLat([item.longitude, item.latitude])
        .setPopup(new mapboxgl.Popup({ offset: 20 }).setDOMContent(popupContent))
        .addTo(this.map);
      this.markers.push(marker);
      bounds.extend([item.longitude, item.latitude]);
    }
    if (items.length) this.map.fitBounds(bounds, { padding: 60, maxZoom: 11, duration: 0 });
  }
}
