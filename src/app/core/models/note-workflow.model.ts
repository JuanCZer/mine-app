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

export type IndicationCollectionSource = '' | 'if1200a-lidar' | 'if1200a-lidar-rgb-ir';
export type IndicationSignature =
  | 'surface-removal'
  | 'irregular-edge'
  | 'vegetation-change'
  | 'soil-density-change';

export const INDICATION_COLLECTION_SOURCES: ReadonlyArray<{
  value: Exclude<IndicationCollectionSource, ''>;
  label: string;
}> = [
  { value: 'if1200a-lidar', label: 'IF1200A · LiDAR' },
  { value: 'if1200a-lidar-rgb-ir', label: 'IF1200A · LiDAR + RGB + infrarrojo térmico' },
];

export const INDICATION_SIGNATURES: ReadonlyArray<{ id: IndicationSignature; label: string }> = [
  { id: 'surface-removal', label: 'Remoción o depresión de la superficie' },
  { id: 'irregular-edge', label: 'Perfil o borde irregular' },
  { id: 'vegetation-change', label: 'Alteración inmediata de la vegetación' },
  { id: 'soil-density-change', label: 'Compactación o esponjamiento localizado' },
];

export interface IndicationIntake {
  platform: '' | 'if1200a';
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

export type RiskLevel = '' | 'low' | 'medium' | 'high';
export type HumanActivity =
  | ''
  | 'not-detected'
  | 'movement-detected'
  | 'irregular-pattern'
  | 'custody-or-activation-risk';
export type EnvironmentDecision = '' | 'clear-for-mitigation' | 'hold-for-reassessment';
export type SurveillanceDeployment = '' | 'uav' | 'ground-backpack' | 'combined';
export type ProtectionLevel = '' | 'stanag-4' | 'stanag-5';
export type NeutralizationResult = '' | 'neutralized' | 'not-neutralized';
export type RouteResult = '' | 'route-opened' | 'not-applicable';

export const RADIO_TECHNOLOGIES = ['2G', '3G', '4G', '5G'] as const;
export type RadioTechnology = (typeof RADIO_TECHNOLOGIES)[number];
export const SURVEILLANCE_METHODS = [
  { id: 'imsi-imei-tmsi-capture', label: 'Captura IMSI, IMEI y TMSI' },
  { id: 'target-import-export', label: 'Importación o exportación de objetivos' },
  { id: 'precision-mapping', label: 'Mapeo y localización precisa' },
  { id: 'silent-call-df', label: 'Llamada silenciosa con radiogoniometría' },
  { id: 'imsi-imei-alert', label: 'Alerta por IMSI o IMEI de interés' },
  { id: 'operations-center-access', label: 'Acceso desde centro de operaciones' },
  { id: 'selective-signal-block', label: 'Bloqueo selectivo de señal' },
  { id: 'online-offline-mapping', label: 'Mapeo en línea o fuera de línea' },
  { id: 'uav-camera-feed', label: 'Integración con cámara UAV' },
  { id: 'gsma-tac', label: 'Consulta de base TAC de GSMA' },
  { id: 'target-api', label: 'Localización mediante API “Proceder al objetivo”' },
  { id: 'ground-refinement-realtime', label: 'Afinamiento terrestre y comunicaciones en tiempo real' },
  { id: 'radio-stack-configuration', label: 'Pila móvil con amplificadores, filtros y antenas' },
] as const;
export type SurveillanceMethod = (typeof SURVEILLANCE_METHODS)[number]['id'];

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
  platform: '' | 'husky-route-clearance';
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
export interface NotePhase {
  stage: DetectionStage;
  data: NotePhaseData;
  validatedAt: string | null;
  validatedBy: string | null;
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

export function notePhaseIssues(note: Note, stage: DetectionStage, data: NotePhaseData): string[] {
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
    issues.push('Completa y guarda el registro inicial de indicios y sus coordenadas.');
  if (stage === 'indications') {
    const indications = data.indications ?? emptyPhaseData().indications;
    if (!review.baseline.trim()) issues.push('Falta la referencia del levantamiento base.');
    if (!review.comparison.trim())
      issues.push('Falta la referencia del levantamiento de comparación.');
    if (indications.platform !== 'if1200a') issues.push('Indica la plataforma Inspired Flight IF1200A.');
    if (!indications.blueUasNdaaCompliant)
      issues.push('Confirma el cumplimiento Blue UAS / NDAA de la plataforma.');
    if (
      indications.flightDurationMin === null ||
      indications.flightDurationMin <= 0 ||
      indications.flightDurationMin > 43
    )
      issues.push('Registra una duración de vuelo entre 0 y 43 minutos.');
    if (indications.lidarPointRate === null || indications.lidarPointRate < 640000)
      issues.push('Registra una captura LiDAR de al menos 640,000 puntos por segundo.');
    if (
      indications.payloadLb === null ||
      indications.payloadLb <= 0 ||
      indications.payloadLb > 19.1
    )
      issues.push('Registra una carga útil entre 0 y 19.1 lb.');
    if (
      indications.missionSpeedMph === null ||
      indications.missionSpeedMph <= 0 ||
      indications.missionSpeedMph > 56
    )
      issues.push('Registra una velocidad de misión entre 0 y 56 mph.');
    if (indications.windKnots === null || indications.windKnots < 0 || indications.windKnots > 23)
      issues.push('Registra una resistencia de viento de hasta 23 nudos.');
    if (!indications.terrainModelReference.trim())
      issues.push('Agrega la referencia del modelo tridimensional denso del corredor.');
    if (!indications.differentialAnalysisCompleted)
      issues.push('Confirma la ejecución del algoritmo diferencial contra la línea base.');
    if (indications.surfaceVariationMm === null || indications.surfaceVariationMm <= 0)
      issues.push('Registra la variación milimétrica de superficie observada.');
    if (!indications.actionableAlertGenerated)
      issues.push('Confirma la generación de una alerta procesable de I&W.');
    if (!indications.personnelNotExposed)
      issues.push('Confirma que el levantamiento no expuso personal al área de riesgo.');
    if (!indications.coordinateTransferredToVeles)
      issues.push('Confirma la transferencia de la coordenada de anomalía a VELES.');
    if (!indications.observedAt) issues.push('Registra la fecha y hora del levantamiento comparativo.');
    if (!indications.collectionSource)
      issues.push('Indica la fuente LiDAR del levantamiento de Fase 1.');
    if (!indications.signatures.length)
      issues.push('Selecciona al menos una firma de alteración del terreno observada.');
    if (
      indications.signatures.includes('surface-removal') &&
      (indications.depressionDepthCm === null ||
        indications.depressionDepthCm < 5 ||
        indications.depressionDepthCm > 15)
    )
      issues.push('Para una depresión o remoción, registra una profundidad estimada entre 5 y 15 cm.');
    if (!data.terrainNotes.trim())
      issues.push('Describe las condiciones base de topografía, vegetación y suelo.');
    if (!indications.alternativeCauses.trim())
      issues.push(
        'Documenta las causas naturales o actividades civiles consideradas para descartar un falso positivo.',
      );
    if (!data.evidence.some((entry) => entry.sensor === 'LiDAR'))
      issues.push('Agrega la referencia de la nube de puntos o producto LiDAR que sustenta el indicio.');
  }
  if (stage === 'reconnaissance') {
    const reconnaissance = data.reconnaissance ?? emptyPhaseData().reconnaissance;
    if (!['confirmed', 'false-positive'].includes(review.finding))
      issues.push('El dictamen está pendiente o es inconcluso.');
    const sensors = new Set(data.evidence.map((entry) => entry.sensor));
    for (const sensor of ['GPR', 'Magnetometría', 'RGB', 'IR'] as const)
      if (!sensors.has(sensor)) issues.push(`Agrega la evidencia ${sensor} de VELES.`);
    if (reconnaissance.magneticReadingNt === null || reconnaissance.magneticReadingNt < 10)
      issues.push('Registra una lectura magnetométrica de al menos 10 nT.');
    if (
      reconnaissance.gprDepthCm === null ||
      reconnaissance.gprDepthCm <= 0 ||
      reconnaissance.gprDepthCm > 30
    )
      issues.push('Registra la profundidad GPR observada, hasta 30 cm.');
    if (
      reconnaissance.rtkAccuracyCm === null ||
      reconnaissance.rtkAccuracyCm < 1 ||
      reconnaissance.rtkAccuracyCm > 3
    )
      issues.push('Registra precisión RTK entre 1 y 3 cm.');
    if (
      reconnaissance.localizationAccuracyCm === null ||
      reconnaissance.localizationAccuracyCm <= 0 ||
      reconnaissance.localizationAccuracyCm > 5
    )
      issues.push('Registra la localización de la amenaza con precisión de hasta 5 cm.');
    if (!reconnaissance.probability || !reconnaissance.priority)
      issues.push('Clasifica la probabilidad y prioridad de la anomalía.');
    if (!reconnaissance.georeferencedPointReference.trim())
      issues.push('Agrega la referencia del punto georreferenciado resultante.');
    if (!reconnaissance.riskMapReference.trim()) issues.push('Agrega la referencia del mapa de riesgo.');
    if (!reconnaissance.aiClassificationReference.trim())
      issues.push('Agrega la referencia de clasificación IA de VELES.');
    if (!reconnaissance.fusionCompleted)
      issues.push('Confirma la fusión de GPR, magnetometría, RGB e infrarrojo térmico.');
  }
  if (stage === 'surveillance') {
    const surveillance = data.surveillance ?? emptyPhaseData().surveillance;
    const sensors = new Set(data.evidence.map((entry) => entry.sensor));
    if (!surveillance.observationStartedAt || !surveillance.observationEndedAt)
      issues.push('Registra el inicio y cierre del periodo de vigilancia.');
    if (!surveillance.radioTechnologies.length)
      issues.push('Indica las tecnologías móviles observadas: 2G, 3G, 4G o 5G.');
    if (!surveillance.methods.includes('imsi-imei-tmsi-capture'))
      issues.push('Registra la captura IMSI, IMEI y TMSI de la vigilancia.');
    if (!surveillance.methods.includes('uav-camera-feed'))
      issues.push('Registra la integración de cámara UAV para observación EO/IR.');
    if (!surveillance.methods.some((method) => method === 'precision-mapping' || method === 'silent-call-df' || method === 'target-api'))
      issues.push('Registra un método de geolocalización o radiogoniometría.');
    if (!surveillance.deployment) issues.push('Indica el despliegue de vigilancia utilizado.');
    if (
      (surveillance.deployment === 'uav' || surveillance.deployment === 'combined') &&
      (surveillance.coveredChannels === null || surveillance.coveredChannels < 2)
    )
      issues.push('Registra al menos dos canales de cobertura para el despliegue UAV.');
    if (surveillance.duplicatedChannelConfiguration && (surveillance.coveredChannels === null || surveillance.coveredChannels < 4))
      issues.push('La configuración duplicada requiere al menos cuatro canales de cobertura.');
    if (surveillance.activeDeviceCount === null || surveillance.activeDeviceCount < 0)
      issues.push('Registra el número de dispositivos móviles activos detectados.');
    if (!surveillance.humanActivity) issues.push('Clasifica la actividad humana observada.');
    if (surveillance.environmentDecision !== 'clear-for-mitigation')
      issues.push('La vigilancia debe certificar el entorno humano antes de pasar a mitigación.');
    if (!sensors.has('EO/IR')) issues.push('Agrega la evidencia de observación EO/IR.');
    if (!sensors.has('IMSI/IMEI/TMSI')) issues.push('Agrega la evidencia de captura IMSI, IMEI o TMSI.');
  }
  if (stage === 'mitigation') {
    const mitigation = data.mitigation ?? emptyPhaseData().mitigation;
    const sensors = new Set(data.evidence.map((entry) => entry.sensor));
    if (!mitigation.coordinateReceived) issues.push('Confirma la recepción de la coordenada exacta de VELES.');
    if (mitigation.platform !== 'husky-route-clearance') issues.push('Indica la plataforma Husky de limpieza de ruta.');
    if (!mitigation.protectionLevel) issues.push('Selecciona el nivel de protección STANAG 4569 utilizado.');
    if (!mitigation.personnelProtectionConfirmed || !mitigation.equipmentProtectionConfirmed)
      issues.push('Confirma la protección de personal y equipo antes de la limpieza de ruta.');
    if (mitigation.detectionAssets === null || mitigation.detectionAssets < 2)
      issues.push('Registra al menos dos medios de detección para la limpieza de ruta.');
    if (mitigation.interrogationAssets === null || mitigation.interrogationAssets < 1)
      issues.push('Registra al menos un medio de interrogación.');
    if (!mitigation.gprCoverageOverlap) issues.push('Confirma el traslape de cobertura GPR.');
    if (mitigation.safetyMarginM === null || mitigation.safetyMarginM <= 0)
      issues.push('Registra los márgenes de seguridad del brazo de interrogación.');
    if (!mitigation.remoteInterrogationCompleted) issues.push('Confirma la interrogación del objetivo a distancia segura.');
    if (!mitigation.artifactType.trim() || !mitigation.orientation.trim() || !mitigation.activationMechanism.trim())
      issues.push('Documenta tipo, orientación y mecanismo de activación antes de la entrega EOD.');
    if (!mitigation.eodHandoffReference.trim()) issues.push('Agrega la referencia de entrega al operador EOD.');
    if (mitigation.neutralizationResult !== 'neutralized') issues.push('Registra la neutralización controlada del dispositivo.');
    if (!mitigation.routeResult) issues.push('Registra el resultado de apertura de ruta segura.');
    if (!mitigation.techintComposition.trim() || !mitigation.techintManufacture.trim() || !mitigation.techintSignature.trim())
      issues.push('Documenta composición, manufactura y firma para TECHINT.');
    if (!mitigation.catalogFeedbackReference.trim()) issues.push('Agrega la referencia de retroalimentación al catálogo de IA de VELES.');
    for (const sensor of ['GPR', 'EOD', 'TECHINT'] as const)
      if (!sensors.has(sensor)) issues.push(`Agrega la evidencia ${sensor} de mitigación.`);
  }
  return issues;
}

export function noteProgress(note: Note): string {
  const workflow = note.workflow;
  if (workflow?.status === 'closed') return '4 de 4 fases validadas';
  if (workflow?.status === 'discarded') return 'Cerrada · falso positivo';
  const number =
    DETECTION_LAYERS.findIndex((layer) => layer.id === (workflow?.currentStage ?? 'indications')) +
    1;
  return `Fase ${number} de 4 · pendiente de validar`;
}
