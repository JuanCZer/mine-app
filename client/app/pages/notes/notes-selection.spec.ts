import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { NotesService } from '../../core/services/notes.service';
import { NotesPage } from './notes';

describe('NotesPage link selection', () => {
  it('does not select a different incident when the requested id is absent', async () => {
    TestBed.configureTestingModule({providers: [
      { provide: NotesService, useValue: { list: () => of([{ id: 'other', title: 'Otra', content: '', state: '', altitude: 1, longitude: 1 }]) } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ note: 'missing' }) } } },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ] });
    const page = TestBed.runInInjectionContext(() => new NotesPage(TestBed.inject(NotesService), TestBed.inject(Router), TestBed.inject(ActivatedRoute)));
    await page.load();
    expect(page.selected()).toBeNull();
    expect(page.error()).toContain('incidencia solicitada');
  });
});
