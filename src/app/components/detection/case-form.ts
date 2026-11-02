import { Component, OnInit, inject, output, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CatalogItem, CatalogKey } from '../../core/models/catalog.model';
import { CaseInput, Priority, Season, Soil } from '../../core/models/detection.model';
import { CatalogsService } from '../../core/services/catalogs.service';

const requiredText = [Validators.required, Validators.pattern(/\S/)];

@Component({
  selector: 'app-case-form',
  imports: [ReactiveFormsModule],
  template: `
    <form [formGroup]="form" (ngSubmit)="submit()">
      <div class="intro">
        <span>REGISTRO DE ANOMALÍA</span>
        <h2>Nuevo expediente</h2>
        <p>El seguimiento comienza en la capa cero: inteligencia e indicios tempranos.</p>
      </div>
      <label
        >Nombre del caso<input
          formControlName="title"
          maxlength="120"
          placeholder="Nombre breve para identificar el registro"
      /></label>
      <div class="row">
        <label
          >Estado<select formControlName="state" [disabled]="catalogLoading()">
            <option value="">Selecciona un estado</option>
            @for (state of options('mexicoStates', form.controls.state.value); track state.code) {
              <option [value]="state.code" [disabled]="!state.active">{{ state.label }}{{ state.active ? '' : ' (inactivo)' }}</option>
            }
          </select></label
        >
        <label
          >Municipio / zona<input
            formControlName="municipality"
            maxlength="120"
            placeholder="Ubicación del registro"
        /></label>
      </div>
      <div class="row">
        <label
          >Latitud<input
            type="number"
            formControlName="latitude"
            min="-90"
            max="90"
            step="any"
            placeholder="−90 a 90"
        /></label>
        <label
          >Longitud<input
            type="number"
            formControlName="longitude"
            min="-180"
            max="180"
            step="any"
            placeholder="−180 a 180"
        /></label>
      </div>
      <div class="row">
        <label
          >Tipo de suelo<select formControlName="soil">
            <option value="">Selecciona un tipo</option>
            @for (option of options('soils', form.controls.soil.value); track option.code) {
              <option [value]="option.code" [disabled]="!option.active">{{ option.label }}{{ option.active ? '' : ' (inactivo)' }}</option>
            }
          </select></label
        >
        <label
          >Temporada<select formControlName="season">
            <option value="">Selecciona una temporada</option>
            @for (option of options('seasons', form.controls.season.value); track option.code) {
              <option [value]="option.code" [disabled]="!option.active">{{ option.label }}{{ option.active ? '' : ' (inactivo)' }}</option>
            }
          </select></label
        >
      </div>
      <div class="row">
        <label
          >Prioridad declarada<select formControlName="priority">
            <option value="">Selecciona una prioridad</option>
            @for (option of options('riskLevels', form.controls.priority.value); track option.code) {
              <option [value]="option.code" [disabled]="!option.active">{{ option.label }}{{ option.active ? '' : ' (inactivo)' }}</option>
            }
          </select></label
        >
        <label
          >Responsable<input
            formControlName="owner"
            maxlength="100"
            placeholder="Nombre o identificador del responsable"
        /></label>
      </div>
      <label
        >Observación inicial<textarea
          formControlName="description"
          rows="3"
          maxlength="3000"
          placeholder="Describe lo observado, su origen y el contexto del hallazgo"
        ></textarea>
      </label>
      @if (catalogLoading()) {
        <p class="catalog-state" role="status">Cargando catálogos…</p>
      } @else if (catalogError()) {
        <p class="error" role="alert">{{ catalogError() }} <button type="button" class="retry" (click)="loadCatalogs(true)">Reintentar</button></p>
      } @else if (!catalogReady()) {
        <p class="error" role="alert">Los catálogos requeridos no tienen opciones activas.</p>
      }
      @if (attempted && form.invalid) {
        <p class="error" role="alert">
          Completa los campos obligatorios y usa coordenadas dentro del rango indicado.
        </p>
      }
      <div class="actions">
        <button class="secondary" type="button" (click)="cancelled.emit()">Cancelar</button
        ><button class="primary" type="submit" [disabled]="!catalogReady()">Crear expediente <span>→</span></button>
      </div>
    </form>
  `,
  styles: `
    form {
      padding: 28px;
    }
    .intro span {
      font-size: 9px;
      letter-spacing: 0.17em;
      color: #83906c;
      font-weight: 600;
    }
    h2 {
      font: 500 29px Georgia;
      color: #273d2b;
      margin: 10px 0;
    }
    .intro p {
      font-size: 12px;
      color: #7a8677;
      line-height: 1.6;
      margin-bottom: 23px;
    }
    label {
      display: grid;
      gap: 7px;
      font-size: 11px;
      color: #55664f;
      margin-bottom: 15px;
    }
    .row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 15px;
    }
    input,
    select,
    textarea {
      width: 100%;
      min-width: 0;
      padding: 11px 12px;
      border: 1px solid #dce2d6;
      border-radius: 7px;
      font: inherit;
      color: #31482f;
      background: #fff;
    }
    textarea {
      resize: vertical;
    }
    .actions {
      display: flex;
      justify-content: flex-end;
      gap: 10px;
      padding-top: 15px;
      border-top: 1px solid #e6eadf;
      margin-top: 10px;
    }
    button {
      padding: 12px 18px;
      border-radius: 7px;
      font-size: 12px;
      cursor: pointer;
    }
    .primary {
      background: #2c4734;
      color: #fff;
      border: 1px solid #2c4734;
    }
    .secondary {
      color: #56644e;
      background: #fff;
      border: 1px solid #dae0d3;
    }
    .error, .catalog-state {
      color: #a04130;
      font-size: 12px;
    }
    .retry { padding: 3px 8px; margin-left: 8px; }
    @media (max-width: 480px) {
      form {
        padding: 20px;
      }
      .row {
        grid-template-columns: 1fr;
        gap: 0;
      }
    }
    form { background: var(--surface); color: var(--text); }
    .intro span { color: var(--accent-hover); font-size: 10px; font-weight: 700; }
    h2 { color: var(--text); font-family: inherit; font-size: 28px; font-weight: 670; }
    .intro p { color: var(--text-secondary); font-size: 13px; }
    label { color: var(--text-secondary); font-size: 12px; font-weight: 600; }
    input, select, textarea { border-color: var(--line); background: var(--surface-input); color: var(--text); font-size: 14px; }
    input:focus, select:focus, textarea:focus { border-color: var(--accent); box-shadow: 0 0 0 3px rgb(91 184 232 / .15); outline: 0; }
    .actions { border-color: var(--line); }
    .primary { border-color: rgb(121 211 245 / .66); background: linear-gradient(135deg, #76cbef, #469dc8); color: var(--accent-ink); font-weight: 700; }
    .secondary { border-color: var(--line-strong); background: var(--surface-input); color: var(--accent-hover); }
    .error { color: #ffc0c0; }
  `,
})
export class CaseForm implements OnInit {
  private readonly catalogsService = inject(CatalogsService);
  private readonly keys: readonly CatalogKey[] = ['mexicoStates', 'soils', 'seasons', 'riskLevels'];
  readonly catalogs = signal<Partial<Record<CatalogKey, CatalogItem[]>>>({});
  readonly catalogLoading = signal(true);
  readonly catalogError = signal('');
  readonly catalogReady = signal(false);
  readonly saved = output<CaseInput>();
  readonly cancelled = output<void>();
  attempted = false;
  readonly form = new FormGroup({
    title: new FormControl('', {
      nonNullable: true,
      validators: [...requiredText, Validators.maxLength(120)],
    }),
    state: new FormControl('', { nonNullable: true, validators: requiredText }),
    municipality: new FormControl('', {
      nonNullable: true,
      validators: [...requiredText, Validators.maxLength(120)],
    }),
    latitude: new FormControl<number | null>(null, [
      Validators.required,
      Validators.min(-90),
      Validators.max(90),
    ]),
    longitude: new FormControl<number | null>(null, [
      Validators.required,
      Validators.min(-180),
      Validators.max(180),
    ]),
    soil: new FormControl<Soil>('', { nonNullable: true, validators: Validators.required }),
    season: new FormControl<Season>('', { nonNullable: true, validators: Validators.required }),
    priority: new FormControl<Priority>('', { nonNullable: true, validators: Validators.required }),
    owner: new FormControl('', {
      nonNullable: true,
      validators: [...requiredText, Validators.maxLength(100)],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [...requiredText, Validators.maxLength(3000)],
    }),
  });
  ngOnInit() {
    void this.loadCatalogs();
  }
  async loadCatalogs(reload = false) {
    this.catalogLoading.set(true);
    this.catalogError.set('');
    try {
      const response = await firstValueFrom(
        reload
          ? this.catalogsService.reload(this.keys, { stage: 'indications', includeInactive: true })
          : this.catalogsService.load(this.keys, { stage: 'indications', includeInactive: true }),
      );
      this.catalogs.set(response.catalogs);
      this.catalogReady.set(this.keys.every((key) => (response.catalogs[key] ?? []).some((item) => item.active)));
    } catch {
      this.catalogReady.set(false);
      this.catalogError.set('No fue posible cargar los catálogos requeridos.');
    } finally {
      this.catalogLoading.set(false);
    }
  }
  options(key: CatalogKey, selected: string): CatalogItem[] {
    return this.catalogsService.selectable(this.catalogs()[key] ?? [], selected);
  }
  submit() {
    this.attempted = true;
    if (this.form.invalid || !this.catalogReady()) {
      this.form.markAllAsTouched();
      return;
    }
    const value = this.form.getRawValue();
    this.saved.emit({ ...value, latitude: value.latitude!, longitude: value.longitude! });
  }
}
