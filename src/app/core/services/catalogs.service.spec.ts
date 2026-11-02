import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { CatalogsService } from './catalogs.service';

describe('CatalogsService', () => {
  let service: CatalogsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CatalogsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('normaliza keys, deduplica códigos y conserva la petición en caché', async () => {
    const first = firstValueFrom(service.load(['soils', 'soils'], { stage: 'indications' }));
    const second = firstValueFrom(service.load(['soils'], { stage: 'indications' }));
    const request = http.expectOne(
      (candidate) =>
        candidate.url.endsWith('/api/catalogs') &&
        candidate.params.get('keys') === 'soils' &&
        candidate.params.get('stage') === 'indications' &&
        candidate.params.get('includeInactive') === 'true',
    );
    request.flush({
      version: 1,
      catalogs: {
        soils: [
          { id: '2', code: 'vertisol', label: 'Vertisol duplicado', sortOrder: 2, active: true },
          { id: '1', code: 'vertisol', label: 'Vertisol', sortOrder: 1, active: true },
        ],
      },
    });
    expect((await first).catalogs.soils).toHaveLength(1);
    expect((await second).catalogs.soils).toHaveLength(1);
  });

  it('reload invalida la caché y permite reintentar después de un error', async () => {
    const failed = firstValueFrom(service.load(['seasons']));
    http.expectOne((request) => request.params.get('keys') === 'seasons').flush('error', {
      status: 503,
      statusText: 'Unavailable',
    });
    await expect(failed).rejects.toBeTruthy();

    const retried = firstValueFrom(service.reload(['seasons']));
    http.expectOne((request) => request.params.get('keys') === 'seasons').flush({
      version: 2,
      catalogs: { seasons: [] },
    });
    expect((await retried).version).toBe(2);
  });

  it('reload invalida también una respuesta exitosa almacenada', async () => {
    const first = firstValueFrom(service.load(['soils']));
    http.expectOne((request) => request.params.get('keys') === 'soils').flush({
      version: 1,
      catalogs: { soils: [] },
    });
    await first;

    const refreshed = firstValueFrom(service.reload(['soils']));
    http.expectOne((request) => request.params.get('keys') === 'soils').flush({
      version: 2,
      catalogs: { soils: [] },
    });
    expect((await refreshed).version).toBe(2);
  });

  it('incluye un valor histórico inactivo una sola vez', () => {
    const options = service.selectable(
      [
        { id: '1', code: 'old', label: 'Anterior', sortOrder: 1, active: false },
        { id: '2', code: 'new', label: 'Actual', sortOrder: 2, active: true },
        { id: '3', code: 'new', label: 'Duplicado', sortOrder: 3, active: true },
      ],
      'old',
    );
    expect(options.map((item) => item.code)).toEqual(['old', 'new']);
  });
});
