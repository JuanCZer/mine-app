import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { NotePhaseEditor } from '../../components/notes/note-phase-editor';
import { NoteWorkflowPanel } from '../../components/notes/note-workflow';
import { DetectionStage, DETECTION_LAYERS } from '../../core/models/detection.model';
import { emptyNoteWorkflow } from '../../core/models/note-workflow.model';
import { Note } from '../../core/models/note.model';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotesPage } from './notes';

describe('Fases de una incidencia nueva', () => {
  let fixture: ComponentFixture<NotesPage>;
  let page: NotesPage;
  let http: HttpTestingController;

  beforeEach(async () => {
    localStorage.removeItem('mine.ui.language');
    TestBed.configureTestingModule({
      imports: [NotesPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({}) } },
        },
      ],
    });
    fixture = TestBed.createComponent(NotesPage);
    page = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne('/api/notes').flush([]);
    await vi.waitFor(() => expect(page.loading()).toBe(false));
    await render();
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
    localStorage.removeItem('mine.ui.language');
  });

  async function render() {
    fixture.detectChanges();
    for (const request of http.match((req) => req.url === '/api/catalogs')) {
      request.flush({
        version: 1,
        catalogs: Object.fromEntries(
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
        ),
      });
    }
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function panel(): NoteWorkflowPanel {
    return fixture.debugElement.query(By.directive(NoteWorkflowPanel)).componentInstance;
  }

  function editor(stage: DetectionStage): NotePhaseEditor {
    return fixture.debugElement
      .queryAll(By.directive(NotePhaseEditor))
      .map((item) => item.componentInstance as NotePhaseEditor)
      .find((item) => item.phase().stage === stage)!;
  }

  async function select(stage: DetectionStage) {
    const index = DETECTION_LAYERS.findIndex((layer) => layer.id === stage);
    const button = fixture.nativeElement.querySelectorAll('.progress-track button')[
      index
    ] as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    button.click();
    await render();
    expect(panel().selected()).toBe(stage);
  }

  function fillIdentity() {
    page.form.setValue({
      title: 'Alerta nueva',
      content: 'Observación',
      state: 'Corredor',
      altitude: 19,
      longitude: -99,
    });
  }

  function createdNote(): Note {
    const workflow = emptyNoteWorkflow();
    // The real API can serialize the empty phase fields in a different order.
    for (const phase of workflow.phases) {
      // PhaseData.Empty() in the API uses catalog sentinel values for omitted fields.
      phase.data.soil = 'unknown';
      phase.data.season = 'unknown';
      phase.data = Object.fromEntries(Object.entries(phase.data).reverse()) as typeof phase.data;
    }
    return {
      id: 'created-1',
      ...page.form.getRawValue(),
      revision: 0,
      createdAt: '',
      updatedAt: '',
      workflow,
    };
  }

  it('habilita fase 2 con cero incidencias y datos generales vacíos', async () => {
    expect(page.notes()).toHaveLength(0);
    expect(page.selected()).toBeNull();
    expect(page.form.invalid).toBe(true);
    expect(
      fixture.nativeElement.querySelector('.identity input[formControlName="title"]'),
    ).not.toBeNull();
    await select('reconnaissance');
    expect(editor('reconnaissance').form.enabled).toBe(true);
    expect(editor('reconnaissance').issues().length).toBeGreaterThan(0);
    expect(fixture.nativeElement.textContent).toContain('Guardar incidencia');
    expect(fixture.nativeElement.textContent).not.toContain('Borrador guardado;');
    await panel().save(editor('reconnaissance').draft());
    await panel().validate();
    http.expectNone((req) => req.url.startsWith('/api/notes'));
    await select('mitigation');
    await select('indications');
  });

  it('conserva campos, evidencia y fase seleccionada al registrar la incidencia', async () => {
    editor('indications').form.controls.baseline.setValue('Borrador de fase 1');
    editor('indications').evidencePanel!.form.controls.reference.setValue('Evidencia incompleta');
    await render();
    await select('mitigation');
    editor('mitigation').form.controls.artifactType.setValue('Borrador de fase 4');
    fillIdentity();
    await render();
    const originalForm = page.form;
    const originalPanel = panel();
    TestBed.inject(I18nService).setLanguage('en');
    await render();
    expect(page.form).toBe(originalForm);
    expect(panel()).toBe(originalPanel);
    expect(panel().isNew()).toBe(true);
    expect(panel().selected()).toBe('mitigation');
    expect(page.form.controls.title.value).toBe('Alerta nueva');
    expect(editor('mitigation').form.controls.artifactType.value).toBe('Borrador de fase 4');
    expect(fixture.nativeElement.textContent).toContain('Save incident');
    http.expectNone((request) => request.url.startsWith('/api/notes'));
    await select('indications');
    const createButton = fixture.nativeElement.querySelector(
      '.identity button[type="submit"]',
    ) as HTMLButtonElement;
    expect(createButton).not.toBeNull();
    expect(createButton.disabled).toBe(false);
    createButton.click();
    const request = http.expectOne('/api/notes');
    expect(request.request.method).toBe('POST');
    request.flush(createdNote());
    await vi.waitFor(() => expect(page.saving()).toBe(false));
    await render();

    expect(page.selected()?.id).toBe('created-1');
    expect(panel().selected()).toBe('indications');
    expect(editor('mitigation').form.controls.artifactType.value).toBe('Borrador de fase 4');
    expect(editor('indications').form.controls.baseline.value).toBe('Borrador de fase 1');
    expect(editor('indications').evidencePanel!.form.controls.reference.value).toBe(
      'Evidencia incompleta',
    );
    expect(page.phaseDirty()).toBe(true);

    await select('mitigation');
    editor('mitigation').save();
    const phaseRequest = http.expectOne('/api/notes/created-1/phases/mitigation');
    expect(phaseRequest.request.body.expectedRevision).toBe(0);
    expect(phaseRequest.request.body.data.soil).toBe('unknown');
    expect(phaseRequest.request.body.data.season).toBe('unknown');
    const saved = structuredClone(page.selected()!);
    saved.revision = 1;
    saved.workflow!.phases[3].data = phaseRequest.request.body.data;
    phaseRequest.flush(saved);
    await vi.waitFor(() => expect(panel().busy()).toBe(false));
    await render();
    expect(editor('mitigation').dirty()).toBe(false);
    expect(page.phaseDirty()).toBe(true);
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    expect(page.canLeave()).toBe(false);
    expect(confirm).toHaveBeenCalled();
    await select('indications');
    expect(editor('indications').form.controls.baseline.value).toBe('Borrador de fase 1');
  });

  it('mantiene los borradores si falla la creación y los limpia al descartarlos', async () => {
    await select('surveillance');
    editor('surveillance').form.controls.observationStartedAt.setValue('2026-11-25T10:00');
    fillIdentity();
    await render();
    const saving = page.save();
    http
      .expectOne('/api/notes')
      .flush({ error: 'No fue posible registrar.' }, { status: 503, statusText: 'Unavailable' });
    await saving;
    await render();
    expect(page.selected()).toBeNull();
    expect(editor('surveillance').form.controls.observationStartedAt.value).toBe(
      '2026-11-25T10:00',
    );
    expect(page.phaseDirty()).toBe(true);

    vi.spyOn(window, 'confirm').mockReturnValue(true);
    page.newNote();
    await render();
    expect(panel().selected()).toBe('indications');
    await select('surveillance');
    expect(editor('surveillance').form.controls.observationStartedAt.value).toBe('');
    expect(page.phaseDirty()).toBe(false);
    expect(page.form.controls.title.value).toBe('');
  });

  it('descarta borradores locales al elegir otra incidencia', async () => {
    await select('reconnaissance');
    editor('reconnaissance').form.controls.riskMapReference.setValue('Local sin guardar');
    await render();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    page.edit(createdNote());
    await render();
    await select('reconnaissance');
    expect(editor('reconnaissance').form.controls.riskMapReference.value).toBe('');
    expect(page.phaseDirty()).toBe(false);
  });

  it('conserva la fuente de una evidencia aunque todavía no tenga referencia ni descripción', async () => {
    editor('indications').evidencePanel!.form.controls.sensor.setValue('GPR');
    await render();
    expect(page.phaseDirty()).toBe(true);
    fillIdentity();
    await render();
    const saving = page.save();
    http.expectOne('/api/notes').flush(createdNote());
    await saving;
    await render();
    expect(editor('indications').evidencePanel!.form.controls.sensor.value).toBe('GPR');
    expect(page.phaseDirty()).toBe(true);
  });
});
