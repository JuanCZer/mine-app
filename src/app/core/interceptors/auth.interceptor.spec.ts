import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { apiFailureKind, authInterceptor } from './auth.interceptor';

describe('authInterceptor', () => {
  let client: HttpClient;
  let http: HttpTestingController;
  let auth: { handleApiFailure: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    auth = { handleApiFailure: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
      ],
    });
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('does not add bearer authentication to same-origin API calls', async () => {
    const result = firstValueFrom(client.get('/api/notes'));
    const request = http.expectOne('/api/notes');
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush([]);
    await result;
  });

  it('reports failures only for the application API', async () => {
    const external = firstValueFrom(client.get('https://example.org/resource'));
    http.expectOne('https://example.org/resource').flush(null, {
      status: 401,
      statusText: 'Unauthorized',
    });
    await expect(external).rejects.toBeTruthy();
    expect(auth.handleApiFailure).not.toHaveBeenCalled();

    const own = firstValueFrom(client.get('/api/notes'));
    http.expectOne('/api/notes').flush(null, { status: 403, statusText: 'Forbidden' });
    await expect(own).rejects.toBeTruthy();
    expect(auth.handleApiFailure).toHaveBeenCalledWith('forbidden', '/api/notes', expect.anything());
  });

  it('distinguishes authentication, authorization, throttling and network errors', () => {
    expect(apiFailureKind(new HttpErrorResponse({ status: 401 }))).toBe('unauthorized');
    expect(apiFailureKind(new HttpErrorResponse({ status: 403 }))).toBe('forbidden');
    expect(apiFailureKind(new HttpErrorResponse({ status: 429 }))).toBe('rate-limited');
    expect(apiFailureKind(new HttpErrorResponse({ status: 0 }))).toBe('network');
    expect(apiFailureKind(new HttpErrorResponse({ status: 503 }))).toBe('server');
  });
});
