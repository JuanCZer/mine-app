import { describe, expect, it } from 'vitest';
import { buildProximityCircle, buildSectorGrid, getSectorColor, getSectorKey, haversineDistanceMeters } from './map-sector.util';

describe('map sector util', () => {
  it('groups notes into the same stable sector cell', () => {
    const grid = buildSectorGrid([
      { id: 'a', latitude: 19.4326, longitude: -99.1332 },
      { id: 'b', latitude: 19.4342, longitude: -99.1326 },
      { id: 'c', latitude: 30.7000, longitude: -99.5000 },
    ]);

    expect(grid).toHaveLength(2);
    expect(grid.find((sector) => sector.count === 2)?.count).toBe(2);
    expect(grid.find((sector) => sector.count === 1)?.count).toBe(1);
  });

  it('contains coordinates in their cell for negative longitudes', () => {
    const cell = buildSectorGrid([{ id: 'a', latitude: 19.4326, longitude: -99.1332 }])[0];
    expect(cell.minLng).toBe(-100);
    expect(cell.maxLng).toBe(-99);
  });

  it('uses the configured count thresholds for color buckets', () => {
    expect(getSectorColor(0)).toBe('#2bb673');
    expect(getSectorColor(99)).toBe('#2bb673');
    expect(getSectorColor(100)).toBe('#f0c84d');
    expect(getSectorColor(300)).toBe('#f0c84d');
    expect(getSectorColor(301)).toBe('#f28f3d');
    expect(getSectorColor(599)).toBe('#f28f3d');
    expect(getSectorColor(600)).toBe('#e94d4d');
  });

  it('creates a deterministic key from latitude and longitude', () => {
    expect(getSectorKey(19.4326, -99.1332)).toBe('19:-100');
    expect(getSectorKey(19.9999, -99.9999)).toBe('19:-100');
  });

  it('measures geodesic distance in meters and produces a 1 km circle ring', () => {
    const start = { latitude: 19.4326, longitude: -99.1332 };
    const within = { latitude: start.latitude + 0.008, longitude: start.longitude };
    const outside = { latitude: start.latitude + 0.02, longitude: start.longitude };

    expect(haversineDistanceMeters(start.latitude, start.longitude, within.latitude, within.longitude)).toBeLessThan(1000);
    expect(haversineDistanceMeters(start.latitude, start.longitude, outside.latitude, outside.longitude)).toBeGreaterThan(1000);

    const circle = buildProximityCircle(start.latitude, start.longitude, 1000);
    expect(circle.length).toBeGreaterThan(12);
    const firstPointDistance = haversineDistanceMeters(start.latitude, start.longitude, circle[0][1], circle[0][0]);
    expect(firstPointDistance).toBeGreaterThan(900);
    expect(firstPointDistance).toBeLessThan(1100);
  });
});
