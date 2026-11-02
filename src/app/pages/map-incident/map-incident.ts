import { DatePipe } from '@angular/common';
import { Component, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Note } from '../../core/models/note.model';
import { incidentFields } from './incident-fields';
import { MapService } from '../../core/services/map.service';

@Component({
  selector: 'app-map-incident', imports: [DatePipe, RouterLink],
  template: `
    <a routerLink="/mapa">← Volver al mapa</a>
    <h2>Ficha de incidencia</h2>
    <p>Consulta compartida de solo lectura.</p>
    @if (loading()) { <p>Cargando incidencia…</p> }
    @if (error()) { <p role="alert">{{ error() }}</p> }
    @if (note(); as item) {
      <section aria-label="Datos de la incidencia">
        <h3>{{ item.title }}</h3>
        <dl><dt>Área</dt><dd>{{ item.state }}</dd><dt>Descripción</dt><dd>{{ item.content }}</dd>
          <dt>Latitud</dt><dd>{{ item.altitude }}</dd><dt>Longitud</dt><dd>{{ item.longitude }}</dd>
          <dt>Registrada</dt><dd>{{ item.createdAt | date:'medium' }}</dd><dt>Actualizada</dt><dd>{{ item.updatedAt | date:'medium' }}</dd></dl>
        @if (item.workflow; as workflow) {
          <h3>Seguimiento por fases</h3>
          <p>Estado: {{ workflow.status }} · Fase actual: {{ workflow.currentStage || 'Finalizada' }}</p>
          @for (phase of workflow.phases; track phase.stage) {
            <details><summary>{{ phaseName(phase.stage) }} · {{ phase.validatedAt ? 'Validada' : 'Sin validar' }}</summary><dl>@for (field of fields(phase); track $index) { <dt>{{ field.label }}</dt><dd>{{ field.value }}</dd> }</dl></details>
          }
          <details><summary>Historial</summary><dl>@for (field of fields(workflow.history); track $index) { <dt>{{ field.label }}</dt><dd>{{ field.value }}</dd> }</dl></details>
        }
      </section>
    }
  `,
  styles: `:host{display:block;max-width:960px;margin:auto;color:var(--text)} a{color:var(--accent)} section{padding:24px;border:1px solid var(--line);border-radius:12px;background:var(--surface)} dt{color:var(--text-muted);font-weight:700} dd{margin:4px 0 18px;white-space:pre-wrap;overflow-wrap:anywhere} details{margin:12px 0} summary{cursor:pointer} pre{white-space:pre-wrap;overflow-wrap:anywhere}`,
})
export class MapIncidentPage implements OnInit {
  readonly fields = incidentFields;
  phaseName(stage: string): string { return ({ indications: 'Indicios tempranos', reconnaissance: 'Reconocimiento', surveillance: 'Vigilancia', mitigation: 'Mitigación y cierre' } as Record<string, string>)[stage] ?? stage; }
  readonly note = signal<Note | null>(null); readonly loading = signal(true); readonly error = signal('');
  constructor(private route: ActivatedRoute, private service: MapService) {}
  async ngOnInit() {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) { this.error.set('No se indicó una incidencia.'); this.loading.set(false); return; }
    try { this.note.set(await firstValueFrom(this.service.detail(id))); }
    catch { this.error.set('No fue posible consultar la incidencia solicitada.'); }
    finally { this.loading.set(false); }
  }
}
