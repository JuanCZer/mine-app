import { Component, ElementRef, OnDestroy, OnInit, ViewChild, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import mapboxgl from 'mapbox-gl';
import { firstValueFrom } from 'rxjs';
import { MAPBOX_TOKEN } from '../../core/config/mapbox.config';
import { MapBounds, MapIncident, MapSector, MapService } from '../../core/services/map.service';
import { observeMapResize } from '../../shared/map-resize';
import { buildProximityCircle, getSectorColor } from './map-sector.util';

const DETAIL_ZOOM = 8;
const MAX_VISIBLE_CIRCLES = 1000;
const MAX_VISIBLE_INCIDENTS = 1000;
const MAX_INCIDENT_PAGES = 4;

// Conservative spherical bounding box: include circles whose centres are up to
// one kilometre outside the viewport, including wrapped longitudes and poles.
export function isCircleNearBounds(latitude: number, longitude: number, bounds: { south: number; north: number; west: number; east: number }): boolean {
  const angular = 1000 / 6371000;
  const latitudeMargin = angular * 180 / Math.PI;
  if (latitude < bounds.south - latitudeMargin || latitude > bounds.north + latitudeMargin) return false;
  const extremeLatitude = Math.min(90, Math.max(Math.abs(bounds.south), Math.abs(bounds.north)) + latitudeMargin);
  const longitudeMargin = extremeLatitude >= 90 ? 180 : Math.asin(Math.min(1, Math.sin(angular) / Math.cos(extremeLatitude * Math.PI / 180))) * 180 / Math.PI;
  if (bounds.east - bounds.west + 2 * longitudeMargin >= 360) return true;
  const west = bounds.west - longitudeMargin;
  const east = bounds.east + longitudeMargin;
  const wrappedLongitude = longitude + 360 * Math.ceil((west - longitude) / 360);
  return wrappedLongitude <= east;
}
export function groupIncidents(items: MapIncident[]): MapIncident[][] {
  const groups = new Map<string, MapIncident[]>();
  for (const item of items) {
    const key = `${item.latitude}:${item.longitude}`;
    const group = groups.get(key) ?? [];
    group.push(item); groups.set(key, group);
  }
  return [...groups.values()];
}
export function incidentMarkerButton(items: MapIncident[]): HTMLButtonElement {
  const first = items[0];
  const button = document.createElement('button'); button.type = 'button'; button.className = 'incident-marker';
  button.setAttribute('aria-label', items.length === 1 ? `Ver incidencia ${first.title || first.id}` : `Ver ${items.length} incidencias en esta ubicación`);
  return button;
}
export function incidentPopup(items: MapIncident[]): HTMLElement {
  const node = document.createElement('div');
  for (const incident of items) {
    const title = document.createElement('strong'); title.textContent = incident.title || 'Incidencia sin identificar'; node.append(title);
    const state = document.createElement('small'); state.textContent = incident.state || 'Área sin identificar'; node.append(state);
    const content = document.createElement('p'); content.textContent = (incident.content || '').slice(0, 120); node.append(content);
    const link = document.createElement('a'); link.href = `/mapa/incidencia/${encodeURIComponent(incident.id)}`; link.textContent = `Ver ficha: ${incident.title || incident.id}`; node.append(link);
  }
  return node;
}
@Component({ selector: 'app-map', imports: [RouterLink], templateUrl: './map.html', styleUrl: './map.less' })
export class MapPage implements OnInit, OnDestroy {
  @ViewChild('mapContainer') private mapContainer?: ElementRef<HTMLElement>;
  readonly loading = signal(true);
  readonly totalIncidents = signal<number | null>(null);
  readonly error = signal('');
  readonly sectorError = signal('');
  readonly pointError = signal('');
  readonly mapError = signal('');
  readonly densityWarning = signal('');
  private map?: mapboxgl.Map;
  private markers: mapboxgl.Marker[] = [];
  private stopResizeObservation?: () => void;
  private generation = 0;
  private sectorGeneration = 0;
  private destroyed = false;
  private sectors: MapSector[] | null = null;
  private drawingSectors = false;
  private sectorsDirty = false;
  private readonly requestedNoteId: string | null;
  constructor(private readonly service: MapService, route: ActivatedRoute) {
    this.requestedNoteId = route.snapshot.queryParamMap.get('note');
  }
  async ngOnInit() {
    await this.retrySectors();
    this.loading.set(false); setTimeout(() => { if (!this.destroyed) void this.initializeMap(); });
  }
  async retrySectors() {
    const generation = ++this.sectorGeneration;
    this.sectorError.set('');
    try {
      const result = await firstValueFrom(this.service.sectors());
      if (this.destroyed || generation !== this.sectorGeneration) return;
      this.sectors = result.sectors;
      this.totalIncidents.set(result.total);
      this.sectorsDirty = true;
      if (this.map?.isStyleLoaded()) this.drawSectors();
    } catch {
      if (!this.destroyed && generation === this.sectorGeneration) this.sectorError.set(this.sectors === null
        ? 'No fue posible cargar los sectores.'
        : 'No fue posible actualizar los sectores. Los datos visibles pueden estar desactualizados.');
    }
  }
  ngOnDestroy() {
    this.destroyed = true; this.generation++; this.sectorGeneration++;
    this.stopResizeObservation?.(); this.clearMarkers(); this.map?.remove();
  }
  private async initializeMap() {
    if (!this.mapContainer) return;
    let center: [number, number] = [-99.1332, 19.4326];
    let zoom = 4.5;
    if (this.requestedNoteId) {
      try {
        const incident = await firstValueFrom(this.service.detail(this.requestedNoteId));
        if (incident.longitude != null && incident.altitude != null) { center = [incident.longitude, incident.altitude]; zoom = 12; }
        else this.error.set('La incidencia solicitada no tiene coordenadas.');
      } catch { this.error.set('No fue posible encontrar la incidencia solicitada.'); }
    }
    if (this.destroyed) return;
    mapboxgl.accessToken = MAPBOX_TOKEN;
    try { this.map = new mapboxgl.Map({ container: this.mapContainer.nativeElement, style: 'mapbox://styles/mapbox/dark-v11', center, zoom }); }
    catch { this.mapError.set('No fue posible iniciar Mapbox. Verifica su configuración.'); return; }
    this.stopResizeObservation = observeMapResize(this.mapContainer.nativeElement, () => this.map?.resize());
    this.map.addControl(new mapboxgl.NavigationControl(), 'top-right');
    this.map.on('load', () => { this.drawSectors(); void this.refreshMarkers(); });
    this.map.on('error', () => this.mapError.set('No fue posible cargar el mapa. Verifica la conexión y la configuración de Mapbox.'));
    this.map.on('styledata', () => this.onStyleData());
    this.map.on('idle', () => this.onMapIdle());
    this.map.on('moveend', () => { this.sectorsDirty = true; if (this.map?.isStyleLoaded()) this.drawSectors(); void this.refreshMarkers(); });
  }
  private onStyleData() { if (this.map?.isStyleLoaded()) this.drawSectors(); }
  private onMapIdle() { if (this.map?.isStyleLoaded()) { this.drawSectors(); if (this.map.areTilesLoaded()) this.mapError.set(''); } }
  private drawSectors() {
    if (!this.map || this.sectors === null || this.drawingSectors) return;
    const existing = this.map.getSource('sector-counts') as mapboxgl.GeoJSONSource | undefined;
    if (existing && !this.sectorsDirty) return;
    this.drawingSectors = true;
    try {
      const unique = new Map<string, MapSector>();
      const bounds = this.map.getBounds?.();
      const viewport = bounds ? { south: bounds.getSouth(), north: bounds.getNorth(), west: bounds.getWest(), east: bounds.getEast() } : null;
      for (const sector of this.sectors) {
        if (viewport && !isCircleNearBounds(sector.latitude, sector.longitude, viewport)) continue;
        const key = `${sector.latitude}:${sector.longitude}`;
        const existingSector = unique.get(key);
        if (!existingSector || sector.count > existingSector.count) unique.set(key, sector);
      }
      // Showing only a subset would hide overlapping higher-count circles and
      // misstate the distribution, so clear all circles until the user zooms in.
      const tooDense = unique.size > MAX_VISIBLE_CIRCLES;
      this.densityWarning.set(tooDense ? 'Hay demasiadas zonas para mostrarlas en esta vista. Acerca el mapa para ver la distribución.' : '');
      const features = (tooDense ? [] : [...unique.values()]).sort((a, b) => a.count - b.count).map((sector) => {
        const ring = buildProximityCircle(sector.latitude, sector.longitude, 1000, 64);
        const closed = [...ring, ring[0]];
        return {
          type: 'Feature' as const,
          properties: { color: getSectorColor(sector.count), label: String(sector.count), count: sector.count },
          geometry: { type: 'Polygon' as const, coordinates: [closed] },
        };
      });
      const data = { type: 'FeatureCollection' as const, features };
      if (existing) { existing.setData(data); this.sectorsDirty = false; return; }
      this.map.addSource('sector-counts', { type: 'geojson', data });
      this.map.addLayer({ id: 'sector-fill', type: 'fill', source: 'sector-counts', paint: { 'fill-color': ['get', 'color'], 'fill-opacity': 1, 'fill-outline-color': 'rgba(255,255,255,0.30)' } });
      this.map.addLayer({ id: 'sector-labels', type: 'symbol', source: 'sector-counts', layout: { 'text-field': ['get', 'label'], 'text-font': ['Open Sans Bold', 'Arial Unicode MS Bold'], 'text-size': 12 }, paint: { 'text-color': '#06111e', 'text-halo-color': '#f6f9fc', 'text-halo-width': 1.2 } });
      this.sectorsDirty = false;
    } catch {
      for (const id of ['sector-labels', 'sector-fill']) if (this.map.getLayer(id)) this.map.removeLayer(id);
      if (this.map.getSource('sector-counts')) this.map.removeSource('sector-counts');
      this.sectorError.set('No fue posible mostrar las zonas de proximidad.');
    } finally { this.drawingSectors = false; }
  }
  private async refreshMarkers() {
    const generation = ++this.generation;
    this.clearMarkers();
    if (!this.map || this.map.getZoom() < DETAIL_ZOOM) { this.pointError.set(''); return; }
    const b = this.map.getBounds();
    if (!b) return;
    const south = Math.max(-90, b.getSouth()); const north = Math.min(90, b.getNorth());
    const width = Math.min(360, b.getEast() - b.getWest());
    const west = ((b.getWest() + 180) % 360 + 360) % 360 - 180;
    const end = west + width;
    const bounds: MapBounds[] = end <= 180 ? [{ west, south, east: end, north }] : [{ west, south, east: 180, north }, { west: -180, south, east: end - 360, north }];
    try {
      const items: MapIncident[] = [];
      let pageCount = 0;
      for (const box of bounds) {
        let after: string | undefined;
        do {
          if (++pageCount > MAX_INCIDENT_PAGES) throw new RangeError('dense');
          const page = await firstValueFrom(this.service.incidents(box, after));
          if (this.destroyed || generation !== this.generation) return;
          if (page.items.length > MAX_VISIBLE_INCIDENTS - items.length ||
              (page.nextCursor && page.items.length === MAX_VISIBLE_INCIDENTS - items.length)) throw new RangeError('dense');
          items.push(...page.items);
          if (page.nextCursor && page.nextCursor === after) throw new Error('Invalid cursor');
          after = page.nextCursor || undefined;
        } while (after);
      }
      if (generation !== this.generation || !this.map) return;
      this.pointError.set('');
      for (const group of groupIncidents(items)) this.addMarker(group);
    } catch (error) { if (generation === this.generation) this.pointError.set(error instanceof RangeError ? 'Hay demasiadas incidencias para mostrarlas en esta vista. Acerca el mapa para verlas.' : 'No fue posible cargar las incidencias visibles. Mueve el mapa para reintentar.'); }
  }
  private addMarker(items: MapIncident[]) {
    if (!this.map) return;
    const first = items[0];
    const button = incidentMarkerButton(items);
    const popup = new mapboxgl.Popup({ offset: 22 }).setDOMContent(incidentPopup(items));
    this.markers.push(new mapboxgl.Marker({ element: button }).setLngLat([first.longitude, first.latitude]).setPopup(popup).addTo(this.map));
  }
  private clearMarkers() { this.markers.forEach((marker) => marker.remove()); this.markers = []; }
}
