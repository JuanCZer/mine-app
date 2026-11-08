import { DatePipe } from '@angular/common';
import {
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  ViewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CatalogItem, CatalogKey } from '../../core/models/catalog.model';
import {
  DETECTION_LAYERS,
  Evidence,
  Season,
  Sensor,
  Soil,
  StageReview,
} from '../../core/models/detection.model';
import { Note } from '../../core/models/note.model';
import {
  emptyPhaseData,
  EnvironmentDecision,
  IndicationCollectionSource,
  IndicationSignature,
  HumanActivity,
  MitigationIntake,
  NotePhase,
  NotePhaseData,
  notePhaseIssues,
  NeutralizationResult,
  ProtectionLevel,
  RadioTechnology,
  ReconnaissanceIntake,
  RiskLevel,
  RouteResult,
  SurveillanceDeployment,
  SurveillanceMethod,
} from '../../core/models/note-workflow.model';
import { CatalogsService } from '../../core/services/catalogs.service';
import { EvidencePanel } from '../detection/evidence-panel';

@Component({
  selector: 'app-note-phase-editor',
  imports: [ReactiveFormsModule, DatePipe, EvidencePanel],
  templateUrl: './note-phase-editor.html',
  styleUrl: './note-phase-editor.less',
})
export class NotePhaseEditor {
  private readonly catalogsService = inject(CatalogsService);
  readonly note = input.required<Note>();
  readonly phase = input.required<NotePhase>();
  readonly editable = input(false);
  readonly busy = input(false);
  readonly basePending = input(false);
  readonly basePendingMessage = input(
    'Guarda primero los cambios en los datos generales de la incidencia.',
  );
  readonly resetToken = input(0);
  readonly saved = output<NotePhaseData>();
  readonly validated = output<void>();
  readonly dirtyChange = output<boolean>();
  readonly draft = signal<NotePhaseData>(emptyPhaseData());
  private readonly reviewDirty = signal(false);
  readonly pendingEvidence = signal(false);
  readonly dirty = computed(() => this.reviewDirty() || this.pendingEvidence());
  readonly definition = computed(() =>
    DETECTION_LAYERS.find((layer) => layer.id === this.phase().stage)!,
  );
  readonly phaseNumber = computed(
    () => DETECTION_LAYERS.findIndex((layer) => layer.id === this.phase().stage) + 1,
  );
  readonly issues = computed(() => notePhaseIssues(this.note(), this.phase().stage, this.draft()));
  readonly validationLabel = computed(() =>
    this.phase().stage === 'mitigation'
      ? 'Validar fase 4 y cerrar proceso'
      : this.phase().stage === 'reconnaissance' && this.draft().review.finding === 'false-positive'
        ? 'Validar y cerrar como falso positivo'
        : `Validar fase ${this.phaseNumber()} y habilitar fase ${this.phaseNumber() + 1}`,
  );
  readonly catalogs = signal<Partial<Record<CatalogKey, CatalogItem[]>>>({});
  readonly catalogsLoading = signal(true);
  readonly catalogsError = signal('');
  private catalogRequest = 0;
  readonly stageKeys = computed<readonly CatalogKey[]>(() => {
    const common: CatalogKey[] = ['sensors'];
    switch (this.phase().stage) {
      case 'indications':
        return [
          ...common,
          'soils',
          'seasons',
          'platforms',
          'indicationSources',
          'indicationSignatures',
        ];
      case 'reconnaissance':
        return [...common, 'riskLevels', 'findings'];
      case 'surveillance':
        return [
          ...common,
          'radioTechnologies',
          'surveillanceMethods',
          'surveillanceDeployments',
          'humanActivities',
          'environmentDecisions',
        ];
      case 'mitigation':
        return [
          ...common,
          'platforms',
          'protectionLevels',
          'neutralizationResults',
          'routeResults',
        ];
    }
  });
  readonly catalogsReady = computed(
    () =>
      !this.catalogsLoading() &&
      !this.catalogsError() &&
      this.stageKeys().every((key) => this.catalogIsUsable(key)),
  );
  readonly evidenceSensors = computed(() =>
    this.options(
      'sensors',
      this.draft().evidence.map((item) => item.sensor),
    ),
  );
  @ViewChild(EvidencePanel) evidencePanel?: EvidencePanel;
  private loadedKey = '';
  readonly form = new FormGroup({
    baseline: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    comparison: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    finding: new FormControl<StageReview['finding']>('pending', { nonNullable: true }),
    soil: new FormControl<Soil>('unknown', { nonNullable: true }),
    season: new FormControl<Season>('unknown', { nonNullable: true }),
    terrainNotes: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(2000),
    }),
    indicationPlatform: new FormControl<string>('', { nonNullable: true }),
    blueUasNdaaCompliant: new FormControl(false, { nonNullable: true }),
    flightDurationMin: new FormControl<number | null>(null, [
      Validators.min(0.1),
      Validators.max(43),
    ]),
    lidarPointRate: new FormControl<number | null>(null, [
      Validators.min(640000),
      Validators.max(5000000),
    ]),
    payloadLb: new FormControl<number | null>(null, [Validators.min(0.1), Validators.max(19.1)]),
    missionSpeedMph: new FormControl<number | null>(null, [
      Validators.min(0.1),
      Validators.max(56),
    ]),
    windKnots: new FormControl<number | null>(null, [Validators.min(0), Validators.max(23)]),
    terrainModelReference: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(500),
    }),
    differentialAnalysisCompleted: new FormControl(false, { nonNullable: true }),
    surfaceVariationMm: new FormControl<number | null>(null, [
      Validators.min(0.001),
      Validators.max(1000),
    ]),
    actionableAlertGenerated: new FormControl(false, { nonNullable: true }),
    personnelNotExposed: new FormControl(false, { nonNullable: true }),
    coordinateTransferredToVeles: new FormControl(false, { nonNullable: true }),
    observedAt: new FormControl('', { nonNullable: true, validators: Validators.maxLength(50) }),
    collectionSource: new FormControl<IndicationCollectionSource>('', { nonNullable: true }),
    signatures: new FormControl<IndicationSignature[]>([], { nonNullable: true }),
    alternativeCauses: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(2000),
    }),
    depressionDepthCm: new FormControl<number | null>(null, [
      Validators.min(5),
      Validators.max(15),
    ]),
    magneticReadingNt: new FormControl<number | null>(null, [
      Validators.min(0),
      Validators.max(100000),
    ]),
    gprDepthCm: new FormControl<number | null>(null, [Validators.min(0), Validators.max(30)]),
    rtkAccuracyCm: new FormControl<number | null>(null, [Validators.min(1), Validators.max(3)]),
    localizationAccuracyCm: new FormControl<number | null>(null, [
      Validators.min(0.1),
      Validators.max(5),
    ]),
    probability: new FormControl<RiskLevel>('', { nonNullable: true }),
    priority: new FormControl<RiskLevel>('', { nonNullable: true }),
    georeferencedPointReference: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(500),
    }),
    riskMapReference: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(500),
    }),
    aiClassificationReference: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(500),
    }),
    fusionCompleted: new FormControl(false, { nonNullable: true }),
    observationStartedAt: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(50),
    }),
    observationEndedAt: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(50),
    }),
    radioTechnologies: new FormControl<RadioTechnology[]>([], { nonNullable: true }),
    methods: new FormControl<SurveillanceMethod[]>([], { nonNullable: true }),
    deployment: new FormControl<SurveillanceDeployment>('', { nonNullable: true }),
    coveredChannels: new FormControl<number | null>(null, [Validators.min(0), Validators.max(100)]),
    duplicatedChannelConfiguration: new FormControl(false, { nonNullable: true }),
    activeDeviceCount: new FormControl<number | null>(null, [
      Validators.min(0),
      Validators.max(100000),
    ]),
    humanActivity: new FormControl<HumanActivity>('', { nonNullable: true }),
    environmentDecision: new FormControl<EnvironmentDecision>('', { nonNullable: true }),
    coordinateReceived: new FormControl(false, { nonNullable: true }),
    platform: new FormControl<MitigationIntake['platform']>('', { nonNullable: true }),
    protectionLevel: new FormControl<ProtectionLevel>('', { nonNullable: true }),
    personnelProtectionConfirmed: new FormControl(false, { nonNullable: true }),
    equipmentProtectionConfirmed: new FormControl(false, { nonNullable: true }),
    detectionAssets: new FormControl<number | null>(null, [Validators.min(0), Validators.max(100)]),
    interrogationAssets: new FormControl<number | null>(null, [
      Validators.min(0),
      Validators.max(100),
    ]),
    gprCoverageOverlap: new FormControl(false, { nonNullable: true }),
    safetyMarginM: new FormControl<number | null>(null, [
      Validators.min(0.1),
      Validators.max(100000),
    ]),
    remoteInterrogationCompleted: new FormControl(false, { nonNullable: true }),
    artifactType: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    orientation: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    activationMechanism: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(500),
    }),
    eodHandoffReference: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(500),
    }),
    neutralizationResult: new FormControl<NeutralizationResult>('', { nonNullable: true }),
    routeResult: new FormControl<RouteResult>('', { nonNullable: true }),
    techintComposition: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(2000),
    }),
    techintManufacture: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(2000),
    }),
    techintSignature: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(2000),
    }),
    catalogFeedbackReference: new FormControl('', {
      nonNullable: true,
      validators: Validators.maxLength(500),
    }),
  });

  constructor() {
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.capture();
      this.reviewDirty.set(true);
    });
    effect(() => this.dirtyChange.emit(this.dirty()));
    effect(() => {
      const phase = this.phase();
      const key = `${this.resetToken()}:${this.note().id}:${phase.stage}:${phase.validatedAt}:${JSON.stringify(phase.data)}`;
      if (key === this.loadedKey) return;
      this.loadedKey = key;
      const defaults = emptyPhaseData();
      const loaded = structuredClone(phase.data);
      const data = {
        ...defaults,
        ...loaded,
        indications: {
          ...defaults.indications,
          ...loaded.indications,
          signatures: loaded.indications?.signatures ?? [],
        },
        reconnaissance: { ...defaults.reconnaissance, ...loaded.reconnaissance },
        surveillance: { ...defaults.surveillance, ...loaded.surveillance },
        mitigation: { ...defaults.mitigation, ...loaded.mitigation },
      };
      this.form.reset(
        {
          ...data.review,
          soil: data.soil,
          season: data.season,
          terrainNotes: data.terrainNotes,
          ...data.indications,
          indicationPlatform: data.indications.platform,
          ...data.reconnaissance,
          ...data.surveillance,
          ...data.mitigation,
        },
        { emitEvent: false },
      );
      this.draft.set(data);
      this.reviewDirty.set(false);
      this.pendingEvidence.set(false);
      this.evidencePanel?.reset();
    });
    effect(() => {
      const stage = this.phase().stage;
      this.stageKeys();
      void this.loadCatalogs(stage);
    });
    effect(() => {
      if (this.editable() && !this.busy()) this.form.enable({ emitEvent: false });
      else this.form.disable({ emitEvent: false });
    });
  }
  addEvidence(input: Pick<Evidence, 'sensor' | 'reference' | 'summary'>) {
    this.draft.update((data) => ({
      ...data,
      evidence: [...data.evidence, { ...input, id: crypto.randomUUID() }],
    }));
    this.reviewDirty.set(true);
    this.evidencePanel?.reset();
  }
  removeEvidence(id: string) {
    this.draft.update((data) => ({
      ...data,
      evidence: data.evidence.filter((entry) => entry.id !== id),
    }));
    this.reviewDirty.set(true);
  }
  save() {
    if (
      !this.busy() &&
      !this.basePending() &&
      !this.pendingEvidence() &&
      this.catalogsReady() &&
      this.form.valid &&
      this.editable()
    )
      this.saved.emit(this.draft());
  }
  validate() {
    if (
      !this.busy() &&
      !this.basePending() &&
      !this.dirty() &&
      !this.issues().length &&
      this.catalogsReady() &&
      this.editable()
    )
      this.validated.emit();
  }
  markSaved() {
    this.reviewDirty.set(false);
  }
  toggleSignature(signature: IndicationSignature, checked: boolean) {
    const selected = this.form.controls.signatures.value;
    this.form.controls.signatures.setValue(
      checked
        ? [...new Set([...selected, signature])]
        : selected.filter((item) => item !== signature),
    );
  }
  toggleRadioTechnology(technology: RadioTechnology, checked: boolean) {
    const selected = this.form.controls.radioTechnologies.value;
    this.form.controls.radioTechnologies.setValue(
      checked
        ? [...new Set([...selected, technology])]
        : selected.filter((item) => item !== technology),
    );
  }
  toggleSurveillanceMethod(method: SurveillanceMethod, checked: boolean) {
    const selected = this.form.controls.methods.value;
    this.form.controls.methods.setValue(
      checked ? [...new Set([...selected, method])] : selected.filter((item) => item !== method),
    );
  }
  async loadCatalogs(stage = this.phase().stage, reload = false) {
    const requestId = ++this.catalogRequest;
    this.catalogsLoading.set(true);
    this.catalogsError.set('');
    const keys = this.stageKeys();
    try {
      const response = await firstValueFrom(
        reload
          ? this.catalogsService.reload(keys, { stage, includeInactive: true })
          : this.catalogsService.load(keys, { stage, includeInactive: true }),
      );
      if (requestId !== this.catalogRequest || stage !== this.phase().stage) return;
      this.catalogs.set(response.catalogs);
    } catch {
      if (requestId !== this.catalogRequest) return;
      this.catalogsError.set(
        'No fue posible cargar los catálogos de esta fase. El borrador se conserva.',
      );
    } finally {
      if (requestId === this.catalogRequest) this.catalogsLoading.set(false);
    }
  }
  options(key: CatalogKey, selected: string | readonly string[] = []): CatalogItem[] {
    const selectedCodes = new Set(typeof selected === 'string' ? [selected] : selected);
    const historical = new Map(
      (this.phase().catalogSnapshot?.selections ?? [])
        .filter((item) => item.catalogKey === key && selectedCodes.has(item.code))
        .map((item) => [item.code, item]),
    );
    const current = this.catalogs()[key] ?? [];
    const merged = current.map((item) => {
      const frozen = historical.get(item.code);
      return frozen ? { ...item, label: frozen.label } : item;
    });
    for (const frozen of historical.values()) {
      if (merged.some((item) => item.code === frozen.code)) continue;
      merged.push({
        id: `snapshot:${frozen.catalogKey}:${frozen.code}`,
        code: frozen.code,
        label: frozen.label,
        sortOrder: Number.MAX_SAFE_INTEGER,
        active: false,
      });
    }
    return this.catalogsService.selectable(merged, selected);
  }
  private catalogIsUsable(key: CatalogKey): boolean {
    const items = this.catalogs()[key] ?? [];
    if (items.some((item) => item.active)) return true;
    const selected = this.selectedCatalogCodes(key).filter(Boolean);
    return (
      selected.length > 0 && selected.every((code) => items.some((item) => item.code === code))
    );
  }
  private selectedCatalogCodes(key: CatalogKey): string[] {
    const value = this.form.getRawValue();
    switch (key) {
      case 'sensors':
        return this.draft().evidence.map((item) => item.sensor);
      case 'soils':
        return [value.soil];
      case 'seasons':
        return [value.season];
      case 'platforms':
        return [this.phase().stage === 'indications' ? value.indicationPlatform : value.platform];
      case 'indicationSources':
        return [value.collectionSource];
      case 'indicationSignatures':
        return value.signatures;
      case 'riskLevels':
        return [value.probability, value.priority];
      case 'findings':
        return [value.finding];
      case 'radioTechnologies':
        return value.radioTechnologies;
      case 'surveillanceMethods':
        return value.methods;
      case 'surveillanceDeployments':
        return [value.deployment];
      case 'humanActivities':
        return [value.humanActivity];
      case 'environmentDecisions':
        return [value.environmentDecision];
      case 'protectionLevels':
        return [value.protectionLevel];
      case 'neutralizationResults':
        return [value.neutralizationResult];
      case 'routeResults':
        return [value.routeResult];
      default:
        return [];
    }
  }
  private capture() {
    const value = this.form.getRawValue();
    this.draft.update((data) => ({
      ...data,
      review: {
        ...data.review,
        baseline: value.baseline,
        comparison: value.comparison,
        finding: value.finding,
      },
      soil: value.soil,
      season: value.season,
      terrainNotes: value.terrainNotes,
      indications: {
        platform: value.indicationPlatform,
        blueUasNdaaCompliant: value.blueUasNdaaCompliant,
        flightDurationMin: value.flightDurationMin,
        lidarPointRate: value.lidarPointRate,
        payloadLb: value.payloadLb,
        missionSpeedMph: value.missionSpeedMph,
        windKnots: value.windKnots,
        terrainModelReference: value.terrainModelReference,
        differentialAnalysisCompleted: value.differentialAnalysisCompleted,
        surfaceVariationMm: value.surfaceVariationMm,
        actionableAlertGenerated: value.actionableAlertGenerated,
        personnelNotExposed: value.personnelNotExposed,
        coordinateTransferredToVeles: value.coordinateTransferredToVeles,
        observedAt: value.observedAt,
        collectionSource: value.collectionSource,
        signatures: value.signatures,
        alternativeCauses: value.alternativeCauses,
        depressionDepthCm: value.depressionDepthCm,
      },
      reconnaissance: {
        magneticReadingNt: value.magneticReadingNt,
        gprDepthCm: value.gprDepthCm,
        rtkAccuracyCm: value.rtkAccuracyCm,
        localizationAccuracyCm: value.localizationAccuracyCm,
        probability: value.probability,
        priority: value.priority,
        georeferencedPointReference: value.georeferencedPointReference,
        riskMapReference: value.riskMapReference,
        aiClassificationReference: value.aiClassificationReference,
        fusionCompleted: value.fusionCompleted,
      } as ReconnaissanceIntake,
      surveillance: {
        observationStartedAt: value.observationStartedAt,
        observationEndedAt: value.observationEndedAt,
        radioTechnologies: value.radioTechnologies,
        methods: value.methods,
        deployment: value.deployment,
        coveredChannels: value.coveredChannels,
        duplicatedChannelConfiguration: value.duplicatedChannelConfiguration,
        activeDeviceCount: value.activeDeviceCount,
        humanActivity: value.humanActivity,
        environmentDecision: value.environmentDecision,
      },
      mitigation: {
        coordinateReceived: value.coordinateReceived,
        platform: value.platform,
        protectionLevel: value.protectionLevel,
        personnelProtectionConfirmed: value.personnelProtectionConfirmed,
        equipmentProtectionConfirmed: value.equipmentProtectionConfirmed,
        detectionAssets: value.detectionAssets,
        interrogationAssets: value.interrogationAssets,
        gprCoverageOverlap: value.gprCoverageOverlap,
        safetyMarginM: value.safetyMarginM,
        remoteInterrogationCompleted: value.remoteInterrogationCompleted,
        artifactType: value.artifactType,
        orientation: value.orientation,
        activationMechanism: value.activationMechanism,
        eodHandoffReference: value.eodHandoffReference,
        neutralizationResult: value.neutralizationResult,
        routeResult: value.routeResult,
        techintComposition: value.techintComposition,
        techintManufacture: value.techintManufacture,
        techintSignature: value.techintSignature,
        catalogFeedbackReference: value.catalogFeedbackReference,
      },
    }));
  }
}
