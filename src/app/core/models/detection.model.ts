export type DetectionStage = 'indications' | 'reconnaissance' | 'surveillance' | 'mitigation';
export type CaseStatus = 'open' | 'closed' | 'discarded';
export type Priority = 'high' | 'medium' | 'low';
export type Soil = 'vertisol' | 'andosol' | 'leptosol' | 'unknown';
export type Season = 'dry' | 'rainy' | 'unknown';
export type Sensor =
  | 'LiDAR'
  | 'RGB'
  | 'IR'
  | 'GPR'
  | 'Magnetometría'
  | 'EO/IR'
  | 'IMSI/IMEI/TMSI'
  | 'EOD'
  | 'TECHINT'
  | 'Observación'
  | 'Informe';

export interface LayerDefinition {
  id: DetectionStage;
  number: string;
  title: string;
  subtitle: string;
  description: string;
  platform: string;
  pages: string;
}

// The source numbers surveillance as layer 3, but explicitly places it before mitigation.
export const DETECTION_LAYERS: readonly LayerDefinition[] = [
  {
    id: 'indications',
    number: '00',
    title: 'Indicios tempranos',
    subtitle: 'Inteligencia · I&W',
    description:
      'Documentar cambios respecto al levantamiento base y registrar una anomalía georreferenciada.',
    platform: 'IF1200A · LiDAR / RGB / IR',
    pages: '7–10',
  },
  {
    id: 'reconnaissance',
    number: '01',
    title: 'Reconocimiento',
    subtitle: 'Correlación multisensorial',
    description:
      'Reunir evidencias de distintos sensores y registrar el dictamen técnico de un especialista.',
    platform: 'VELES · GPR / magnetometría / RGB / IR',
    pages: '11–17',
  },
  {
    id: 'surveillance',
    number: '03',
    title: 'Vigilancia',
    subtitle: 'Revisión del entorno',
    description:
      'Registrar la revisión del entorno y la decisión del responsable antes de pasar a mitigación.',
    platform: 'Observación EO/IR · revisión humana',
    pages: '25–26',
  },
  {
    id: 'mitigation',
    number: '02',
    title: 'Mitigación y cierre',
    subtitle: 'Seguimiento especializado',
    description:
      'Dar seguimiento a la intervención externa y conservar el informe de cierre y la referencia TECHINT.',
    platform: 'Equipo especializado · informe EOD / TECHINT',
    pages: '18–24',
  },
];

export const PRIORITY_LABELS: Record<Priority, string> = {
  high: 'Alta',
  medium: 'Media',
  low: 'Baja',
};
export const SOIL_LABELS: Record<Soil, string> = {
  vertisol: 'Vertisol',
  andosol: 'Andosol / Luvisol',
  leptosol: 'Leptosol',
  unknown: 'Sin determinar',
};
export const SEASON_LABELS: Record<Season, string> = {
  dry: 'Seca',
  rainy: 'Lluvias',
  unknown: 'Sin determinar',
};
export const SENSORS: readonly Sensor[] = [
  'LiDAR',
  'RGB',
  'IR',
  'GPR',
  'Magnetometría',
  'EO/IR',
  'IMSI/IMEI/TMSI',
  'EOD',
  'TECHINT',
  'Observación',
  'Informe',
];

export interface CaseInput {
  title: string;
  state: string;
  municipality: string;
  latitude: number;
  longitude: number;
  soil: Soil;
  season: Season;
  priority: Priority;
  owner: string;
  description: string;
}

export interface StageReview {
  baseline: string;
  comparison: string;
  finding: 'pending' | 'confirmed' | 'inconclusive' | 'false-positive';
  environment: 'pending' | 'blocked' | 'reviewed';
  report: string;
  techint: string;
  reviewer: string;
  notes: string;
}

export const emptyReview = (): StageReview => ({
  baseline: '',
  comparison: '',
  finding: 'pending',
  environment: 'pending',
  report: '',
  techint: '',
  reviewer: '',
  notes: '',
});

export interface Evidence {
  id: string;
  stage: DetectionStage;
  sensor: Sensor;
  reference: string;
  summary: string;
  recordedAt: string;
}

export interface CaseEvent {
  id: string;
  at: string;
  actor: string;
  text: string;
}

export interface DetectionCase extends CaseInput {
  id: string;
  code: string;
  stage: DetectionStage;
  status: CaseStatus;
  demo: boolean;
  createdAt: string;
  updatedAt: string;
  reviews: Record<DetectionStage, StageReview>;
  evidence: Evidence[];
  history: CaseEvent[];
}

export function stageLabel(stage: DetectionStage): string {
  return DETECTION_LAYERS.find((layer) => layer.id === stage)!.title;
}

export function transitionIssues(item: DetectionCase): string[] {
  if (item.status !== 'open') return ['El expediente ya está cerrado.'];
  const review = item.reviews[item.stage];
  const evidence = item.evidence.filter((entry) => entry.stage === item.stage);
  const issues: string[] = [];
  if (!review.reviewer.trim()) issues.push('Indica quién revisa esta etapa.');
  if (!review.notes.trim()) issues.push('Documenta el fundamento de la revisión.');
  if (!evidence.length) issues.push('Registra al menos una evidencia de esta etapa.');
  if (item.stage === 'indications') {
    if (!review.baseline.trim()) issues.push('Falta la referencia del levantamiento base.');
    if (!review.comparison.trim())
      issues.push('Falta la referencia del levantamiento de comparación.');
  }
  if (item.stage === 'reconnaissance') {
    if (!['confirmed', 'false-positive'].includes(review.finding))
      issues.push('El dictamen está pendiente o es inconcluso.');
    const sensors = new Set(
      evidence
        .filter((entry) => ['RGB', 'IR', 'GPR', 'Magnetometría'].includes(entry.sensor))
        .map((entry) => entry.sensor),
    );
    if (sensors.size < 2)
      issues.push('Registra evidencias de al menos dos sensores de reconocimiento.');
  }
  if (item.stage === 'surveillance' && review.environment !== 'reviewed')
    issues.push('La revisión del entorno está pendiente o bloqueada.');
  if (item.stage === 'mitigation') {
    if (!review.report.trim())
      issues.push('Falta la referencia del informe de cierre especializado.');
    if (!review.techint.trim()) issues.push('Falta la referencia de retroalimentación TECHINT.');
  }
  return issues;
}
