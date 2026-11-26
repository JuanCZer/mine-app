import { es as esWorkflow, WorkflowDictionary } from '../i18n/workflow-dictionary';
import {
  CaseEvent,
  DetectionStage,
  DETECTION_LAYERS,
  emptyReview,
  Sensor,
  Soil,
  Season,
  StageReview,
} from './detection.model';
import type { Note } from './note.model';

export type IndicationCollectionSource = string;
export type IndicationSignature = string;

export interface IndicationIntake {
  platform: string;
  blueUasNdaaCompliant: boolean;
  flightDurationMin: number | null;
  lidarPointRate: number | null;
  payloadLb: number | null;
  missionSpeedMph: number | null;
  windKnots: number | null;
  terrainModelReference: string;
  differentialAnalysisCompleted: boolean;
  surfaceVariationMm: number | null;
  actionableAlertGenerated: boolean;
  personnelNotExposed: boolean;
  coordinateTransferredToVeles: boolean;
  observedAt: string;
  collectionSource: IndicationCollectionSource;
  signatures: IndicationSignature[];
  alternativeCauses: string;
  depressionDepthCm: number | null;
}

export type RiskLevel = string;
export type HumanActivity = string;
export type EnvironmentDecision = string;
export type SurveillanceDeployment = string;
export type ProtectionLevel = string;
export type NeutralizationResult = string;
export type RouteResult = string;
export type RadioTechnology = string;
export type SurveillanceMethod = string;

export interface ReconnaissanceIntake {
  magneticReadingNt: number | null;
  gprDepthCm: number | null;
  rtkAccuracyCm: number | null;
  localizationAccuracyCm: number | null;
  probability: RiskLevel;
  priority: RiskLevel;
  georeferencedPointReference: string;
  riskMapReference: string;
  aiClassificationReference: string;
  fusionCompleted: boolean;
}

export interface SurveillanceIntake {
  observationStartedAt: string;
  observationEndedAt: string;
  radioTechnologies: RadioTechnology[];
  methods: SurveillanceMethod[];
  deployment: SurveillanceDeployment;
  coveredChannels: number | null;
  duplicatedChannelConfiguration: boolean;
  activeDeviceCount: number | null;
  humanActivity: HumanActivity;
  environmentDecision: EnvironmentDecision;
}

export interface MitigationIntake {
  coordinateReceived: boolean;
  platform: string;
  protectionLevel: ProtectionLevel;
  personnelProtectionConfirmed: boolean;
  equipmentProtectionConfirmed: boolean;
  detectionAssets: number | null;
  interrogationAssets: number | null;
  gprCoverageOverlap: boolean;
  safetyMarginM: number | null;
  remoteInterrogationCompleted: boolean;
  artifactType: string;
  orientation: string;
  activationMechanism: string;
  eodHandoffReference: string;
  neutralizationResult: NeutralizationResult;
  routeResult: RouteResult;
  techintComposition: string;
  techintManufacture: string;
  techintSignature: string;
  catalogFeedbackReference: string;
}
export interface NoteEvidence {
  id: string;
  sensor: Sensor;
  reference: string;
  summary: string;
  recordedAt?: string | null;
}
export interface NotePhaseData {
  review: StageReview;
  soil: Soil;
  season: Season;
  terrainNotes: string;
  evidence: NoteEvidence[];
  indications: IndicationIntake;
  reconnaissance: ReconnaissanceIntake;
  surveillance: SurveillanceIntake;
  mitigation: MitigationIntake;
}
export interface CatalogSelectionSnapshot {
  field: string;
  catalogKey: string;
  code: string;
  label: string;
  version: number;
}
export interface CatalogValidationSnapshot {
  version: number;
  selections: CatalogSelectionSnapshot[];
}
export interface NotePhase {
  stage: DetectionStage;
  data: NotePhaseData;
  validatedAt: string | null;
  validatedBy: string | null;
  catalogSnapshot?: CatalogValidationSnapshot | null;
}
export interface NoteWorkflow {
  status: 'open' | 'closed' | 'discarded';
  currentStage: DetectionStage | null;
  phases: NotePhase[];
  history: CaseEvent[];
}

