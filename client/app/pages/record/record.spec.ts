import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { I18nService } from '../../core/i18n/i18n.service';
import { NotesService } from '../../core/services/notes.service';
import { RecordPage } from './record';

describe('RecordPage language changes', () => {
  beforeEach(() => localStorage.removeItem('mine.ui.language'));
  afterEach(() => localStorage.removeItem('mine.ui.language'));
  it('preserves the search filter and user data while translating the history table', async () => {
    const list = vi.fn(() =>
      of([
        {
          id: 'a',
          title: 'Registro original',
          content: 'Notas del usuario',
          state: 'Área propia',
          altitude: 19.432123,
          longitude: -99,
          createdAt: '2026-11-25T12:00:00Z',
          updatedAt: '2026-11-25T12:00:00Z',
        },
        {
          id: 'b',
          title: 'Oculto',
          content: '',
          state: '',
          altitude: null,
          longitude: null,
          createdAt: '',
          updatedAt: '',
        },
      ]),
    );
    TestBed.configureTestingModule({
      imports: [RecordPage],
      providers: [provideRouter([]), { provide: NotesService, useValue: { list } }],
    });
    const fixture = TestBed.createComponent(RecordPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const search = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    search.value = 'original';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('1 REGISTRO');
    TestBed.inject(I18nService).setLanguage('en');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('1 RECORD');
    expect(fixture.nativeElement.textContent).toContain('Incident tracking.');
    expect(fixture.nativeElement.textContent).toContain('Notas del usuario');
    expect(fixture.nativeElement.textContent).not.toContain('Oculto');
    expect(search.value).toBe('original');
    expect(list).toHaveBeenCalledTimes(1);
  });
});
