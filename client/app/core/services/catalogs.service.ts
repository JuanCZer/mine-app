import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, catchError, map, shareReplay, throwError } from 'rxjs';
import {
  CatalogItem,
  CatalogKey,
  CatalogQuery,
  CatalogResponse,
} from '../models/catalog.model';

const API_URL = '/api/catalogs';

@Injectable({ providedIn: 'root' })
export class CatalogsService {
  private readonly cache = new Map<string, Observable<CatalogResponse>>();

  constructor(private readonly http: HttpClient) {}

  load(keys: readonly CatalogKey[], query: CatalogQuery = {}): Observable<CatalogResponse> {
    const normalizedKeys = [...new Set(keys)].sort();
    const cacheKey = this.cacheKey(normalizedKeys, query);
    const cached = this.cache.get(cacheKey);
    if (cached) return cached;

    let params = new HttpParams()
      .set('keys', normalizedKeys.join(','))
      .set('includeInactive', String(query.includeInactive ?? true));
    if (query.stage) params = params.set('stage', query.stage);

    const request = this.http.get<CatalogResponse>(API_URL, { params }).pipe(
      map((response) => this.normalize(response, normalizedKeys)),
      catchError((error) => {
        this.cache.delete(cacheKey);
        return throwError(() => error);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.cache.set(cacheKey, request);
    return request;
  }

  reload(keys: readonly CatalogKey[], query: CatalogQuery = {}): Observable<CatalogResponse> {
    this.cache.delete(this.cacheKey([...new Set(keys)].sort(), query));
    return this.load(keys, query);
  }

  label(items: readonly CatalogItem[], code: string, missing = code): string {
    return items.find((item) => item.code === code)?.label ?? missing;
  }

  selectable(items: readonly CatalogItem[], selected: string | readonly string[] = []): CatalogItem[] {
    const selectedCodes = new Set(Array.isArray(selected) ? selected : [selected]);
    return this.dedupe(items).filter((item) => item.active || selectedCodes.has(item.code));
  }

  private normalize(response: CatalogResponse, requestedKeys: readonly CatalogKey[]): CatalogResponse {
    const catalogs: Partial<Record<CatalogKey, CatalogItem[]>> = {};
    for (const key of requestedKeys) catalogs[key] = this.dedupe(response.catalogs?.[key] ?? []);
    return {
      version: Number.isFinite(response.version) ? response.version : 0,
      catalogs,
    };
  }

  private dedupe(items: readonly CatalogItem[]): CatalogItem[] {
    const byCode = new Map<string, CatalogItem>();
    for (const item of items) {
      const code = String(item.code ?? '').trim();
      if (!code || byCode.has(code)) continue;
      byCode.set(code, {
        id: String(item.id ?? code),
        code,
        label: String(item.label ?? code),
        sortOrder: Number.isFinite(item.sortOrder) ? item.sortOrder : 0,
        active: item.active !== false,
      });
    }
    return [...byCode.values()].sort(
      (left, right) => left.sortOrder - right.sortOrder || left.label.localeCompare(right.label, 'es'),
    );
  }

  private cacheKey(keys: readonly CatalogKey[], query: CatalogQuery): string {
    return `${keys.join(',')}|${query.stage ?? ''}|${query.includeInactive ?? true}`;
  }
}
