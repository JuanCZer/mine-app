import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, signal, ViewChild } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CaseTimeline } from '../../components/detection/case-timeline';
import { EvidencePanel } from '../../components/detection/evidence-panel';
import { LayerPipeline } from '../../components/detection/layer-pipeline';
import { TerrainContext } from '../../components/detection/terrain-context';
import {
  DetectionStage,
  DETECTION_LAYERS,
  Evidence,
  StageReview,
  transitionIssues,
} from '../../core/models/detection.model';
import { CatalogItem, CatalogKey } from '../../core/models/catalog.model';
import { CatalogsService } from '../../core/services/catalogs.service';
import { DetectionService } from '../../core/services/detection.service';
import { downloadCases } from '../../core/services/detection-export';

@Component({
  selector: 'app-case-detail',
  imports: [
    RouterLink,
    DatePipe,
    DecimalPipe,
    ReactiveFormsModule,
    LayerPipeline,
    TerrainContext,
    EvidencePanel,
    CaseTimeline,
  ],
  templateUrl: './case-detail.html',
  styleUrl: './case-detail.less',
})
export class CaseDetailPage {
  readonly store = inject(DetectionService);
  private readonly catalogsService = inject(CatalogsService);
  private readonly params = toSignal(inject(ActivatedRoute).paramMap);
  @ViewChild(EvidencePanel) evidencePanel?: EvidencePanel;
  readonly item = computed(() =>
    this.store.cases().find((entry) => entry.id === this.params()?.get('id')),
  );
  readonly selected = signal<DetectionStage>('indications');
  readonly layer = computed(() => DETECTION_LAYERS.find((entry) => entry.id === this.selected())!);
  readonly editable = computed(
    () => this.item()?.status === 'open' && this.item()?.stage === this.selected(),
  );
  readonly evidence = computed(
    () => this.item()?.evidence.filter((entry) => entry.stage === this.selected()) ?? [],
  );
  readonly evidenceSensorCodes = computed(() => this.evidence().map((entry) => entry.sensor));
  readonly issues = computed(() => (this.item() ? transitionIssues(this.item()!) : []));
  readonly dirty = signal(false);
  readonly message = signal('');
  readonly error = signal('');
  readonly catalogs = signal<Partial<Record<CatalogKey, CatalogItem[]>>>({});
  readonly catalogsLoading = signal(true);
  readonly catalogsError = signal('');
  private catalogRequest = 0;
  readonly catalogKeys = computed<readonly CatalogKey[]>(() => [
    'riskLevels',
    'workflowStatuses',
    'mexicoStates',
    'soils',
    'seasons',
    'sensors',
    ...(this.selected() === 'reconnaissance' ? (['findings'] as CatalogKey[]) : []),
    ...(this.selected() === 'surveillance' ? (['environmentReviews'] as CatalogKey[]) : []),
  ]);
  readonly catalogsReady = computed(
    () =>
      !this.catalogsLoading() &&
      !this.catalogsError() &&
      [
        'sensors',
        ...(this.selected() === 'reconnaissance' ? ['findings'] : []),
        ...(this.selected() === 'surveillance' ? ['environmentReviews'] : []),
      ].every((key) => this.catalogIsUsable(key as CatalogKey)),
  );
  private loadedKey = '';
  private loadedId = '';
  readonly form = new FormGroup({
    baseline: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    comparison: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    finding: new FormControl<StageReview['finding']>('pending', { nonNullable: true }),
    environment: new FormControl<StageReview['environment']>('pending', { nonNullable: true }),
    report: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    techint: new FormControl('', { nonNullable: true, validators: Validators.maxLength(500) }),
    reviewer: new FormControl('', { nonNullable: true, validators: Validators.maxLength(100) }),
    notes: new FormControl('', { nonNullable: true, validators: Validators.maxLength(5000) }),
  });

