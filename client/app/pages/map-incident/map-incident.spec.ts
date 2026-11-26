import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { MapService } from '../../core/services/map.service';
import { MapIncidentPage } from './map-incident';
import { I18nService } from '../../core/i18n/i18n.service';

describe('MapIncidentPage', () => {
  beforeEach(() => localStorage.removeItem('mine.ui.language'));
  afterEach(() => localStorage.removeItem('mine.ui.language'));
  it('requests the shared incident by id and renders its data without editing controls', async () => {
    const detail = vi
      .fn()
      .mockReturnValue(
        of({
          id: 'abc',
          title: 'Registro compartido',
          content: 'Descripción',
          state: 'Sector',
          altitude: 19,
          longitude: -99,
          createdAt: '2026-10-01T00:00:00Z',
          updatedAt: '2026-10-01T00:00:00Z',
          workflow: {
            status: 'open',
            currentStage: 'indications',
            phases: [{ stage: 'indications', validatedAt: null, data: { soil: 'arena' } }],
            history: [],
          },
        }),
      );
    await TestBed.configureTestingModule({
      imports: [MapIncidentPage],
      providers: [
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 'abc' }) } },
        },
        { provide: MapService, useValue: { detail } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(MapIncidentPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(detail).toHaveBeenCalledWith('abc');
    expect(fixture.nativeElement.textContent).toContain('Registro compartido');
    expect(fixture.nativeElement.textContent).toContain('Suelo');
    expect(fixture.nativeElement.querySelector('input, textarea, button')).toBeNull();
    TestBed.inject(I18nService).setLanguage('en');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Incident details');
    expect(fixture.nativeElement.textContent).toContain('Soil');
    expect(fixture.nativeElement.textContent).toContain('Registro compartido');
    expect(fixture.nativeElement.textContent).toContain('Descripción');
    expect(fixture.nativeElement.textContent).toContain('Early indications');
    expect(detail).toHaveBeenCalledTimes(1);
  });
  it('reports an unavailable incident without rendering another', async () => {
    const page = TestBed.runInInjectionContext(
      () =>
        new MapIncidentPage(
          { snapshot: { paramMap: convertToParamMap({ id: 'missing' }) } } as never,
          { detail: () => throwError(() => new Error()) } as never,
        ),
    );
    await page.ngOnInit();
    expect(page.note()).toBeNull();
    expect(page.error()).toContain('No fue posible');
  });
  it('preserves audited labels in validated phase data and original history', () => {
    const page = TestBed.runInInjectionContext(() => new MapIncidentPage({} as never, {} as never));
    const phase = {
      data: { soil: 'vertisol', review: { notes: 'Texto del usuario' } },
      catalogSnapshot: {
        selections: [
          {
            field: 'soil',
            catalogKey: 'soils',
            code: 'vertisol',
            label: 'Etiqueta al validar',
            version: 1,
          },
        ],
      },
    };
    TestBed.inject(I18nService).setLanguage('en');
    expect(page.fields(phase)).toContainEqual({
      label: 'Data · Soil',
      value: 'Etiqueta al validar',
    });
    expect(page.fields(phase)).toContainEqual({
      label: 'Data · Review · Review notes',
      value: 'Texto del usuario',
    });
    expect(
      page.historyFields([
        { id: 'old', at: '2026-11-25T12:00:00Z', actor: 'Responsable', text: 'Evento histórico' },
      ]),
    ).toContainEqual({ label: '1 · Description', value: 'Evento histórico' });
  });
});
