import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, signal, ViewChild } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { CaseTimeline } from '../../components/detection/case-timeline';
import { EvidencePanel } from '../../components/detection/evidence-panel';
import { LayerPipeline } from '../../components/detection/layer-pipeline';
import { TerrainContext } from '../../components/detection/terrain-context';
import {
  DetectionStage,
  DETECTION_LAYERS,
  Evidence,
  PRIORITY_LABELS,
  StageReview,
  transitionIssues,
} from '../../core/models/detection.model';
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
  readonly issues = computed(() => (this.item() ? transitionIssues(this.item()!) : []));
  readonly dirty = signal(false);
  readonly message = signal('');
  readonly error = signal('');
  readonly priorities = PRIORITY_LABELS;
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
    if (!item || !this.editable() || this.form.invalid) return;
    this.perform(() => {
      this.store.saveReview(item.id, this.selected(), this.form.getRawValue());
      this.dirty.set(false);
    }, 'Revisión guardada en este navegador.');
  }
  addEvidence(input: Pick<Evidence, 'sensor' | 'reference' | 'summary'>) {
    const item = this.item();
    if (!item) return;
    this.perform(() => {
      this.store.addEvidence(item.id, { ...input, stage: this.selected() });
      this.evidencePanel?.reset();
    }, 'Referencia de evidencia registrada.');
  }
  advance() {
    const item = this.item();
    if (!item || this.dirty()) return;
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
