import { Component, input, output } from '@angular/core';
import { CaseStatus, DetectionStage, DETECTION_LAYERS } from '../../core/models/detection.model';

@Component({
  selector: 'app-layer-pipeline',
  template: `
    <nav aria-label="Etapas del expediente">
      @for (layer of layers; track layer.id; let index = $index) {
        <button
          type="button"
          [class.current]="selected() === layer.id"
          [class.complete]="isComplete(index)"
          [attr.aria-current]="selected() === layer.id ? 'step' : null"
          (click)="selectedChange.emit(layer.id)"
        >
          <span class="number">{{ isComplete(index) ? '✓' : layer.number }}</span>
          <span
            ><small>CAPA {{ layer.number }}</small
            ><strong>{{ layer.title }}</strong>
            <em>{{
              isComplete(index)
                ? 'Revisada'
                : current() === layer.id && status() === 'open'
                  ? 'Etapa activa'
                  : 'Consultar etapa'
            }}</em></span
          >
        </button>
      }
    </nav>
  `,
  styles: `
    :host {
      display: block;
    }
    nav {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 10px;
    }
    button {
      text-align: left;
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 17px 13px;
      border: 1px solid #dde3da;
      border-radius: 12px;
      background: #fff;
      color: #506053;
      cursor: pointer;
    }
    button.current {
      background: #edf3e4;
      border-color: #71884b;
      box-shadow: inset 0 0 0 1px #71884b;
    }
    .number {
      font-size: 15px;
      font-weight: 600;
      color: #7b886e;
      width: 31px;
      height: 34px;
      display: grid;
      place-items: center;
      background: #f3f5ef;
      border-radius: 8px;
      flex: none;
    }
    .complete .number {
      background: #deebd2;
      color: #3d662b;
    }
    small,
    strong,
    em {
      display: block;
    }
    small {
      font-size: 8px;
      letter-spacing: 0.14em;
    }
    strong {
      color: #23362b;
      font-size: 12px;
      margin: 4px 0;
    }
    em {
      font-size: 10px;
      font-style: normal;
      color: #7a867d;
    }
    @media (max-width: 1100px) {
      nav {
        grid-template-columns: repeat(2, 1fr);
      }
    }
    @media (max-width: 430px) {
      button {
        padding: 12px 8px;
        gap: 7px;
      }
      strong {
        font-size: 11px;
      }
    }
    button { border-color: var(--line); background: var(--surface); color: var(--text-secondary); }
    button:hover { border-color: var(--line-strong); background: var(--surface-hover); }
    button.current { border-color: var(--accent); background: rgb(19 65 91 / .88); box-shadow: inset 0 0 0 1px rgb(91 184 232 / .32); }
    .number { background: var(--surface-input); color: var(--text-secondary); }
    .complete .number { background: var(--success-soft); color: #a8e8c3; }
    small { color: var(--text-muted); } strong { color: var(--text); } em { color: var(--text-secondary); }
  `,
})
export class LayerPipeline {
  readonly layers = DETECTION_LAYERS;
  readonly current = input<DetectionStage>('indications');
  readonly selected = input<DetectionStage>('indications');
  readonly status = input<CaseStatus>('open');
  readonly selectedChange = output<DetectionStage>();
  isComplete(index: number) {
    const currentIndex = this.layers.findIndex((layer) => layer.id === this.current());
    return index < currentIndex || (index === currentIndex && this.status() !== 'open');
  }
}
