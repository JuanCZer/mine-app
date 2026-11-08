import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Note } from '../models/note.model';

export interface MapIncident {
  id: string; title: string; content: string; state: string;
  latitude: number; longitude: number; status: string; createdAt: string;
}
export interface MapSector { latitude: number; longitude: number; count: number; }
export interface MapBounds { west: number; south: number; east: number; north: number; }
@Injectable({ providedIn: 'root' })
export class MapService {
  constructor(private readonly http: HttpClient) {}
  sectors() { return this.http.get<{ total: number; sectors: MapSector[] }>('/api/map/sectors'); }
  incidents(bounds: MapBounds, after?: string) {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(bounds)) params = params.set(key, String(value));
    if (after) params = params.set('after', after);
    return this.http.get<{ items: MapIncident[]; nextCursor: string | null }>('/api/map/incidents', { params });
  }
  detail(id: string) { return this.http.get<Note>(`/api/map/incidents/${encodeURIComponent(id)}`); }
}
