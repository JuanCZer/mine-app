import { Injectable, signal } from '@angular/core';
import {
  CaseInput,
  DetectionCase,
  DetectionStage,
  DETECTION_LAYERS,
  emptyReview,
  Evidence,
  StageReview,
  stageLabel,
  transitionIssues,
} from '../models/detection.model';

const STORAGE_KEY = 'gds-detection-cases-v1';

@Injectable({ providedIn: 'root' })
export class DetectionService {
  private readonly records = signal<DetectionCase[]>([]);
  readonly cases = this.records.asReadonly();
  readonly storageError = signal('');
  private storageReadable = true;

  constructor() {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const saved = JSON.parse(raw);
      if (saved.version !== 1 || !Array.isArray(saved.cases) || !saved.cases.every(isDetectionCase))
        throw new Error('Formato de datos incompatible.');
      this.records.set(saved.cases);
    } catch {
      this.storageReadable = false;
      this.storageError.set(
        'No se pudieron leer los expedientes locales. No se sobrescribirán los datos existentes. Revisa el almacenamiento del navegador.',
      );
    }
  }

  create(input: CaseInput, demo = false): DetectionCase {
    if (
      !input.title.trim() ||
      !input.owner.trim() ||
      !input.description.trim() ||
      !input.state.trim() ||
      !input.municipality.trim()
    )
      throw new Error('Completa los datos del expediente.');
    if (
      !Number.isFinite(input.latitude) ||
      Math.abs(input.latitude) > 90 ||
      !Number.isFinite(input.longitude) ||
      Math.abs(input.longitude) > 180
    )
      throw new Error('Las coordenadas no son válidas.');
    const now = new Date().toISOString();
    const id = crypto.randomUUID();
    const item: DetectionCase = {
      ...input,
      title: input.title.trim(),
      owner: input.owner.trim(),
      id,
      code: `GDS-${new Date().getFullYear()}-${id.slice(0, 6).toUpperCase()}`,
      stage: 'indications',
      status: 'open',
      demo,
      createdAt: now,
      updatedAt: now,
      reviews: {
        indications: emptyReview(),
        reconnaissance: emptyReview(),
        surveillance: emptyReview(),
        mitigation: emptyReview(),
      },
      evidence: [],
      history: [
        {
          id: crypto.randomUUID(),
          at: now,
          actor: input.owner.trim(),
          text: demo ? 'Ejemplo ficticio creado.' : 'Expediente creado. Pendiente de revisión I&W.',
        },
      ],
    };
    this.commit([item, ...this.records()]);
    return item;
  }

  saveReview(id: string, stage: DetectionStage, review: StageReview): void {
    this.update(id, (item) => {
      this.ensureCurrent(item, stage);
      return this.withEvent(
        { ...item, reviews: { ...item.reviews, [stage]: { ...review } } },
        review.reviewer.trim() || item.owner,
        `Revisión guardada: ${stageLabel(stage)}.`,
      );
    });
  }

  addEvidence(
    id: string,
    input: Pick<Evidence, 'stage' | 'sensor' | 'reference' | 'summary'>,
  ): void {
    if (!input.reference.trim() || !input.summary.trim())
      throw new Error('Incluye la referencia y la descripción de la evidencia.');
    this.update(id, (item) => {
      this.ensureCurrent(item, input.stage);
      const evidence: Evidence = {
        ...input,
        reference: input.reference.trim(),
        summary: input.summary.trim(),
        id: crypto.randomUUID(),
        recordedAt: new Date().toISOString(),
      };
      return this.withEvent(
        { ...item, evidence: [...item.evidence, evidence] },
        item.owner,
        `Referencia de evidencia registrada: ${input.sensor}.`,
      );
    });
  }

  advance(id: string): void {
    this.update(id, (item) => {
      const issues = transitionIssues(item);
      if (issues.length) throw new Error(issues.join(' '));
      const reviewer = item.reviews[item.stage].reviewer;
      if (
        item.stage === 'reconnaissance' &&
        item.reviews.reconnaissance.finding === 'false-positive'
      ) {
        return this.withEvent(
          { ...item, status: 'discarded' },
          reviewer,
          'Cerrado como falso positivo según el dictamen registrado.',
        );
      }
      if (item.stage === 'mitigation')
        return this.withEvent(
          { ...item, status: 'closed' },
          reviewer,
          'Expediente cerrado con referencias de informe especializado y TECHINT.',
        );
      const next =
        DETECTION_LAYERS[DETECTION_LAYERS.findIndex((layer) => layer.id === item.stage) + 1].id;
      return this.withEvent(
        { ...item, stage: next },
        reviewer,
        `Etapa completada. Expediente trasladado a ${stageLabel(next)}.`,
      );
    });
  }

  private ensureCurrent(item: DetectionCase, stage: DetectionStage) {
    if (item.status !== 'open' || item.stage !== stage)
      throw new Error('Solo puedes modificar la etapa activa de un expediente abierto.');
  }

  private update(id: string, change: (item: DetectionCase) => DetectionCase): void {
    const item = this.records().find((entry) => entry.id === id);
    if (!item) throw new Error('El expediente no existe.');
    const updated = change(item);
    this.commit(this.records().map((entry) => (entry.id === id ? updated : entry)));
  }

  private withEvent(item: DetectionCase, actor: string, text: string): DetectionCase {
    const now = new Date().toISOString();
    return {
      ...item,
      updatedAt: now,
      history: [...item.history, { id: crypto.randomUUID(), at: now, actor, text }],
    };
  }

  private commit(items: DetectionCase[]): void {
    if (!this.storageReadable) throw new Error(this.storageError());
    try {
      // Avoid silently replacing a change made by another tab.
      const raw = localStorage.getItem(STORAGE_KEY);
      if (JSON.stringify(raw ? JSON.parse(raw).cases : []) !== JSON.stringify(this.records()))
        throw new Error('Los datos cambiaron en otra pestaña. Recarga la página antes de guardar.');
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ version: 1, cases: items }));
    } catch (error) {
      const message =
        error instanceof Error && error.message.includes('otra pestaña')
          ? error.message
          : 'No se pudo guardar. Verifica que el navegador permita almacenamiento local y tenga espacio disponible.';
      this.storageError.set(message);
      throw new Error(message);
    }
    this.records.set(items);
    this.storageError.set('');
  }
}

