import { Component, OnInit, signal, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotePhase } from '../../core/models/note-workflow.model';
import { CaseEvent } from '../../core/models/detection.model';
import { Note } from '../../core/models/note.model';
import { incidentFields } from './incident-fields';
import { MapService } from '../../core/services/map.service';

@Component({
  selector: 'app-map-incident',
  imports: [RouterLink],
  template: `
    <a routerLink="/mapa">{{ dictionary.map.backToMap }}</a>
    <h2>{{ dictionary.map.detailsHeading }}</h2>
    <p>{{ dictionary.map.readOnly }}</p>
    @if (loading()) {
      <p>{{ dictionary.map.loadingIncident }}</p>
    }
    @if (error()) {
      <p role="alert">{{ error() }}</p>
    }
    @if (note(); as item) {
      <section [attr.aria-label]="dictionary.map.incidentData">
        <h3>{{ item.title }}</h3>
        <dl>
          <dt>{{ dictionary.map.area }}</dt>
          <dd>{{ item.state }}</dd>
          <dt>{{ dictionary.map.description }}</dt>
          <dd>{{ item.content }}</dd>
          <dt>{{ dictionary.map.latitude }}</dt>
          <dd>{{ coordinate(item.altitude) }}</dd>
          <dt>{{ dictionary.map.longitude }}</dt>
          <dd>{{ coordinate(item.longitude) }}</dd>
          <dt>{{ dictionary.map.created }}</dt>
          <dd>{{ i18n.date(item.createdAt) }}</dd>
          <dt>{{ dictionary.map.updated }}</dt>
          <dd>{{ i18n.date(item.updatedAt) }}</dd>
        </dl>
        @if (item.workflow; as workflow) {
          <h3>{{ dictionary.map.phaseTracking }}</h3>
          <p>
            {{ dictionary.map.status }}:
            {{ i18n.catalogLabel('workflowStatuses', workflow.status) }} ·
            {{ dictionary.map.currentPhase }}:
            {{ workflow.currentStage ? phaseName(workflow.currentStage) : dictionary.map.finished }}
          </p>
          @for (phase of workflow.phases; track phase.stage) {
            <details>
              <summary>
                {{ phaseName(phase.stage) }} ·
                {{ phase.validatedAt ? dictionary.map.validated : dictionary.map.notValidated }}
              </summary>
              <dl>
                @for (field of fields(phase); track $index) {
                  <dt>{{ field.label }}</dt>
                  <dd>{{ field.value }}</dd>
                }
              </dl>
            </details>
          }
          <details>
            <summary>{{ dictionary.map.history }}</summary>
            <dl>
              @for (field of historyFields(workflow.history); track $index) {
                <dt>{{ field.label }}</dt>
                <dd>{{ field.value }}</dd>
              }
            </dl>
          </details>
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: block;
      max-width: 960px;
      margin: auto;
      color: var(--text);
    }
    a {
      color: var(--accent);
    }
    section {
      padding: 24px;
      border: 1px solid var(--line);
      border-radius: 12px;
      background: var(--surface);
    }
    dt {
      color: var(--text-muted);
      font-weight: 700;
    }
    dd {
      margin: 4px 0 18px;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    details {
      margin: 12px 0;
    }
    summary {
      cursor: pointer;
    }
    pre {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
  `,
})
export class MapIncidentPage implements OnInit {
  readonly i18n = inject(I18nService);
  get dictionary() {
    return this.i18n.dictionary();
  }
  fields(value: unknown) {
    const snapshots = (value as Partial<NotePhase> | null)?.catalogSnapshot?.selections ?? [];
    return incidentFields(value, this.dictionary.map, (item, key, path) => {
      if (path.includes('catalogSnapshot')) return undefined;
      if (typeof item === 'number') return this.i18n.number(item, { maximumFractionDigits: 8 });
      if (typeof item !== 'string' || !item) return undefined;
      if (key === 'stage') return this.phaseName(item);
      if (
        [
          'validatedAt',
          'recordedAt',
          'observedAt',
          'observationStartedAt',
          'observationEndedAt',
          'at',
        ].includes(key)
      )
        return this.i18n.date(item);
      const field = (path[0] === 'data' ? path.slice(1) : path).join('.');
      const historical = snapshots.find(
        (selection) => selection.field === field && selection.code === item,
      );
      if (historical) return historical.label;
      const catalog = FIELD_CATALOGS[key];
      return catalog ? this.i18n.catalogLabel(catalog, item) : undefined;
    });
  }
  historyFields(events: CaseEvent[]) {
    return this.fields(
      events.map(({ code, parameters, ...event }) => ({
        ...event,
        text:
          code && this.i18n.has(`api.events.${code}`)
            ? this.i18n.text(`api.events.${code}`, parameters)
            : event.text,
      })),
    );
  }
  phaseName(stage: string): string {
    return this.i18n.catalogLabel('workflowStages', stage);
  }
  coordinate(value: number | null) {
    return value === null ? '—' : this.i18n.number(value, { maximumFractionDigits: 8 });
  }
  readonly note = signal<Note | null>(null);
  readonly loading = signal(true);
  readonly error = this.i18n.message();
  constructor(
    private route: ActivatedRoute,
    private service: MapService,
  ) {}
  async ngOnInit() {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.error.set({ key: 'map.noIncidentId' });
      this.loading.set(false);
      return;
    }
    try {
      this.note.set(await firstValueFrom(this.service.detail(id)));
    } catch (error) {
      this.error.set(this.i18n.errorRef(error, 'map.incidentFailed'));
    } finally {
      this.loading.set(false);
    }
  }
}

const FIELD_CATALOGS: Record<string, string> = {
  soil: 'soils',
  season: 'seasons',
  finding: 'findings',
  environment: 'environmentReviews',
  platform: 'platforms',
  collectionSource: 'indicationSources',
  signatures: 'indicationSignatures',
  probability: 'riskLevels',
  priority: 'riskLevels',
  radioTechnologies: 'radioTechnologies',
  methods: 'surveillanceMethods',
  deployment: 'surveillanceDeployments',
  humanActivity: 'humanActivities',
  environmentDecision: 'environmentDecisions',
  protectionLevel: 'protectionLevels',
  neutralizationResult: 'neutralizationResults',
  routeResult: 'routeResults',
  sensor: 'sensors',
};