  constructor() {
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => this.dirty.set(true));
    effect(() => {
      const item = this.item();
      if (item && this.loadedId !== item.id) {
        this.loadedId = item.id;
        this.selected.set(item.stage);
      }
      if (!item) return;
      const review = item.reviews[this.selected()];
      const key = `${item.id}:${this.selected()}:${this.editable()}:${JSON.stringify(review)}`;
      if (key === this.loadedKey) return;
      this.loadedKey = key;
      this.form.reset(review, { emitEvent: false });
      if (this.editable()) this.form.enable({ emitEvent: false });
      else this.form.disable({ emitEvent: false });
      this.dirty.set(false);
    });
    effect(() => {
      const stage = this.selected();
      this.catalogKeys();
      void this.loadCatalogs(stage);
    });
  }

  async loadCatalogs(stage = this.selected(), reload = false) {
    const requestId = ++this.catalogRequest;
    this.catalogsLoading.set(true);
    this.catalogsError.set('');
    const keys = this.catalogKeys();
    try {
      const response = await firstValueFrom(
        reload
          ? this.catalogsService.reload(keys, { stage, includeInactive: true })
          : this.catalogsService.load(keys, { stage, includeInactive: true }),
      );
      if (requestId !== this.catalogRequest || stage !== this.selected()) return;
      this.catalogs.set(response.catalogs);
    } catch {
      if (requestId !== this.catalogRequest) return;
      this.catalogsError.set('No fue posible cargar los catálogos. El expediente y los cambios locales se conservan.');
    } finally {
      if (requestId === this.catalogRequest) this.catalogsLoading.set(false);
    }
  }
  options(key: CatalogKey, selected: string | readonly string[] = []) {
    return this.catalogsService.selectable(this.catalogs()[key] ?? [], selected);
  }
  label(key: CatalogKey, code: string) {
    return this.catalogsService.label(this.catalogs()[key] ?? [], code, code);
  }
  private catalogIsUsable(key: CatalogKey): boolean {
    const items = this.catalogs()[key] ?? [];
    if (items.some((item) => item.active)) return true;
    const selected =
      key === 'sensors'
        ? this.evidenceSensorCodes()
        : key === 'findings'
          ? [this.form.controls.finding.value]
          : key === 'environmentReviews'
            ? [this.form.controls.environment.value]
            : [];
    const codes = selected.filter(Boolean);
    return codes.length > 0 && codes.every((code) => items.some((item) => item.code === code));
  }

  select(stage: DetectionStage) {
    if (this.dirty()) {
      this.error.set('Guarda la revisión antes de consultar otra etapa.');
      return;
    }
    this.selected.set(stage);
    this.message.set('');
    this.error.set('');
    this.evidencePanel?.reset();
  }
  save() {
    const item = this.item();
    if (!item || !this.editable() || this.form.invalid || !this.catalogsReady()) return;
    this.perform(() => {
      this.store.saveReview(item.id, this.selected(), this.form.getRawValue());
      this.dirty.set(false);
    }, 'Revisión guardada en este navegador.');
  }
  addEvidence(input: Pick<Evidence, 'sensor' | 'reference' | 'summary'>) {
    const item = this.item();
    if (!item || !this.catalogsReady()) return;
    this.perform(() => {
      this.store.addEvidence(item.id, { ...input, stage: this.selected() });
      this.evidencePanel?.reset();
    }, 'Referencia de evidencia registrada.');
  }
  advance() {
    const item = this.item();
    if (!item || this.dirty() || !this.catalogsReady()) return;
    this.perform(() => {
      this.store.advance(item.id);
      this.selected.set(this.item()!.stage);
    }, 'Estado del expediente actualizado.');
  }
  export() {
    if (this.item()) downloadCases([this.item()!], 'json');
  }
  private perform(action: () => void, success: string) {
    this.error.set('');
    this.message.set('');
    try {
      action();
      this.message.set(success);
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'No se pudo guardar el cambio.');
    }
  }
}
