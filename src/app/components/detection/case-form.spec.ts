import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CaseForm } from './case-form';

const item = (code: string, label: string, active = true, sortOrder = 0) => ({
  id: code,
  code,
  label,
  sortOrder,
  active,
});

describe('CaseForm catalogs', () => {
  let fixture: ComponentFixture<CaseForm>;
  let component: CaseForm;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CaseForm],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(CaseForm);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  function flushCatalogs() {
    http.expectOne((request) => request.url.endsWith('/api/catalogs')).flush({
      version: 1,
      catalogs: {
        mexicoStates: [item('MX-CMX', 'Ciudad de México')],
        soils: [item('soil-db', 'Suelo desde BD')],
        seasons: [item('season-db', 'Temporada desde BD')],
        riskLevels: [item('risk-db', 'Prioridad desde BD')],
      },
    });
    fixture.detectChanges();
  }

  it('renderiza opciones de la API y envía códigos, no etiquetas', async () => {
    flushCatalogs();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Suelo desde BD');
    expect(component.form.controls.soil.value).toBe('');

    let emitted: unknown;
    component.saved.subscribe((value) => (emitted = value));
    component.form.setValue({
      title: 'Caso',
      state: 'MX-CMX',
      municipality: 'Zona',
      latitude: 19,
      longitude: -99,
      soil: 'soil-db',
      season: 'season-db',
      priority: 'risk-db',
      owner: 'Responsable',
      description: 'Observación',
    });
    component.submit();
    expect(emitted).toMatchObject({
      state: 'MX-CMX',
      soil: 'soil-db',
      season: 'season-db',
      priority: 'risk-db',
    });
  });

  it('no borra el borrador y bloquea crear cuando falla el catálogo', async () => {
    component.form.controls.title.setValue('Borrador conservado');
    http.expectOne((request) => request.url.endsWith('/api/catalogs')).flush('fallo', {
      status: 503,
      statusText: 'Unavailable',
    });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(component.form.controls.title.value).toBe('Borrador conservado');
    expect(component.catalogReady()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('No fue posible cargar');
  });
});
