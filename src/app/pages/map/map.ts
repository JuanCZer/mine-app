import {
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import mapboxgl from 'mapbox-gl';
import { firstValueFrom } from 'rxjs';
import { MAPBOX_TOKEN } from '../../core/config/mapbox.config';
import { Note } from '../../core/models/note.model';
import { NotesService } from '../../core/services/notes.service';

@Component({
  selector: 'app-map',
  imports: [RouterLink],
  templateUrl: './map.html',
  styleUrl: './map.less',
})
export class MapPage implements OnInit, OnDestroy {
  @ViewChild('mapContainer') private mapContainer?: ElementRef<HTMLElement>;
  readonly notes = signal<Note[]>([]);
  readonly areas = computed(() =>
    Array.from(
      new Set(
        this.notes()
          .map((note) => note.state)
          .filter((area): area is string => !!area),
      ),
    ).sort((first, second) => first.localeCompare(second, 'es')),
  );
  readonly stateFilter = signal('');
  readonly visibleNotes = computed(() =>
    this.stateFilter()
      ? this.notes().filter((note) => note.state === this.stateFilter())
      : this.notes(),
  );
  readonly loading = signal(true);
  readonly error = signal('');
  private map?: mapboxgl.Map;
  private markers: mapboxgl.Marker[] = [];
  private circleLayerIds: string[] = [];
  private readonly requestedNoteId: string | null;
  constructor(
    private readonly notesService: NotesService,
    route: ActivatedRoute,
  ) {
    this.requestedNoteId = route.snapshot.queryParamMap.get('note');
  }

  async ngOnInit() {
    try {
      this.notes.set(
        (await firstValueFrom(this.notesService.list())).filter((note) =>
          this.hasCoordinates(note),
        ),
      );
    } catch {
      this.error.set('No fue posible cargar las coordenadas.');
    } finally {
      this.loading.set(false);
      setTimeout(() => this.initializeMap());
    }
  }
  ngOnDestroy() {
    this.clearMapItems();
    this.map?.remove();
  }
  focus(note: Note) {
    this.map?.flyTo({ center: [note.longitude!, note.altitude!], zoom: 14, essential: true });
  }
  filterByState(state: string) {
    this.stateFilter.set(state);
    this.drawMapItems();
  }
  private initializeMap() {
    if (!this.mapContainer) return;
    mapboxgl.accessToken = MAPBOX_TOKEN;
    const first = this.requestedNoteId
      ? (this.notes().find((note) => note.id === this.requestedNoteId) ?? this.notes()[0])
      : this.notes()[0];
    this.map = new mapboxgl.Map({
      container: this.mapContainer.nativeElement,
      style: 'mapbox://styles/mapbox/dark-v11',
      center: first ? [first.longitude!, first.altitude!] : [-99.1332, 19.4326],
      zoom: first ? 11 : 4.5,
    });
    this.map.addControl(new mapboxgl.NavigationControl(), 'top-right');
    this.map.on('load', () => {
      this.drawMapItems();
      if (first) this.focus(first);
    });
  }
  private drawMapItems() {
    if (!this.map) return;
    if (!this.map.isStyleLoaded()) return;
    this.clearMapItems();
    const bounds = new mapboxgl.LngLatBounds();
    for (const note of this.visibleNotes()) {
      const popup = new mapboxgl.Popup({ offset: 22 }).setHTML(
        `<strong>${this.escape(note.title || 'Incidencia sin identificar')}</strong><small>${this.escape(note.state || 'Área sin identificar')}</small><p>${this.escape(note.content.slice(0, 120))}</p><code>${note.altitude}, ${note.longitude}</code>`,
      );
      const marker = new mapboxgl.Marker({ color: '#5bb8e8' })
        .setLngLat([note.longitude!, note.altitude!])
        .setPopup(popup)
        .addTo(this.map);
      this.markers.push(marker);
      bounds.extend([note.longitude!, note.altitude!]);
      this.addRadius(note);
    }
    if (this.visibleNotes().length > 1) this.map.fitBounds(bounds, { padding: 70, maxZoom: 14 });
    else if (this.visibleNotes().length === 1) this.focus(this.visibleNotes()[0]);
  }
  private addRadius(note: Note) {
    if (!this.map) return;
    const id = `radius-${note.id}`;
    this.map.addSource(id, {
      type: 'geojson',
      data: {
        type: 'Feature',
        properties: {},
        geometry: {
          type: 'Polygon',
          coordinates: [this.circleCoordinates(note.longitude!, note.altitude!, 500)],
        },
      },
    });
    this.map.addLayer({
      id,
      type: 'fill',
      source: id,
      paint: { 'fill-color': '#5bb8e8', 'fill-opacity': 0.18, 'fill-outline-color': '#3b8fbd' },
    });
    this.circleLayerIds.push(id);
  }
  private circleCoordinates(longitude: number, latitude: number, radiusMeters: number): number[][] {
    const earthRadius = 6371008.8;
    const angularDistance = radiusMeters / earthRadius;
    const latitudeRadians = (latitude * Math.PI) / 180;
    const longitudeRadians = (longitude * Math.PI) / 180;
    return Array.from({ length: 65 }, (_, index) => {
      const bearing = (index * 2 * Math.PI) / 64;
      const lat = Math.asin(
        Math.sin(latitudeRadians) * Math.cos(angularDistance) +
          Math.cos(latitudeRadians) * Math.sin(angularDistance) * Math.cos(bearing),
      );
      const lng =
        longitudeRadians +
        Math.atan2(
          Math.sin(bearing) * Math.sin(angularDistance) * Math.cos(latitudeRadians),
          Math.cos(angularDistance) - Math.sin(latitudeRadians) * Math.sin(lat),
        );
      return [(lng * 180) / Math.PI, (lat * 180) / Math.PI];
    });
  }
  private clearMapItems() {
    this.markers.forEach((marker) => marker.remove());
    this.markers = [];
    if (!this.map) return;
    for (const id of this.circleLayerIds) {
      if (this.map.getLayer(id)) this.map.removeLayer(id);
      if (this.map.getSource(id)) this.map.removeSource(id);
    }
    this.circleLayerIds = [];
  }
  private hasCoordinates(note: Note) {
    return (
      note.altitude !== null &&
      note.longitude !== null &&
      note.altitude >= -90 &&
      note.altitude <= 90 &&
      note.longitude >= -180 &&
      note.longitude <= 180
    );
  }
  private escape(value: string) {
    const node = document.createElement('div');
    node.textContent = value;
    return node.innerHTML;
  }
}
