import { CaseInput } from '../models/detection.model';
import { DetectionService } from './detection.service';

const key = 'gds-detection-cases-v1';
const archived = JSON.stringify({
  version: 1,
  cases: [
    {
      id: 'case-a',
      title: 'Expediente exclusivo de la cuenta A',
      latitude: 19.4,
      longitude: -101.2,
    },
  ],
});

const input: CaseInput = {
  title: 'Intento de la cuenta B',
  state: 'michoacan',
  municipality: 'Zona de prueba',
  latitude: 19.4,
  longitude: -101.2,
  soil: 'andosol',
  season: 'rainy',
  priority: 'medium',
  owner: 'Cuenta B',
  description: 'No debe modificar el archivo local retirado.',
};

describe('DetectionService retirado', () => {
  beforeEach(() => localStorage.setItem(key, archived));

  afterEach(() => localStorage.clear());

  it('no entrega a la cuenta B los expedientes locales de la cuenta A', () => {
    const serviceForAccountB = new DetectionService();

    expect(serviceForAccountB.cases()).toEqual([]);
    expect(serviceForAccountB.storageError()).toContain('retirado');
  });

  it('no permite que otra cuenta modifique el archivo local retirado', () => {
    const serviceForAccountB = new DetectionService();

    expect(() => serviceForAccountB.create(input)).toThrow('retirado');
    expect(localStorage.getItem(key)).toBe(archived);
  });

  it('no borra ni reescribe datos existentes al iniciar el servicio retirado', () => {
    new DetectionService();

    expect(localStorage.getItem(key)).toBe(archived);
  });
});
