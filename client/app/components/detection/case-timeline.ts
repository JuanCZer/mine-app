import { I18nService } from '../../core/i18n/i18n.service';
import { Component, computed, inject, input } from '@angular/core';
import { CaseEvent } from '../../core/models/detection.model';

@Component({
  selector: 'app-case-timeline',
  imports: [],
  template: `
    <section>
      <h3>{{ dictionary.workflow.timeline.heading }}</h3>
      <p>{{ subtitle() || dictionary.workflow.timeline.subtitle }}</p>
      <ol>
        @for (event of recent(); track event.id) {
          <li>
            <time>{{ i18n.date(event.at) }}</time
            ><strong>{{ eventText(event) }}</strong
            ><span>{{ event.actor }}</span>
          </li>
        }
      </ol>
    </section>
  `,
  styles: `
    section {
      padding: 22px;
      background: #fff;
      border: 1px solid #e0e5dd;
      border-radius: 12px;
    }
    h3 {
      margin: 0;
      font-size: 14px;
      color: #2c3c30;
    }
    p {
      color: #899183;
      font-size: 10px;
      margin-bottom: 20px;
    }
    ol {
      list-style: none;
      padding: 0 0 0 8px;
      margin: 0;
      max-height: 480px;
      overflow: auto;
    }
    li {
      border-left: 1px solid #dbe1d4;
      position: relative;
      padding: 0 0 22px 19px;
    }
    li:before {
      content: '';
      position: absolute;
      top: 3px;
      left: -4px;
      height: 7px;
      width: 7px;
      border-radius: 50%;
      background: #8b9e67;
    }
    li:last-child {
      padding-bottom: 0;
    }
    time,
    strong,
    span {
      display: block;
    }
    time {
      font-size: 9px;
      color: #8c9587;
    }
    strong {
      font-size: 11px;
      font-weight: 500;
      line-height: 1.6;
      color: #475746;
      margin: 6px 0;
    }
    span {
      font-size: 10px;
      color: #7d8776;
    }
    section {
      border-color: var(--line);
      background: var(--surface);
    }
    .section,
    h3 {
      color: var(--text);
    }
    p,
    time,
    span {
      color: var(--text-muted);
    }
    strong {
      color: var(--text-secondary);
      font-size: 12px;
    }
    li {
      border-left-color: var(--line-strong);
    }
    li:before {
      background: var(--accent);
    }
  `,
})
export class CaseTimeline {
  readonly i18n = inject(I18nService);
  get dictionary() {
    return this.i18n.dictionary();
  }
  readonly subtitle = input('');
  readonly events = input<CaseEvent[]>([]);
  eventText(event: CaseEvent): string {
    const key = `api.events.${event.code}`;
    return event.code && this.i18n.has(key) ? this.i18n.text(key, event.parameters) : event.text;
  }
  readonly recent = computed(() => [...this.events()].reverse());
}
