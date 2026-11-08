import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Note } from '../../core/models/note.model';
import { emptyNoteWorkflow, NotePhase } from '../../core/models/note-workflow.model';
import { NotePhaseEditor } from './note-phase-editor';

const catalog = (code: string, label = code, active = true) => ({
  id: code,
  code,
  label,
  sortOrder: 1,
  active,
});

describe('NotePhaseEditor catalogs', () => {
  let fixture: ComponentFixture<NotePhaseEditor>;
  let component: NotePhaseEditor;
  let http: HttpTestingController;
  let phase: NotePhase;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [NotePhaseEditor],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(NotePhaseEditor);
    component = fixture.componentInstance;
    phase = structuredClone(emptyNoteWorkflow().phases[0]);
    phase.data.soil = 'legacy-soil';
    const note: Note = {
      id: 'note-1',
      title: 'Incidencia',
      content: 'Cambio',
      state: 'Corredor',
      altitude: 19,
      longitude: -99,
      createdAt: '',
      updatedAt: '',
      workflow: emptyNoteWorkflow(),
    };
    fixture.componentRef.setInput('note', note);
    fixture.componentRef.setInput('phase', phase);
    fixture.componentRef.setInput('editable', true);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  function flushIndications(activeSoil = true) {
    http
      .expectOne((request) => request.params.get('stage') === 'indications')
      .flush({
        version: 1,
        catalogs: {
          sensors: [catalog('LiDAR')],
          soils: [
            catalog('legacy-soil', 'Suelo histórico', false),
            ...(activeSoil ? [catalog('active-soil', 'Suelo actual')] : []),
          ],
          seasons: [catalog('dry', 'Seca')],
          platforms: [catalog('if1200a', 'IF1200A')],
          indicationSources: [catalog('if1200a-lidar', 'LiDAR')],
          indicationSignatures: [catalog('surface-removal', 'Remoción')],
        },
      });
  }

  it('muestra la selección histórica inactiva sin hacerla seleccionable de nuevo', async () => {
    flushIndications();
    await fixture.whenStable();
    fixture.detectChanges();
    const option = [...fixture.nativeElement.querySelectorAll('option')].find(
      (candidate: HTMLOptionElement) => candidate.value === 'legacy-soil',
    ) as HTMLOptionElement;
    expect(option.textContent).toContain('(inactivo)');
    expect(option.disabled).toBe(true);
    expect(component.form.controls.soil.value).toBe('legacy-soil');
  });

  it('permite conservar una selección histórica aunque ya no queden alternativas activas', async () => {
    flushIndications(false);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.catalogsReady()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Suelo histórico (inactivo)');
  });

  it('normaliza firmas nulas del API antes de renderizar la fase', async () => {
    flushIndications();
    await fixture.whenStable();
    const phaseWithNullSignatures = structuredClone(phase);
    phaseWithNullSignatures.data.indications.signatures = null as never;
    fixture.componentRef.setInput('phase', phaseWithNullSignatures);

    expect(() => fixture.detectChanges()).not.toThrow();
    expect(component.form.controls.signatures.value).toEqual([]);
    expect(component.draft().indications.signatures).toEqual([]);
  });

  it('deduplica multiselecciones y conserva el borrador si falla una recarga', async () => {
    flushIndications();
    await fixture.whenStable();
    component.toggleSignature('surface-removal', true);
    component.toggleSignature('surface-removal', true);
    expect(component.form.controls.signatures.value).toEqual(['surface-removal']);
    component.form.controls.alternativeCauses.setValue('Borrador que debe persistir');

    const retry = component.loadCatalogs('indications', true);
    http
      .expectOne((request) => request.params.get('stage') === 'indications')
      .flush('fallo', {
        status: 503,
        statusText: 'Unavailable',
      });
    await retry;
    expect(component.draft().indications.alternativeCauses).toBe('Borrador que debe persistir');
    expect(component.catalogsError()).toContain('El borrador se conserva');
  });

  it('ignora una respuesta tardía y conserva el catálogo de la solicitud más reciente', async () => {
    const latest = component.loadCatalogs('indications', true);
    const requests = http.match((request) => request.params.get('stage') === 'indications');
    expect(requests).toHaveLength(2);

    requests[1].flush({
      version: 2,
      catalogs: {
        sensors: [catalog('LiDAR')],
        soils: [catalog('legacy-soil', 'Etiqueta vigente')],
        seasons: [catalog('dry', 'Seca')],
        platforms: [catalog('if1200a', 'IF1200A')],
        indicationSources: [catalog('if1200a-lidar', 'LiDAR')],
        indicationSignatures: [catalog('surface-removal', 'Remoción')],
      },
    });
    await latest;
    requests[0].flush({
      version: 1,
      catalogs: {
        sensors: [catalog('LiDAR')],
        soils: [catalog('legacy-soil', 'Etiqueta obsoleta')],
        seasons: [catalog('dry', 'Seca')],
        platforms: [catalog('if1200a', 'IF1200A')],
        indicationSources: [catalog('if1200a-lidar', 'LiDAR')],
        indicationSignatures: [catalog('surface-removal', 'Remoción')],
      },
    });

    expect(component.form.controls.soil.value).toBe('legacy-soil');
    expect(component.options('soils', 'legacy-soil')[0].label).toBe('Etiqueta vigente');
  });

  it('muestra la etiqueta congelada de una fase validada sin cambiar su código', async () => {
    phase.validatedAt = '2026-10-16T12:00:00Z';
    phase.catalogSnapshot = {
      version: 7,
      selections: [
        {
          field: 'soil',
          catalogKey: 'soils',
          code: 'legacy-soil',
          label: 'Etiqueta al validar',
          version: 7,
        },
      ],
    };
    fixture.componentRef.setInput('phase', phase);
    fixture.detectChanges();
    flushIndications();
    await fixture.whenStable();

    expect(component.form.controls.soil.value).toBe('legacy-soil');
    expect(component.options('soils', 'legacy-soil')[0].label).toBe('Etiqueta al validar');
  });
});
