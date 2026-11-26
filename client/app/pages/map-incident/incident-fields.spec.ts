import { describe, expect, it } from 'vitest';
import { incidentFields } from './incident-fields';
import { en } from '../../core/i18n/map-dictionary';

describe('incidentFields', () => {
  it('translates labels and empty values without changing user content or catalog snapshots', () => {
    const fields = incidentFields(
      {
        terrainNotes: 'Texto original',
        blueUasNdaaCompliant: true,
        evidence: [],
        reference: null,
        catalogSnapshot: { selections: [{ label: 'Suelo histórico' }] },
      },
      en,
    );
    expect(fields).toContainEqual({ label: 'Terrain notes', value: 'Texto original' });
    expect(fields).toContainEqual({ label: 'Blue UAS / NDAA compliant', value: 'Yes' });
    expect(fields).toContainEqual({ label: 'Evidence', value: 'No records' });
    expect(fields).toContainEqual({ label: 'Reference', value: 'No data' });
    expect(fields.some((field) => field.value === 'Suelo histórico')).toBe(true);
  });
  it('presents nested phase data as readable labels and values', () => {
    const fields = incidentFields({
      validatedAt: '2026-10-01',
      data: { intake: { platform: 'VELES', blueUasNdaaCompliant: true, signatures: ['A', 'B'] } },
    });
    expect(fields).toContainEqual({ label: 'Validada el', value: '2026-10-01' });
    expect(fields).toContainEqual({ label: 'Datos · Registro · Plataforma', value: 'VELES' });
    expect(fields).toContainEqual({
      label: 'Datos · Registro · Cumple Blue UAS / NDAA',
      value: 'Sí',
    });
    expect(fields).toContainEqual({ label: 'Datos · Registro · Firmas', value: 'A, B' });
  });
});
