import { describe, expect, it } from 'vitest';
import { incidentFields } from './incident-fields';

describe('incidentFields', () => {
  it('presents nested phase data as readable labels and values', () => {
    const fields = incidentFields({ validatedAt: '2026-10-01', data: { intake: { platform: 'VELES', blueUasNdaaCompliant: true, signatures: ['A', 'B'] } } });
    expect(fields).toContainEqual({ label: 'Validada el', value: '2026-10-01' });
    expect(fields).toContainEqual({ label: 'Datos · Registro · Plataforma', value: 'VELES' });
    expect(fields).toContainEqual({ label: 'Datos · Registro · Cumple Blue UAS / NDAA', value: 'Sí' });
    expect(fields).toContainEqual({ label: 'Datos · Registro · Firmas', value: 'A, B' });
  });
});
