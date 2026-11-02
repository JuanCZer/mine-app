import { Component, input } from '@angular/core';
import { Season, Soil } from '../../core/models/detection.model';

@Component({
  selector: 'app-terrain-context',
  template: `
    <section aria-label="Contexto del terreno">
      <div class="caption">CONTEXTO DEL TERRENO <span>PDF · pp. 32–35</span></div>
      <h3>{{ soilLabel() || soil() }}</h3>
      <span class="season">Temporada: {{ seasonLabel() || season() }}</span>
      @switch (soil()) {
        @case ('vertisol') {
          <p>
            El documento describe cambios estacionales y pérdida de firmas de remoción. Registra la
            fecha y las condiciones de cada levantamiento.
          </p>
          @if (season() === 'rainy') {
            <div class="notice">
              Revisión adicional: el PDF señala limitaciones del GPR en condiciones de lluvia.
            </div>
          }
        }
        @case ('andosol') {
          <p>
            El sustrato basáltico y la actividad agrícola pueden interferir en la interpretación.
            Documenta labores agrícolas y correlaciona las evidencias.
          </p>
          <div class="notice">
            Revisa posibles falsos positivos por cambios agrícolas o respuesta magnética del suelo.
          </div>
        }
        @case ('leptosol') {
          <p>
            El PDF describe suelo somero, cobertura vegetal densa e interferencia en la interfaz
            suelo–roca. Registra las limitaciones del levantamiento.
          </p>
          <div class="notice">
            La disponibilidad de evidencia depende de la cobertura y las condiciones del terreno.
          </div>
        }
        @default {
          <p>
            Registra el tipo de suelo para contextualizar las evidencias y las posibles causas
            naturales de una anomalía.
          </p>
        }
      }
      <small
        >Contexto tomado del documento; no constituye un dictamen del sitio ni una selección
        automática de equipo.</small
      >
    </section>
  `,
  styles: `
    section {
      padding: 22px;
      border: 1px solid #dde3d8;
      border-radius: 12px;
      background: #f1f4ec;
      color: #394c3f;
    }
    .caption {
      font-size: 9px;
      letter-spacing: 0.12em;
      font-weight: 700;
      display: flex;
      justify-content: space-between;
      gap: 10px;
    }
    .caption span {
      font-size: 9px;
      letter-spacing: 0;
      color: #78836f;
      font-weight: 400;
    }
    h3 {
      font: 600 22px Georgia;
      margin: 20px 0 7px;
    }
    .season {
      font-size: 11px;
    }
    p {
      font-size: 12px;
      line-height: 1.8;
      margin: 15px 0;
    }
    .notice {
      background: #e5eadb;
      border-left: 2px solid #869b5b;
      padding: 12px;
      font-size: 11px;
      line-height: 1.6;
      margin-bottom: 15px;
    }
    small {
      font-size: 10px;
      line-height: 1.6;
      display: block;
      color: #73806f;
    }
    section { border-color: var(--line); background: linear-gradient(145deg, #10243a, #0c1b2c); color: var(--text); }
    .caption { color: var(--accent-hover); }.caption span, small { color: var(--text-muted); } h3 { color: var(--text); font-family: inherit; font-size: 21px; font-weight: 670; }.season, p { color: var(--text-secondary); font-size: 13px; }.notice { border-left-color: var(--warning); background: var(--warning-soft); color: #ffd28e; font-size: 12px; }
  `,
})
export class TerrainContext {
  readonly soil = input<Soil>('unknown');
  readonly season = input<Season>('unknown');
  readonly soilLabel = input('');
  readonly seasonLabel = input('');
}
