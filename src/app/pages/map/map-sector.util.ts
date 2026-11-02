export type SectorNote = {
  id: string;
  latitude: number;
  longitude: number;
};

export type SectorCell = {
  key: string;
  count: number;
  minLat: number;
  maxLat: number;
  minLng: number;
  maxLng: number;
};

export const SECTOR_SIZE = 1;

export function getSectorKey(latitude: number, longitude: number, size = SECTOR_SIZE): string {
  const latBucket = Math.floor(latitude / size);
  const lngBucket = Math.floor(longitude / size);
  return `${latBucket}:${lngBucket}`;
}

export function haversineDistanceMeters(latitude1: number, longitude1: number, latitude2: number, longitude2: number): number {
  const toRadians = (degrees: number) => (degrees * Math.PI) / 180;
  const earthRadiusMeters = 6371000;
  const deltaLatitude = toRadians(latitude2 - latitude1);
  const deltaLongitude = toRadians(longitude2 - longitude1);
  const latitudeA = toRadians(latitude1);
  const latitudeB = toRadians(latitude2);

  const a = Math.sin(deltaLatitude / 2) ** 2 + Math.cos(latitudeA) * Math.cos(latitudeB) * Math.sin(deltaLongitude / 2) ** 2;
  const centralAngle = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  return earthRadiusMeters * centralAngle;
}

export function buildProximityCircle(latitude: number, longitude: number, radiusMeters = 1000, segments = 64): [number, number][] {
  const points: [number, number][] = [];
  const earthRadiusMeters = 6371000;
  const angularDistance = radiusMeters / earthRadiusMeters;
  const centerLatitude = (latitude * Math.PI) / 180;
  const centerLongitude = (longitude * Math.PI) / 180;

  for (let index = 0; index <= segments; index += 1) {
    const bearing = (index / segments) * Math.PI * 2;
    const lat = Math.asin(
      Math.sin(centerLatitude) * Math.cos(angularDistance) +
      Math.cos(centerLatitude) * Math.sin(angularDistance) * Math.cos(bearing),
    );
    const lon = centerLongitude + Math.atan2(
      Math.sin(bearing) * Math.sin(angularDistance) * Math.cos(centerLatitude),
      Math.cos(angularDistance) - Math.sin(centerLatitude) * Math.sin(lat),
    );
    points.push([(lon * 180) / Math.PI, (lat * 180) / Math.PI]);
  }

  return points;
}

export function getSectorColor(count: number): string {
  if (count <= 99) return '#2bb673';
  if (count <= 300) return '#f0c84d';
  if (count <= 599) return '#f28f3d';
  return '#e94d4d';
}

export function buildSectorGrid(notes: SectorNote[]): SectorCell[] {
  const counts = new Map<string, SectorCell>();

  for (const note of notes) {
    const key = getSectorKey(note.latitude, note.longitude);
    const existing = counts.get(key);

    if (existing) {
      existing.count += 1;
      continue;
    }

    const latBucket = Math.floor(note.latitude / SECTOR_SIZE);
    const lngBucket = Math.floor(note.longitude / SECTOR_SIZE);
    const minLat = latBucket * SECTOR_SIZE;
    const minLng = lngBucket * SECTOR_SIZE;
    counts.set(key, {
      key,
      count: 1,
      minLat,
      maxLat: minLat + SECTOR_SIZE,
      minLng,
      maxLng: minLng + SECTOR_SIZE,
    });
  }

  return Array.from(counts.values()).sort((first, second) => second.count - first.count || first.key.localeCompare(second.key));
}