export const emptyPhaseData = (): NotePhaseData => ({
  review: emptyReview(),
  soil: 'unknown',
  season: 'unknown',
  terrainNotes: '',
  evidence: [],
  indications: {
    platform: '',
    blueUasNdaaCompliant: false,
    flightDurationMin: null,
    lidarPointRate: null,
    payloadLb: null,
    missionSpeedMph: null,
    windKnots: null,
    terrainModelReference: '',
    differentialAnalysisCompleted: false,
    surfaceVariationMm: null,
    actionableAlertGenerated: false,
    personnelNotExposed: false,
    coordinateTransferredToVeles: false,
    observedAt: '',
    collectionSource: '',
    signatures: [],
    alternativeCauses: '',
    depressionDepthCm: null,
  },
  reconnaissance: {
    magneticReadingNt: null,
    gprDepthCm: null,
    rtkAccuracyCm: null,
    localizationAccuracyCm: null,
    probability: '',
    priority: '',
    georeferencedPointReference: '',
    riskMapReference: '',
    aiClassificationReference: '',
    fusionCompleted: false,
  },
  surveillance: {
    observationStartedAt: '',
    observationEndedAt: '',
    radioTechnologies: [],
    methods: [],
    deployment: '',
    coveredChannels: null,
    duplicatedChannelConfiguration: false,
    activeDeviceCount: null,
    humanActivity: '',
    environmentDecision: '',
  },
  mitigation: {
    coordinateReceived: false,
    platform: '',
    protectionLevel: '',
    personnelProtectionConfirmed: false,
    equipmentProtectionConfirmed: false,
    detectionAssets: null,
    interrogationAssets: null,
    gprCoverageOverlap: false,
    safetyMarginM: null,
    remoteInterrogationCompleted: false,
    artifactType: '',
    orientation: '',
    activationMechanism: '',
    eodHandoffReference: '',
    neutralizationResult: '',
    routeResult: '',
    techintComposition: '',
    techintManufacture: '',
    techintSignature: '',
    catalogFeedbackReference: '',
  },
});
export const emptyNoteWorkflow = (): NoteWorkflow => ({
  status: 'open',
  currentStage: 'indications',
  phases: DETECTION_LAYERS.map((layer) => ({
    stage: layer.id,
    data: emptyPhaseData(),
    validatedAt: null,
    validatedBy: null,
  })),
  history: [],
});

