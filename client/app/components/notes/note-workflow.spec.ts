import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { DetectionStage, DETECTION_LAYERS } from '../../core/models/detection.model';
import { Note } from '../../core/models/note.model';
import { emptyNoteWorkflow } from '../../core/models/note-workflow.model';
import { NotePhaseEditor } from './note-phase-editor';
import { NoteWorkflowPanel } from './note-workflow';
import { I18nService } from '../../core/i18n/i18n.service';

describe('Navegación libre de fases', () => {
  let fixture: ComponentFixture<NoteWorkflowPanel>;
  let panel: NoteWorkflowPanel;
  let http: HttpTestingController;
  let note: Note;
  let dirty: boolean;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [NoteWorkflowPanel],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(NoteWorkflowPanel);
    panel = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    note = {
      id: 'incident-1',
      title: 'Incidencia',
      content: 'Observación',
      state: 'Sector',
      altitude: 19,
      longitude: -99,
      createdAt: '',
      updatedAt: '',
      revision: 0,
      workflow: emptyNoteWorkflow(),
    };
    dirty = false;
    panel.dirtyChange.subscribe((value) => (dirty = value));
    panel.updated.subscribe((updated) => {
      note = updated;
      fixture.componentRef.setInput('note', updated);
    });
    fixture.componentRef.setInput('note', note);
    await render();
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('mine.ui.language');
  });

  async function render() {
    fixture.detectChanges();
    for (const request of http.match((req) => req.url === '/api/catalogs')) {
      const catalogs = Object.fromEntries(
        request.request.params
          .get('keys')!
          .split(',')
          .map((key) => [
            key,
            [
              {
                id: key,
                code: key === 'sensors' ? 'GPR' : 'test',
                label: key,
                active: true,
                sortOrder: 1,
              },
            ],
          ]),
      );
      request.flush({ version: 1, catalogs });
    }
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function editor(stage: DetectionStage): NotePhaseEditor {
    return fixture.debugElement
      .queryAll(By.directive(NotePhaseEditor))
      .map((element) => element.componentInstance as NotePhaseEditor)
      .find((component) => component.phase().stage === stage)!;
  }

  async function select(stage: DetectionStage) {
    const index = DETECTION_LAYERS.findIndex((layer) => layer.id === stage);
    const button = fixture.nativeElement.querySelectorAll('.progress-track button')[
      index
    ] as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    button.click();
    await render();
    expect(panel.selected()).toBe(stage);
  }

  it('permite saltar directamente a fase 4 y muestra los requisitos pendientes', async () => {
    await select('mitigation');
    expect(editor('mitigation').form.enabled).toBe(true);
    expect(editor('mitigation').issues().length).toBeGreaterThan(0);
    const body = fixture.nativeElement.querySelector('.progress-track').textContent;
    expect(body).toContain('pendientes');
    expect(body).not.toContain('Bloqueada');
  });

  it('traduce fases y pendientes sin perder borradores ni evidencia al cambiar de idioma', async () => {
    const first = editor('indications');
    first.form.controls.baseline.setValue('Mi referencia original');
    first.evidencePanel!.form.controls.reference.setValue('Mi evidencia incompleta');
    await select('reconnaissance');
    await select('surveillance');
    await select('mitigation');
    editor('mitigation').form.controls.artifactType.setValue('Mi artefacto');
    const i18n = TestBed.inject(I18nService);
    i18n.setLanguage('en');
    fixture.detectChanges();
    http.expectNone('/api/catalogs');
    await render();

    expect(panel.selected()).toBe('mitigation');
    expect(fixture.nativeElement.querySelector('.progress-track').textContent).toContain(
      'Early indications',
    );
    expect(fixture.nativeElement.querySelector('.progress-track').textContent).toContain(
      'pending requirements',
    );
    expect(editor('mitigation').issues()).toContain(
      'Confirm receipt of the exact VELES coordinate.',
    );
    expect(editor('indications').issues()).toContain('The comparison survey reference is missing.');
    expect(editor('reconnaissance').issues()).toContain(
      'The assessment is pending or inconclusive.',
    );
    expect(editor('surveillance').issues()).toContain(
      'Record the start and end of the surveillance period.',
    );
    expect(editor('mitigation').form.controls.artifactType.value).toBe('Mi artefacto');
    await select('indications');
    expect(editor('indications')).toBe(first);
    expect(first.form.controls.baseline.value).toBe('Mi referencia original');
    expect(first.evidencePanel!.form.controls.reference.value).toBe('Mi evidencia incompleta');
    expect(dirty).toBe(true);
    i18n.setLanguage('es');
    await render();
    expect(fixture.nativeElement.querySelector('.progress-track').textContent).toContain(
      'Indicios tempranos',
    );
    expect(first.form.controls.baseline.value).toBe('Mi referencia original');
    http.expectNone((request) => request.url.startsWith('/api/notes'));
  });

  it('retraduce el aviso de guardado visible sin volver a enviar el borrador', async () => {
    editor('indications').form.controls.baseline.setValue('Referencia conservada');
    await render();
    editor('indications').save();
    const request = http.expectOne('/api/notes/incident-1/phases/indications');
    const saved = structuredClone(note);
    saved.revision = 1;
    saved.workflow!.phases[0].data = request.request.body.data;
    request.flush(saved);
    await vi.waitFor(() => expect(panel.busy()).toBe(false));
    await render();
    expect(panel.message()).toContain('Borrador guardado.');

    TestBed.inject(I18nService).setLanguage('en');
    await render();
    expect(panel.message()).toContain('Draft saved.');
    expect(editor('indications').form.controls.baseline.value).toBe('Referencia conservada');
    http.expectNone((request) => request.url.startsWith('/api/notes'));
  });

  it('conserva campos inválidos y evidencia a medio escribir al cambiar de fase', async () => {
    const first = editor('indications');
    first.form.controls.baseline.setValue('Borrador local');
    first.form.controls.flightDurationMin.setValue(100);
    first.evidencePanel!.form.controls.reference.setValue('Evidencia sin terminar');
    await render();
    await select('mitigation');
    editor('mitigation').form.controls.artifactType.setValue('Otro borrador');
    await render();
    await select('indications');
    expect(editor('indications').form.controls.baseline.value).toBe('Borrador local');
    expect(editor('indications').form.controls.flightDurationMin.value).toBe(100);
    expect(editor('indications').evidencePanel!.form.controls.reference.value).toBe(
      'Evidencia sin terminar',
    );
    expect(dirty).toBe(true);
    await select('mitigation');
    expect(editor('mitigation').form.controls.artifactType.value).toBe('Otro borrador');
  });

  it('guarda una fase futura incompleta sin borrar cambios de otra fase', async () => {
    editor('indications').form.controls.baseline.setValue('Pendiente local');
    await render();
    await select('mitigation');
    editor('mitigation').form.controls.artifactType.setValue('Parcial');
    await render();
    editor('mitigation').save();
    const request = http.expectOne('/api/notes/incident-1/phases/mitigation');
    expect(request.request.body.expectedRevision).toBe(0);
    expect(request.request.body.data.mitigation.artifactType).toBe('Parcial');
    const updated = structuredClone(note);
    updated.revision = 1;
    updated.workflow!.phases[3].data = request.request.body.data;
    request.flush(updated);
    await vi.waitFor(() => expect(panel.busy()).toBe(false));
    await render();
    expect(dirty).toBe(true);
    expect(editor('mitigation').dirty()).toBe(false);
    await select('indications');
    expect(editor('indications').form.controls.baseline.value).toBe('Pendiente local');
    editor('indications').save();
    const next = http.expectOne('/api/notes/incident-1/phases/indications');
    expect(next.request.body.expectedRevision).toBe(1);
    next.flush({ error: 'La incidencia cambió.' }, { status: 409, statusText: 'Conflict' });
    await vi.waitFor(() => expect(panel.busy()).toBe(false));
    await render();
    await select('surveillance');
    await select('indications');
    expect(editor('indications').form.controls.baseline.value).toBe('Pendiente local');
    expect(dirty).toBe(true);
  });

  it('mantiene la validación formal en orden aunque se pueda editar cualquier fase', async () => {
    await select('surveillance');
    await panel.validate();
    http.expectNone((req) => req.url.endsWith('/validate'));
    expect(editor('surveillance').form.enabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Para validar esta fase');
  });

  it('elimina borradores locales al recargar explícitamente y cambiar de incidencia', async () => {
    editor('indications').form.controls.baseline.setValue('Descartar en recarga');
    await render();
    await select('surveillance');
    editor('surveillance').form.controls.observationStartedAt.setValue('2026-11-23T10:00');
    fixture.componentRef.setInput('resetToken', 1);
    await render();
    await select('indications');
    expect(editor('indications').form.controls.baseline.value).toBe('');
    expect(dirty).toBe(false);
    editor('indications').form.controls.baseline.setValue('No pertenece a otra incidencia');
    fixture.componentRef.setInput('note', { ...note, id: 'incident-2' });
    await render();
    expect(editor('indications').form.controls.baseline.value).toBe('');
    expect(dirty).toBe(false);
  });

  it('permite consultar borradores posteriores de una incidencia descartada sin editarlos', async () => {
    note.workflow!.status = 'discarded';
    note.workflow!.currentStage = null;
    note.workflow!.phases[0].validatedAt = '2026-11-23T10:00:00Z';
    note.workflow!.phases[1].validatedAt = '2026-11-23T11:00:00Z';
    note.workflow!.phases[3].data.mitigation.artifactType = 'Borrador conservado';
    fixture.componentRef.setInput('note', structuredClone(note));
    await render();
    await select('mitigation');
    expect(editor('mitigation').form.disabled).toBe(true);
    expect(editor('mitigation').form.controls.artifactType.value).toBe('Borrador conservado');
    await panel.save(editor('mitigation').draft());
    http.expectNone((req) => req.url.startsWith('/api/notes/'));
  });
});