function isDetectionCase(value: unknown): value is DetectionCase {
  if (!value || typeof value !== 'object') return false;
  const item = value as DetectionCase;
  return (
    [
      'id',
      'code',
      'title',
      'state',
      'municipality',
      'owner',
      'description',
      'createdAt',
      'updatedAt',
    ].every((key) => typeof (value as Record<string, unknown>)[key] === 'string') &&
    typeof item.demo === 'boolean' &&
    Number.isFinite(Date.parse(item.createdAt)) &&
    Number.isFinite(Date.parse(item.updatedAt)) &&
    Number.isFinite(item.latitude) &&
    Math.abs(item.latitude) <= 90 &&
    Number.isFinite(item.longitude) &&
    Math.abs(item.longitude) <= 180 &&
    ['open', 'closed', 'discarded'].includes(item.status) &&
    ['high', 'medium', 'low'].includes(item.priority) &&
    ['vertisol', 'andosol', 'leptosol', 'unknown'].includes(item.soil) &&
    ['dry', 'rainy', 'unknown'].includes(item.season) &&
    DETECTION_LAYERS.some((layer) => layer.id === item.stage) &&
    DETECTION_LAYERS.every(
      (layer) =>
        item.reviews?.[layer.id] &&
        ['pending', 'confirmed', 'inconclusive', 'false-positive'].includes(
          item.reviews[layer.id].finding,
        ) &&
        ['pending', 'blocked', 'reviewed'].includes(item.reviews[layer.id].environment) &&
        Object.keys(emptyReview()).every(
          (key) =>
            typeof (item.reviews[layer.id] as unknown as Record<string, unknown>)[key] === 'string',
        ),
    ) &&
    Array.isArray(item.evidence) &&
    item.evidence.every(
      (entry) =>
        entry &&
        typeof entry.id === 'string' &&
        typeof entry.recordedAt === 'string' &&
        Number.isFinite(Date.parse(entry.recordedAt)) &&
        DETECTION_LAYERS.some((layer) => layer.id === entry.stage) &&
        typeof entry.reference === 'string' &&
        typeof entry.summary === 'string' &&
        typeof entry.sensor === 'string',
    ) &&
    Array.isArray(item.history) &&
    item.history.every(
      (entry) =>
        entry &&
        typeof entry.id === 'string' &&
        typeof entry.at === 'string' &&
        Number.isFinite(Date.parse(entry.at)) &&
        typeof entry.actor === 'string' &&
        typeof entry.text === 'string',
    )
  );
}