export function notePhaseIssues(
  note: Note,
  stage: DetectionStage,
  data: NotePhaseData,
  dictionary: WorkflowDictionary = esWorkflow,
): string[] {
  const issues: string[] = [];
  const review = data.review;
  if (
    !note.title.trim() ||
    !note.content.trim() ||
    !note.state?.trim() ||
    note.altitude === null ||
    !Number.isFinite(note.altitude) ||
    Math.abs(note.altitude) > 90 ||
    note.longitude === null ||
    !Number.isFinite(note.longitude) ||
    Math.abs(note.longitude) > 180
  )
    issues.push(dictionary.issues.initialRecord);
  if (stage === 'indications') {
    const indications = data.indications ?? emptyPhaseData().indications;
    if (!review.baseline.trim()) issues.push(dictionary.issues.baseline);
    if (!review.comparison.trim()) issues.push(dictionary.issues.comparison);
    if (indications.platform !== 'if1200a') issues.push(dictionary.issues.indicationPlatform);
    if (!indications.blueUasNdaaCompliant) issues.push(dictionary.issues.platformCompliance);
    if (
      indications.flightDurationMin === null ||
      indications.flightDurationMin <= 0 ||
      indications.flightDurationMin > 43
    )
      issues.push(dictionary.issues.flightDuration);
    if (indications.lidarPointRate === null || indications.lidarPointRate < 640000)
      issues.push(dictionary.issues.lidarPointRate);
    if (
      indications.payloadLb === null ||
      indications.payloadLb <= 0 ||
      indications.payloadLb > 19.1
    )
      issues.push(dictionary.issues.payload);
    if (
      indications.missionSpeedMph === null ||
      indications.missionSpeedMph <= 0 ||
      indications.missionSpeedMph > 56
    )
      issues.push(dictionary.issues.missionSpeed);
    if (indications.windKnots === null || indications.windKnots < 0 || indications.windKnots > 23)
      issues.push(dictionary.issues.wind);
    if (!indications.terrainModelReference.trim()) issues.push(dictionary.issues.terrainModel);
    if (!indications.differentialAnalysisCompleted)
      issues.push(dictionary.issues.differentialAnalysis);
    if (indications.surfaceVariationMm === null || indications.surfaceVariationMm <= 0)
      issues.push(dictionary.issues.surfaceVariation);
    if (!indications.actionableAlertGenerated) issues.push(dictionary.issues.actionableAlert);
    if (!indications.personnelNotExposed) issues.push(dictionary.issues.personnelNotExposed);
    if (!indications.coordinateTransferredToVeles)
      issues.push(dictionary.issues.coordinateTransferred);
    if (!indications.observedAt) issues.push(dictionary.issues.observedAt);
    if (!indications.collectionSource) issues.push(dictionary.issues.collectionSource);
    if (!indications.signatures.length) issues.push(dictionary.issues.signatures);
    if (
      indications.signatures.includes('surface-removal') &&
      (indications.depressionDepthCm === null ||
        indications.depressionDepthCm < 5 ||
        indications.depressionDepthCm > 15)
    )
      issues.push(dictionary.issues.depressionDepth);
    if (!data.terrainNotes.trim()) issues.push(dictionary.issues.terrainNotes);
    if (!indications.alternativeCauses.trim()) issues.push(dictionary.issues.alternativeCauses);
    if (!data.evidence.some((entry) => entry.sensor === 'LiDAR'))
      issues.push(dictionary.issues.lidarEvidence);
  }
  if (stage === 'reconnaissance') {
    const reconnaissance = data.reconnaissance ?? emptyPhaseData().reconnaissance;
    if (!['confirmed', 'false-positive'].includes(review.finding))
      issues.push(dictionary.issues.finding);
    const sensors = new Set(data.evidence.map((entry) => entry.sensor));
    for (const sensor of ['GPR', 'Magnetometría', 'RGB', 'IR'] as const)
      if (!sensors.has(sensor))
        issues.push(
          dictionary.issues.velesEvidence.replace(
            '{sensor}',
            sensor === 'Magnetometría' ? dictionary.issues.magnetometrySensor : sensor,
          ),
        );
    if (reconnaissance.magneticReadingNt === null || reconnaissance.magneticReadingNt < 10)
      issues.push(dictionary.issues.magneticReading);
    if (
      reconnaissance.gprDepthCm === null ||
      reconnaissance.gprDepthCm <= 0 ||
      reconnaissance.gprDepthCm > 30
    )
      issues.push(dictionary.issues.gprDepth);
    if (
      reconnaissance.rtkAccuracyCm === null ||
      reconnaissance.rtkAccuracyCm < 1 ||
      reconnaissance.rtkAccuracyCm > 3
    )
      issues.push(dictionary.issues.rtkAccuracy);
    if (
      reconnaissance.localizationAccuracyCm === null ||
      reconnaissance.localizationAccuracyCm <= 0 ||
      reconnaissance.localizationAccuracyCm > 5
    )
      issues.push(dictionary.issues.localizationAccuracy);
    if (!reconnaissance.probability || !reconnaissance.priority)
      issues.push(dictionary.issues.risk);
    if (!reconnaissance.georeferencedPointReference.trim())
      issues.push(dictionary.issues.georeferencedPoint);
    if (!reconnaissance.riskMapReference.trim()) issues.push(dictionary.issues.riskMap);
    if (!reconnaissance.aiClassificationReference.trim())
      issues.push(dictionary.issues.aiClassification);
    if (!reconnaissance.fusionCompleted) issues.push(dictionary.issues.fusion);
  }
  if (stage === 'surveillance') {
    const surveillance = data.surveillance ?? emptyPhaseData().surveillance;
    const sensors = new Set(data.evidence.map((entry) => entry.sensor));
    if (!surveillance.observationStartedAt || !surveillance.observationEndedAt)
      issues.push(dictionary.issues.observationPeriod);
    if (!surveillance.radioTechnologies.length) issues.push(dictionary.issues.radioTechnologies);
    if (!surveillance.methods.includes('imsi-imei-tmsi-capture'))
      issues.push(dictionary.issues.identityCapture);
    if (!surveillance.methods.includes('uav-camera-feed')) issues.push(dictionary.issues.uavCamera);
    if (
      !surveillance.methods.some(
        (method) =>
          method === 'precision-mapping' || method === 'silent-call-df' || method === 'target-api',
      )
    )
      issues.push(dictionary.issues.geolocationMethod);
    if (!surveillance.deployment) issues.push(dictionary.issues.deployment);
    if (
      (surveillance.deployment === 'uav' || surveillance.deployment === 'combined') &&
      (surveillance.coveredChannels === null || surveillance.coveredChannels < 2)
    )
      issues.push(dictionary.issues.uavChannels);
    if (
      surveillance.duplicatedChannelConfiguration &&
      (surveillance.coveredChannels === null || surveillance.coveredChannels < 4)
    )
      issues.push(dictionary.issues.duplicatedChannels);
    if (surveillance.activeDeviceCount === null || surveillance.activeDeviceCount < 0)
      issues.push(dictionary.issues.activeDevices);
    if (!surveillance.humanActivity) issues.push(dictionary.issues.humanActivity);
    if (surveillance.environmentDecision !== 'clear-for-mitigation')
      issues.push(dictionary.issues.environmentDecision);
    if (!sensors.has('EO/IR')) issues.push(dictionary.issues.eoIrEvidence);
    if (!sensors.has('IMSI/IMEI/TMSI')) issues.push(dictionary.issues.identityEvidence);
  }
  if (stage === 'mitigation') {
    const mitigation = data.mitigation ?? emptyPhaseData().mitigation;
    const sensors = new Set(data.evidence.map((entry) => entry.sensor));
    if (!mitigation.coordinateReceived) issues.push(dictionary.issues.coordinateReceived);
    if (mitigation.platform !== 'husky-route-clearance')
      issues.push(dictionary.issues.mitigationPlatform);
    if (!mitigation.protectionLevel) issues.push(dictionary.issues.protectionLevel);
    if (!mitigation.personnelProtectionConfirmed || !mitigation.equipmentProtectionConfirmed)
      issues.push(dictionary.issues.protectionConfirmed);
    if (mitigation.detectionAssets === null || mitigation.detectionAssets < 2)
      issues.push(dictionary.issues.detectionAssets);
    if (mitigation.interrogationAssets === null || mitigation.interrogationAssets < 1)
      issues.push(dictionary.issues.interrogationAssets);
    if (!mitigation.gprCoverageOverlap) issues.push(dictionary.issues.gprOverlap);
    if (mitigation.safetyMarginM === null || mitigation.safetyMarginM <= 0)
      issues.push(dictionary.issues.safetyMargin);
    if (!mitigation.remoteInterrogationCompleted)
      issues.push(dictionary.issues.remoteInterrogation);
    if (
      !mitigation.artifactType.trim() ||
      !mitigation.orientation.trim() ||
      !mitigation.activationMechanism.trim()
    )
      issues.push(dictionary.issues.artifactDetails);
    if (!mitigation.eodHandoffReference.trim()) issues.push(dictionary.issues.eodHandoff);
    if (mitigation.neutralizationResult !== 'neutralized')
      issues.push(dictionary.issues.neutralization);
    if (!mitigation.routeResult) issues.push(dictionary.issues.routeResult);
    if (
      !mitigation.techintComposition.trim() ||
      !mitigation.techintManufacture.trim() ||
      !mitigation.techintSignature.trim()
    )
      issues.push(dictionary.issues.techintDetails);
    if (!mitigation.catalogFeedbackReference.trim()) issues.push(dictionary.issues.catalogFeedback);
    for (const sensor of ['GPR', 'EOD', 'TECHINT'] as const)
      if (!sensors.has(sensor))
        issues.push(dictionary.issues.mitigationEvidence.replace('{sensor}', sensor));
  }
  return issues;
}

export function noteProgress(note: Note, dictionary: WorkflowDictionary = esWorkflow): string {
  const workflow = note.workflow;
  if (workflow?.status === 'closed') return dictionary.progress.closed;
  if (workflow?.status === 'discarded') return dictionary.progress.discarded;
  const number =
    DETECTION_LAYERS.findIndex((layer) => layer.id === (workflow?.currentStage ?? 'indications')) +
    1;
  return dictionary.progress.pending.replace('{phase}', String(number));
}
