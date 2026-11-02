import { convertToParamMap } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { MapPage, groupIncidents, incidentPopup, incidentMarkerButton, isCircleNearBounds } from './map';
import { MapService } from '../../core/services/map.service';

describe('MapPage visible incidents', () => {
  it('only requests incident markers when zoomed in', async () => {
    const incidents = vi.fn(() => of({ items: [], nextCursor: null }));
    const page = new MapPage({ incidents } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const internal = page as unknown as { map: unknown; refreshMarkers: () => Promise<void> };
    internal.map = { getZoom: () => 7, getBounds: () => { throw new Error('unexpected bounds'); } };
    await internal.refreshMarkers();
    expect(incidents).not.toHaveBeenCalled();
  });
  it('loads every page for visible bounds at detailed zoom', async () => {
    const incidents = vi.fn().mockReturnValueOnce(of({ items: [], nextCursor: 'cursor' })).mockReturnValueOnce(of({ items: [], nextCursor: null }));
    const page = new MapPage({ incidents } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const internal = page as unknown as { map: unknown; refreshMarkers: () => Promise<void> };
    internal.map = { getZoom: () => 9, getBounds: () => ({ getSouth: () => 19, getNorth: () => 20, getWest: () => -100, getEast: () => -99 }) };
    await internal.refreshMarkers();
    expect(incidents).toHaveBeenCalledTimes(2);
    expect(incidents).toHaveBeenLastCalledWith({ west: -100, east: -99, south: 19, north: 20 }, 'cursor');
  });
  it('shows no partial markers and asks to zoom when the incident limit is exceeded', async () => {
    const item = { id: 'x', title: 'Uno', state: '', content: '', latitude: 19, longitude: -99, status: 'open', createdAt: '' };
    const incidents = vi.fn().mockReturnValueOnce(of({ items: Array(500).fill(item), nextCursor: 'a' })).mockReturnValueOnce(of({ items: Array(500).fill(item), nextCursor: 'b' }));
    const page = new MapPage({ incidents } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const internal = page as unknown as { map: unknown; refreshMarkers: () => Promise<void>; addMarker: (items: unknown[]) => void };
    internal.map = { getZoom: () => 9, getBounds: () => ({ getSouth: () => 19, getNorth: () => 20, getWest: () => -100, getEast: () => -99 }) };
    const addMarker = vi.spyOn(internal, 'addMarker');
    await internal.refreshMarkers();
    expect(incidents).toHaveBeenCalledTimes(2); expect(addMarker).not.toHaveBeenCalled(); expect(page.pointError()).toContain('Acerca');
  });
  it('ignores a delayed marker response after the viewport changes', async () => {
    const delayed = new Subject<{ items: never[]; nextCursor: null }>();
    const incidents = vi.fn().mockReturnValueOnce(delayed).mockReturnValueOnce(of({ items: [], nextCursor: null }));
    const page = new MapPage({ incidents } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const internal = page as unknown as { map: unknown; refreshMarkers: () => Promise<void>; addMarker: (items: unknown[]) => void };
    internal.map = { getZoom: () => 9, getBounds: () => ({ getSouth: () => 19, getNorth: () => 20, getWest: () => -100, getEast: () => -99 }) };
    const addMarker = vi.spyOn(internal, 'addMarker');
    const earlier = internal.refreshMarkers(); await internal.refreshMarkers(); delayed.next({ items: [], nextCursor: null }); await earlier;
    expect(addMarker).not.toHaveBeenCalled(); expect(page.pointError()).toBe('');
  });
  it('retries failed sector loading and draws the recovered data', async () => {
    const sectors = vi.fn().mockReturnValueOnce(throwError(() => new Error('offline'))).mockReturnValueOnce(of({ total: 1, sectors: [{ latitude: 19, longitude: -100, count: 1 }] }));
    const page = new MapPage({ sectors } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    await page.retrySectors();
    expect(page.sectorError()).toContain('sectores');
    const addSource = vi.fn();
    const internal = page as unknown as { map: unknown };
    internal.map = { isStyleLoaded: () => true, getSource: () => undefined, addSource, addLayer: vi.fn(), remove: vi.fn() };
    await page.retrySectors();
    expect(page.sectorError()).toBe('');
    expect(addSource).toHaveBeenCalledTimes(1);
    page.ngOnDestroy();
  });
  it('draws retried sectors once the map style becomes ready', async () => {
    const page = new MapPage({ sectors: () => of({ total: 0, sectors: [] }) } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    let ready = false;
    const addSource = vi.fn();
    const internal = page as unknown as { map: unknown; onStyleData: () => void };
    internal.map = { isStyleLoaded: () => ready, getSource: () => undefined, addSource, addLayer: vi.fn(), remove: vi.fn() };
    await page.retrySectors(); expect(addSource).not.toHaveBeenCalled();
    ready = true; internal.onStyleData(); expect(addSource).toHaveBeenCalledTimes(1);
    page.ngOnDestroy();
  });
  it('clears a map error only when its style and tiles are loaded', () => {
    const page = new MapPage({} as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    let loaded = false;
    const internal = page as unknown as { map: unknown; onMapIdle: () => void };
    internal.map = { isStyleLoaded: () => true, areTilesLoaded: () => loaded, getSource: () => undefined, remove: vi.fn() };
    page.mapError.set('error'); internal.onMapIdle(); expect(page.mapError()).toBe('error');
    loaded = true; internal.onMapIdle(); expect(page.mapError()).toBe(''); page.ngOnDestroy();
  });
  it('does not reenter drawing when adding a source emits styledata synchronously', async () => {
    const page = new MapPage({ sectors: () => of({ total: 0, sectors: [] }) } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const internal = page as unknown as { map: unknown; onStyleData: () => void };
    const sources = new Set<string>();
    const addSource = vi.fn((id: string) => { sources.add(id); internal.onStyleData(); });
    internal.map = { isStyleLoaded: () => true, getSource: (id: string) => sources.has(id), addSource, addLayer: vi.fn(), remove: vi.fn() };
    await page.retrySectors();
    expect(addSource).toHaveBeenCalledTimes(1); page.ngOnDestroy();
  });
  it('draws pending sectors on idle when the style becomes ready', async () => {
    const page = new MapPage({ sectors: () => of({ total: 0, sectors: [] }) } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    let ready = false;
    const addSource = vi.fn();
    const internal = page as unknown as { map: unknown; onMapIdle: () => void };
    internal.map = { isStyleLoaded: () => ready, areTilesLoaded: () => true, getSource: () => undefined, addSource, addLayer: vi.fn(), remove: vi.fn() };
    await page.retrySectors(); expect(addSource).not.toHaveBeenCalled();
    ready = true; internal.onMapIdle(); expect(addSource).toHaveBeenCalledTimes(1); page.ngOnDestroy();
  });
  it('shows the unique total and replaces the source when proximity data is reloaded', async () => {
    const sectors = vi.fn().mockReturnValueOnce(of({ total: 106, sectors: [{ latitude: 20.09798, longitude: -98.76842, count: 106 }] }))
      .mockReturnValueOnce(of({ total: 1, sectors: [{ latitude: 20, longitude: -98, count: 1 }] }));
    const page = new MapPage({ sectors } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const setData = vi.fn();
    let source: unknown;
    const internal = page as unknown as { map: unknown };
    internal.map = { isStyleLoaded: () => true, getSource: () => source, addSource: (_id: string, data: unknown) => { source = { setData }; expect(JSON.stringify(data)).toContain('#f0c84d'); }, addLayer: vi.fn(), remove: vi.fn() };
    await page.retrySectors();
    expect((page as unknown as { totalIncidents: () => number }).totalIncidents()).toBe(106);
    await page.retrySectors();
    expect((page as unknown as { totalIncidents: () => number }).totalIncidents()).toBe(1);
    expect(setData).toHaveBeenCalledOnce();
    expect(JSON.stringify(setData.mock.calls[0][0])).toContain('#2bb673');
    page.ngOnDestroy();
  });
  it('keeps circles visible at detailed zoom and shows the highest count on overlaps without blending', async () => {
    const page = new MapPage({ sectors: () => of({ total: 106, sectors: [
      { latitude: 20, longitude: -98, count: 106 }, { latitude: 20.001, longitude: -98, count: 1 },
    ] }) } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const addSource = vi.fn(); const addLayer = vi.fn();
    const internal = page as unknown as { map: unknown };
    internal.map = { isStyleLoaded: () => true, getSource: () => undefined, addSource, addLayer, remove: vi.fn() };
    await page.retrySectors();
    const features = addSource.mock.calls[0][1].data.features;
    expect(features.map((f: { properties: { count: number } }) => f.properties.count)).toEqual([1, 106]);
    const fill = addLayer.mock.calls[0][0];
    expect(fill.maxzoom).toBeUndefined();
    expect(fill.paint['fill-opacity']).toBe(1);
    page.ngOnDestroy();
  });
  it('draws a single circle for coincident incidents without reducing their total', async () => {
    const page = new MapPage({ sectors: () => of({ total: 106, sectors: Array.from({ length: 106 }, () => ({ latitude: 20, longitude: -98, count: 106 })) }) } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const addSource = vi.fn();
    const internal = page as unknown as { map: unknown };
    internal.map = { isStyleLoaded: () => true, getSource: () => undefined, addSource, addLayer: vi.fn(), remove: vi.fn() };
    await page.retrySectors();
    expect(addSource.mock.calls[0][1].data.features).toHaveLength(1);
    expect(page.totalIncidents()).toBe(106);
    page.ngOnDestroy();
  });
  it('ignores older requests that finish after a newer request', async () => {
    const first = new Subject<{ total: number; sectors: never[] }>();
    const second = new Subject<{ total: number; sectors: never[] }>();
    const sectors = vi.fn().mockReturnValueOnce(first).mockReturnValueOnce(second);
    const page = new MapPage({ sectors } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const earlier = page.retrySectors(); const later = page.retrySectors();
    second.next({ total: 2, sectors: [] }); await later;
    first.next({ total: 1, sectors: [] }); await earlier;
    expect(page.totalIncidents()).toBe(2);
    page.ngOnDestroy();
  });
  it('marks previous data as outdated when a refresh fails', async () => {
    const sectors = vi.fn().mockReturnValueOnce(of({ total: 1, sectors: [] })).mockReturnValueOnce(throwError(() => new Error('offline')));
    const page = new MapPage({ sectors } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    await page.retrySectors(); await page.retrySectors();
    expect(page.sectorError()).toContain('desactualizados');
    page.ngOnDestroy();
  });
  it('does not draw a zero-count overlay when sectors cannot be loaded', async () => {
    const page = new MapPage({ sectors: () => throwError(() => new Error('offline')) } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    await page.ngOnInit();
    const addSource = vi.fn();
    const internal = page as unknown as { map: unknown; drawSectors: () => void };
    internal.map = { addSource, addLayer: vi.fn(), remove: vi.fn() };
    internal.drawSectors();
    expect(addSource).not.toHaveBeenCalled();
    expect(page.sectorError()).toContain('sectores');
    page.ngOnDestroy();
  });
});

describe('viewport proximity rendering', () => {
  it('includes circles within one kilometre of the viewport, including the antimeridian', () => {
    expect(isCircleNearBounds(20, -98.009, { south: 19, north: 21, west: -98, east: -97 })).toBe(true);
    expect(isCircleNearBounds(20, -98.02, { south: 19, north: 21, west: -98, east: -97 })).toBe(false);
    expect(isCircleNearBounds(0, -179.999, { south: -1, north: 1, west: 179, east: 180 })).toBe(true);
    expect(isCircleNearBounds(89.999, 120, { south: 89.998, north: 90, west: -5, east: 5 })).toBe(true);
  });
  it('clears all circles when view is too dense and restores complete view after zoom', async () => {
    const sectors = Array.from({ length: 1001 }, (_, i) => ({ latitude: 20 + i / 10000, longitude: -98, count: 106 }));
    const page = new MapPage({ sectors: () => of({ total: 1001, sectors }) } as unknown as MapService, { snapshot: { queryParamMap: convertToParamMap({}) } } as never);
    const setData = vi.fn(); let source: unknown; let north = 21;
    const internal = page as unknown as { map: unknown; sectorsDirty: boolean; drawSectors: () => void };
    internal.map = { isStyleLoaded: () => true, getBounds: () => ({ getSouth: () => 19, getNorth: () => north, getWest: () => -99, getEast: () => -97 }), getSource: () => source, addSource: (_id: string, data: { data: { features: unknown[] } }) => { expect(data.data.features).toHaveLength(0); source = { setData }; }, addLayer: vi.fn(), remove: vi.fn() };
    await page.retrySectors();
    expect(page.densityWarning()).toContain('Acerca'); expect(page.totalIncidents()).toBe(1001);
    north = 20.001; internal.sectorsDirty = true; internal.drawSectors();
    expect(page.densityWarning()).toBe('');
    expect(setData.mock.calls[0][0].features.length).toBeGreaterThan(0);
    expect(setData.mock.calls[0][0].features.length).toBeLessThan(1001);
    page.ngOnDestroy();
  });
});

describe('coincident incidents', () => {
  it('provides a link for each incident at the same coordinates', () => {
    const incidents = [
      { id: 'a', title: 'Uno', state: 'A', content: '', latitude: 19, longitude: -99, status: 'open', createdAt: '' },
      { id: 'b', title: 'Dos', state: 'A', content: '', latitude: 19, longitude: -99, status: 'open', createdAt: '' },
    ];
    const groups = groupIncidents(incidents);
    expect(groups).toHaveLength(1);
    const button = incidentMarkerButton(groups[0]);
    expect(button.tagName).toBe('BUTTON');
    expect(button.getAttribute('aria-label')).toContain('2 incidencias');
    const popup = incidentPopup(groups[0]);
    expect(Array.from(popup.querySelectorAll('a')).map(a => a.getAttribute('href'))).toEqual(['/mapa/incidencia/a', '/mapa/incidencia/b']);
  });
});
