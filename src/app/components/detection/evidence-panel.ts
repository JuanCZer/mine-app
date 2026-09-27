import { DatePipe } from '@angular/common';
import { Component, effect, input, output } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Evidence, Sensor, SENSORS } from '../../core/models/detection.model';

@Component({
  selector: 'app-evidence-panel',
  imports: [DatePipe, ReactiveFormsModule],
  template: `
    <section class="panel">
      <div class="heading">
        <h3>Evidencias de la fase</h3>
        <span>{{ evidence().length }} referencias</span>
      </div>
      <p class="hint">
        Registra el folio o la ubicación del archivo original. Los archivos de sensores se conservan
        en su sistema de origen.
      </p>
      @for (entry of evidence(); track entry.id) {
        <article>
          <span class="sensor">{{ entry.sensor }}</span>
          <div>
            <strong>{{ entry.reference }}</strong>
            <p>{{ entry.summary }}</p>
            @if (entry.recordedAt) {
              <time>{{ entry.recordedAt | date: 'dd/MM/yyyy HH:mm' }}</time>
            }
          </div>
          @if (editable() && allowRemove()) {
            <button
              class="remove"
              type="button"
              [attr.aria-label]="'Quitar evidencia ' + entry.reference"
              (click)="removed.emit(entry.id)"
            >
              ×
            </button>
          }
        </article>
      } @empty {
        <div class="empty">Aún no hay evidencias registradas para esta fase.</div>
      }
      @if (editable()) {
        <form [formGroup]="form" (ngSubmit)="submit()">
          <div class="fields">
            <label
              >Fuente<select formControlName="sensor">
                @for (sensor of allowedSensors(); track sensor) {
                  <option [value]="sensor">{{ sensor }}</option>
                }
              </select></label
            >
            <label
              >Referencia o folio<input
                formControlName="reference"
                maxlength="300"
                placeholder="Ej. REP-2026-014 / repositorio"
            /></label>
          </div>
          <label
            >Descripción<textarea
              formControlName="summary"
              rows="2"
              maxlength="2000"
              placeholder="Qué aporta esta evidencia y cuáles son sus limitaciones"
            ></textarea>
          </label>
          <button type="submit" [disabled]="form.invalid">
            {{ draftMode() ? '＋ Agregar evidencia al borrador' : '＋ Registrar evidencia' }}
          </button>
        </form>
      }
    </section>
  `,
  styles: `
    .panel {
      background: #fff;
      border: 1px solid #e0e5dd;
      padding: 24px;
      border-radius: 12px;
    }
    .heading {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    h3 {
      margin: 0;
      color: #293b2e;
      font-size: 14px;
    }
    .heading span,
    .hint {
      font-size: 11px;
      color: #7d897e;
    }
    .hint {
      line-height: 1.7;
    }
    article {
      display: flex;
      gap: 12px;
      padding: 15px 0;
      border-bottom: 1px solid #edf0e9;
    }
    article div {
      min-width: 0;
      flex: 1;
    }
    .remove {
      align-self: start;
      padding: 3px 8px;
    }
    strong {
      font-size: 11px;
      overflow-wrap: anywhere;
    }
    article p {
      font-size: 11px;
      line-height: 1.6;
      color: #667568;
      white-space: pre-wrap;
    }
    time {
      font-size: 9px;
      color: #8e988d;
    }
    .sensor {
      height: fit-content;
      border-radius: 5px;
      background: #edf1e7;
      color: #657946;
      padding: 5px 7px;
      font-size: 9px;
    }
    .empty {
      padding: 20px 0;
      font-size: 12px;
      color: #8a948a;
    }
    form {
      margin-top: 18px;
    }
    label {
      display: grid;
      gap: 6px;
      font-size: 11px;
      color: #5c6b5e;
      margin-bottom: 12px;
    }
    .fields {
      display: grid;
      grid-template-columns: 1fr 2fr;
      gap: 10px;
    }
    input,
    select,
    textarea {
      width: 100%;
      padding: 10px;
      border: 1px solid #dce2d7;
      border-radius: 6px;
      font: inherit;
      background: #fff;
      color: #263c2d;
    }
    textarea {
      resize: vertical;
    }
    button {
      padding: 10px 14px;
      border: 1px solid #ced8c3;
      border-radius: 7px;
      background: #f0f4e9;
      color: #4f6836;
      cursor: pointer;
      font-size: 11px;
    }
    button:disabled {
      opacity: 0.5;
      cursor: default;
    }
    @media (max-width: 480px) {
      .fields {
        grid-template-columns: 1fr;
      }
      .panel {
        padding: 18px;
      }
    }
    .panel { border-color: var(--line); background: var(--surface); color: var(--text); }
    h3 { color: var(--text); font-size: 16px; }
    .heading span, .hint { color: var(--text-secondary); font-size: 12px; }
    article { border-color: var(--line); }
    strong { color: var(--text); font-size: 12px; }
    article p { color: var(--text-secondary); font-size: 12px; }
    time { color: var(--text-muted); font-size: 10px; }
    .sensor { border: 1px solid var(--line-strong); background: var(--surface-input); color: var(--accent-hover); }
    .empty { color: var(--text-secondary); }
    label { color: var(--text-secondary); font-size: 12px; font-weight: 600; }
    input, select, textarea { border-color: var(--line); background: var(--surface-input); color: var(--text); font-size: 14px; }
    input:focus, select:focus, textarea:focus { border-color: var(--accent); box-shadow: 0 0 0 3px rgb(91 184 232 / .15); outline: 0; }
    button { border-color: rgb(121 211 245 / .5); background: var(--surface-hover); color: var(--accent-hover); font-size: 12px; }
    button:hover:not(:disabled) { border-color: var(--accent); color: #fff; }
  `,
})
export class EvidencePanel {
  readonly evidence = input<
    (Pick<Evidence, 'id' | 'sensor' | 'reference' | 'summary'> & { recordedAt?: string })[]
  >([]);
  readonly editable = input(false);
  readonly allowRemove = input(false);
  readonly draftMode = input(false);
  readonly removed = output<string>();
  readonly draftChange = output<boolean>();
  readonly added = output<Pick<Evidence, 'sensor' | 'reference' | 'summary'>>();
  readonly allowedSensors = input<readonly Sensor[]>(SENSORS);
  readonly form = new FormGroup({
    sensor: new FormControl<Sensor>('LiDAR', { nonNullable: true }),
    reference: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/\S/), Validators.maxLength(300)],
    }),
    summary: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/\S/), Validators.maxLength(2000)],
    }),
  });
  constructor() {
    this.form.valueChanges
      .pipe(takeUntilDestroyed())
      .subscribe(() =>
        this.draftChange.emit(
          !!(this.form.controls.reference.value || this.form.controls.summary.value),
        ),
      );
    effect(() => {
      const sensors = this.allowedSensors();
      if (sensors.length && !sensors.includes(this.form.controls.sensor.value))
        this.form.controls.sensor.setValue(sensors[0], { emitEvent: false });
    });
  }
  submit() {
    if (this.form.valid) this.added.emit(this.form.getRawValue());
  }
  reset() {
    this.form.reset({ sensor: this.allowedSensors()[0] ?? 'LiDAR', reference: '', summary: '' });
  }
}
