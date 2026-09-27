import { CaseInput, DetectionStage, emptyReview, StageReview } from '../models/detection.model';
import { DetectionService } from './detection.service';

const input: CaseInput = {
  title: 'Registro de prueba',
  state: 'Michoacán',
  municipality: 'Zona de prueba',
  latitude: 19.4,
  longitude: -101.2,
  soil: 'andosol',
  season: 'rainy',
  priority: 'medium',
  owner: 'Revisor de prueba',
  description: 'Observación de prueba.',
};
const key = 'gds-detection-cases-v1';

describe('DetectionService', () => {
  let service: DetectionService;
  let id: string;
  const current = () => service.cases().find((item) => item.id === id)!;
  function review(stage: DetectionStage, values: Partial<StageReview> = {}) {
    service.saveReview(id, stage, {
      ...emptyReview(),
      reviewer: 'Especialista de prueba',
      notes: 'Fundamento documentado.',
      ...values,
    });
  }
  function evidence(
    stage: DetectionStage,
    sensor: 'LiDAR' | 'GPR' | 'RGB' | 'Observación' | 'Informe' = 'LiDAR',
  ) {
    service.addEvidence(id, {
      stage,
      sensor,
      reference: 'Folio de prueba',
      summary: 'Evidencia de prueba.',
    });
  }
  function toReconnaissance() {
    review('indications', { baseline: 'BASE-01', comparison: 'ACTUAL-01' });
    evidence('indications');
    service.advance(id);
  }
  function toSurveillance() {
    toReconnaissance();
    review('reconnaissance', { finding: 'confirmed' });
    evidence('reconnaissance', 'GPR');
    evidence('reconnaissance', 'RGB');
    service.advance(id);
  }
  beforeEach(() => {
    localStorage.clear();
    service = new DetectionService();
    id = service.create(input).id;
  });
  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('creates a georeferenced case in I&W and reloads its history', () => {
    expect(current().stage).toBe('indications');
    expect(current().latitude).toBe(19.4);
    expect(new DetectionService().cases()[0]).toEqual(current());
    expect(current().history).toHaveLength(1);
  });
  it('rejects invalid coordinates and whitespace-only required fields', () => {
    expect(() => service.create({ ...input, latitude: 91 })).toThrow('coordenadas');
    expect(() => service.create({ ...input, longitude: NaN })).toThrow('coordenadas');
    expect(() => service.create({ ...input, owner: '  ' })).toThrow('Completa');
    expect(service.cases()).toHaveLength(1);
  });
  it('blocks advancement without both baseline and comparison references', () => {
    review('indications', { comparison: 'ACTUAL-01' });
    evidence('indications');
    expect(() => service.advance(id)).toThrow('levantamiento base');
    expect(current().stage).toBe('indications');
  });
  it('requires evidence and a reviewer even with baseline references', () => {
    service.saveReview(id, 'indications', {
      ...emptyReview(),
      baseline: 'BASE',
      comparison: 'ACTUAL',
      notes: 'Motivo',
    });
    expect(() => service.advance(id)).toThrow('quién revisa');
    review('indications', { baseline: 'BASE', comparison: 'ACTUAL' });
    expect(() => service.advance(id)).toThrow('evidencia');
  });
  it('follows 0 → 1 → 3 → 2 and closes with external report references', () => {
    toSurveillance();
    expect(current().stage).toBe('surveillance');
    review('surveillance', { environment: 'reviewed' });
    evidence('surveillance', 'Observación');
    service.advance(id);
    expect(current().stage).toBe('mitigation');
    review('mitigation', { report: 'EOD-01', techint: 'TECHINT-01' });
    evidence('mitigation', 'Informe');
    service.advance(id);
    expect(current().status).toBe('closed');
    expect(current().history.at(-1)?.text).toContain('TECHINT');
    expect(new DetectionService().cases()[0].status).toBe('closed');
  });
  it('rejects modifications to future and completed stages', () => {
    expect(() => review('mitigation', { report: 'EOD' })).toThrow('etapa activa');
    toReconnaissance();
    expect(() => review('indications')).toThrow('etapa activa');
    expect(() => evidence('mitigation')).toThrow('etapa activa');
  });
  it('keeps inconclusive reconnaissance in place', () => {
    toReconnaissance();
    review('reconnaissance', { finding: 'inconclusive' });
    evidence('reconnaissance', 'GPR');
    evidence('reconnaissance', 'RGB');
    expect(() => service.advance(id)).toThrow('inconcluso');
    expect(current().stage).toBe('reconnaissance');
  });
  it('requires different sensor sources instead of multiple references from one sensor', () => {
    toReconnaissance();
    review('reconnaissance', { finding: 'confirmed' });
    evidence('reconnaissance', 'GPR');
    evidence('reconnaissance', 'GPR');
    expect(() => service.advance(id)).toThrow('dos sensores');
  });
  it('closes documented false positives without entering mitigation', () => {
    toReconnaissance();
    review('reconnaissance', { finding: 'false-positive' });
    evidence('reconnaissance', 'GPR');
    evidence('reconnaissance', 'RGB');
    service.advance(id);
    expect(current().status).toBe('discarded');
    expect(current().stage).toBe('reconnaissance');
    expect(() => service.advance(id)).toThrow('cerrado');
    expect(() => evidence('reconnaissance', 'RGB')).toThrow('etapa activa');
  });
  it('blocks mitigation until the environment review is documented', () => {
    toSurveillance();
    review('surveillance', { environment: 'blocked' });
    evidence('surveillance', 'Observación');
    expect(() => service.advance(id)).toThrow('entorno');
    expect(current().stage).toBe('surveillance');
  });
  it('requires both specialist closure and TECHINT references', () => {
    toSurveillance();
    review('surveillance', { environment: 'reviewed' });
    evidence('surveillance', 'Observación');
    service.advance(id);
    review('mitigation', { report: 'EOD-01' });
    evidence('mitigation', 'Informe');
    expect(() => service.advance(id)).toThrow('TECHINT');
    expect(current().status).toBe('open');
  });
  it('retains local state and shows an error when persistence fails', () => {
    const original = current();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Full', 'QuotaExceededError');
    });
    expect(() => review('indications', { baseline: 'BASE' })).toThrow('No se pudo guardar');
    expect(current()).toEqual(original);
    expect(service.storageError()).toBeTruthy();
  });
  it('does not overwrite unreadable stored data', () => {
    localStorage.setItem(key, '{broken');
    const corrupted = new DetectionService();
    expect(corrupted.cases()).toHaveLength(0);
    expect(corrupted.storageError()).toBeTruthy();
    expect(() => corrupted.create(input)).toThrow();
    expect(localStorage.getItem(key)).toBe('{broken');
  });
  it('does not silently overwrite a change from another tab', () => {
    const otherTab = new DetectionService();
    otherTab.create({ ...input, title: 'Otro expediente' });
    expect(() => review('indications')).toThrow('otra pestaña');
    expect(new DetectionService().cases()).toHaveLength(2);
  });
  it('does not recreate records removed by another tab', () => {
    localStorage.removeItem(key);
    expect(() => review('indications')).toThrow('otra pestaña');
    expect(localStorage.getItem(key)).toBeNull();
  });
  it('rejects stored records with dates that cannot be displayed', () => {
    const saved = JSON.parse(localStorage.getItem(key)!);
    saved.cases[0].createdAt = 'invalid-date';
    localStorage.setItem(key, JSON.stringify(saved));
    const invalid = new DetectionService();
    expect(invalid.storageError()).toBeTruthy();
    expect(invalid.cases()).toHaveLength(0);
  });
});
