import { Injectable, signal } from '@angular/core';
import {
  CaseInput,
  DetectionCase,
  DetectionStage,
  Evidence,
  StageReview,
} from '../models/detection.model';

const RETIRED_MESSAGE =
  'El panel local fue retirado porque no podía aislar expedientes entre cuentas. ' +
  'Los datos existentes permanecen intactos para su exportación y conciliación controladas.';

/**
 * Compatibility stub for components that are no longer routed.
 *
 * Deliberately never reads or writes gds-detection-cases-v1. The legacy blob has no
 * trustworthy authenticated owner, so exposing or claiming it after a new login
 * would cross the account boundary. Operations must export and reconcile it offline.
 */
@Injectable({ providedIn: 'root' })
export class DetectionService {
  private readonly records = signal<DetectionCase[]>([]);
  readonly cases = this.records.asReadonly();
  readonly storageError = signal(RETIRED_MESSAGE);

  create(_input: CaseInput, _demo = false): DetectionCase {
    return this.retired();
  }

  saveReview(_id: string, _stage: DetectionStage, _review: StageReview): void {
    this.retired();
  }

  addEvidence(
    _id: string,
    _input: Pick<Evidence, 'stage' | 'sensor' | 'reference' | 'summary'>,
  ): void {
    this.retired();
  }

  advance(_id: string): void {
    this.retired();
  }

  private retired(): never {
    throw new Error(RETIRED_MESSAGE);
  }
}
