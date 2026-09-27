import { DetectionCase } from '../models/detection.model';

export function downloadCases(items: DetectionCase[], format: 'json' | 'geojson'): void {
  const content =
    format === 'json'
      ? {
          schemaVersion: 1,
          exportedAt: new Date().toISOString(),
          source: 'Registro local, sin verificación de sensores',
          cases: items,
        }
      : {
          type: 'FeatureCollection',
          features: items.map((item) => ({
            type: 'Feature',
            id: item.id,
            geometry: { type: 'Point', coordinates: [item.longitude, item.latitude] },
            properties: {
              code: item.code,
              title: item.title,
              stage: item.stage,
              status: item.status,
              priority: item.priority,
              soil: item.soil,
              demo: item.demo,
              updatedAt: item.updatedAt,
            },
          })),
        };
  const url = URL.createObjectURL(
    new Blob([JSON.stringify(content, null, 2)], {
      type: format === 'geojson' ? 'application/geo+json' : 'application/json',
    }),
  );
  const link = document.createElement('a');
  link.href = url;
  link.download = `gds-expedientes-${new Date().toISOString().slice(0, 10)}.${format}`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
