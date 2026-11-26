import { TestBed } from '@angular/core/testing';
import { I18nService } from '../../core/i18n/i18n.service';
import { CaseEvent } from '../../core/models/detection.model';
import { CaseTimeline } from './case-timeline';

describe('CaseTimeline language changes', () => {
  beforeEach(() => localStorage.removeItem('mine.ui.language'));
  afterEach(() => localStorage.removeItem('mine.ui.language'));

  it('translates coded events while keeping historical text and responsible names intact', () => {
    TestBed.configureTestingModule({ imports: [CaseTimeline] });
    const fixture = TestBed.createComponent(CaseTimeline);
    const events: CaseEvent[] = [
      {
        id: 'old',
        at: '2026-11-25T12:00:00Z',
        actor: 'Operador histórico',
        text: 'Registro original sin código.',
      },
      {
        id: 'new',
        at: '2026-11-25T13:00:00Z',
        actor: 'Responsable original',
        text: 'Borrador guardado: fase 2. Pendiente de validación.',
        code: 'phase_saved',
        parameters: { stage: 2 },
      },
      {
        id: 'future',
        at: '2026-11-25T14:00:00Z',
        actor: 'Otro responsable',
        text: 'Evento de una versión posterior.',
        code: 'unrecognized_future_event',
      },
    ];
    fixture.componentRef.setInput('events', events);
    fixture.detectChanges();
    const originalDate = fixture.nativeElement.querySelector('time').textContent;
    expect(fixture.nativeElement.textContent).toContain('Borrador guardado: fase 2.');

    TestBed.inject(I18nService).setLanguage('en');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Draft saved: phase 2.');
    expect(fixture.nativeElement.textContent).toContain('Registro original sin código.');
    expect(fixture.nativeElement.textContent).toContain('Evento de una versión posterior.');
    expect(fixture.nativeElement.textContent).toContain('Responsable original');
    expect(fixture.nativeElement.querySelector('time').textContent).not.toBe(originalDate);
    expect(events[1].text).toBe('Borrador guardado: fase 2. Pendiente de validación.');
    expect(events[1].parameters).toEqual({ stage: 2 });
  });
});
