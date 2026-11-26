import { es as esWorkflow, WorkflowDictionary } from '../i18n/workflow-dictionary';
export type DetectionStage = 'indications' | 'reconnaissance' | 'surveillance' | 'mitigation';
export type CaseStatus = 'open' | 'closed' | 'discarded';
export type Priority = string;
export type Soil = string;
export type Season = string;
export type Sensor = string;

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
export const detectionLayers = (
  dictionary: WorkflowDictionary = esWorkflow,
): readonly LayerDefinition[] => [
  {
    id: 'indications',
    number: '00',
    title: dictionary.layers.indicationsTitle,
    subtitle: dictionary.layers.indicationsSubtitle,
    description: dictionary.layers.indicationsDescription,
    platform: dictionary.layers.indicationsPlatform,
    pages: '7–10',
  },
  {
    id: 'reconnaissance',
    number: '01',
    title: dictionary.layers.reconnaissanceTitle,
    subtitle: dictionary.layers.reconnaissanceSubtitle,
    description: dictionary.layers.reconnaissanceDescription,
    platform: dictionary.layers.reconnaissancePlatform,
    pages: '11–17',
  },
  {
    id: 'surveillance',
    number: '03',
    title: dictionary.layers.surveillanceTitle,
    subtitle: dictionary.layers.surveillanceSubtitle,
    description: dictionary.layers.surveillanceDescription,
    platform: dictionary.layers.surveillancePlatform,
    pages: '25–26',
  },
  {
    id: 'mitigation',
    number: '02',
    title: dictionary.layers.mitigationTitle,
    subtitle: dictionary.layers.mitigationSubtitle,
    description: dictionary.layers.mitigationDescription,
    platform: dictionary.layers.mitigationPlatform,
    pages: '18–24',
  },
];

export const DETECTION_LAYERS = detectionLayers();

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
  finding: string;
  environment: string;
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
  code?: string;
  parameters?: Record<string, string | number>;
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

export function stageLabel(
  stage: DetectionStage,
  dictionary: WorkflowDictionary = esWorkflow,
): string {
  return detectionLayers(dictionary).find((layer) => layer.id === stage)!.title;
}

export function transitionIssues(
  item: DetectionCase,
  dictionary: WorkflowDictionary = esWorkflow,
): string[] {
  if (item.status !== 'open') return [dictionary.issues.alreadyClosed];
  const review = item.reviews[item.stage];
  const evidence = item.evidence.filter((entry) => entry.stage === item.stage);
  const issues: string[] = [];
  if (!review.reviewer.trim()) issues.push(dictionary.issues.reviewer);
  if (!review.notes.trim()) issues.push(dictionary.issues.reviewNotes);
  if (!evidence.length) issues.push(dictionary.issues.phaseEvidence);
  if (item.stage === 'indications') {
    if (!review.baseline.trim()) issues.push(dictionary.issues.baseline);
    if (!review.comparison.trim()) issues.push(dictionary.issues.comparison);
  }
  if (item.stage === 'reconnaissance') {
    if (!['confirmed', 'false-positive'].includes(review.finding))
      issues.push(dictionary.issues.finding);
    const sensors = new Set(
      evidence
        .filter((entry) => ['RGB', 'IR', 'GPR', 'Magnetometría'].includes(entry.sensor))
        .map((entry) => entry.sensor),
    );
    if (sensors.size < 2) issues.push(dictionary.issues.reconnaissanceSensors);
  }
  if (item.stage === 'surveillance' && review.environment !== 'reviewed')
    issues.push(dictionary.issues.environmentReview);
  if (item.stage === 'mitigation') {
    if (!review.report.trim()) issues.push(dictionary.issues.closureReport);
    if (!review.techint.trim()) issues.push(dictionary.issues.techintReference);
  }
  return issues;
}
