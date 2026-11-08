import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { MapService } from '../../core/services/map.service';
import { MapIncidentPage } from './map-incident';

describe('MapIncidentPage', () => {
  it('requests the shared incident by id and renders its data without editing controls', async () => {
    const detail = vi.fn().mockReturnValue(of({ id: 'abc', title: 'Registro compartido', content: 'Descripción', state: 'Sector', altitude: 19, longitude: -99, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z', workflow: { status: 'open', currentStage: 'indications', phases: [{ stage: 'indications', validatedAt: null, data: { soil: 'arena' } }], history: [] } }));
    await TestBed.configureTestingModule({ imports: [MapIncidentPage], providers: [
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'abc' }) } } },
      { provide: MapService, useValue: { detail } },
    ] }).compileComponents();
    const fixture = TestBed.createComponent(MapIncidentPage);
    fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges();
    expect(detail).toHaveBeenCalledWith('abc');
    expect(fixture.nativeElement.textContent).toContain('Registro compartido');
    expect(fixture.nativeElement.textContent).toContain('Suelo');
    expect(fixture.nativeElement.querySelector('input, textarea, button')).toBeNull();
  });
  it('reports an unavailable incident without rendering another', async () => {
    const page = new MapIncidentPage({ snapshot: { paramMap: convertToParamMap({ id: 'missing' }) } } as never, { detail: () => throwError(() => new Error()) } as never);
    await page.ngOnInit();
    expect(page.note()).toBeNull();
    expect(page.error()).toContain('No fue posible');
  });
});
