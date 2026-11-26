import { TestBed } from '@angular/core/testing';
import { I18nService } from './i18n.service';

describe('I18nService', () => {
  beforeEach(() => {
    localStorage.removeItem('mine.ui.language');
    TestBed.configureTestingModule({});
  });
  afterEach(() => {
    localStorage.removeItem('mine.ui.language');
    vi.restoreAllMocks();
  });

  it('starts in Spanish and switches the document language without reloading', () => {
    const service = TestBed.inject(I18nService);
    expect(service.language()).toBe('es');
    service.setLanguage('en');
    expect(service.language()).toBe('en');
    expect(service.dictionary().common.save).toBe('Save');
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('mine.ui.language')).toBe('en');
  });
  it('restores supported preferences and rejects invalid ones', () => {
    localStorage.setItem('mine.ui.language', 'en');
    expect(TestBed.inject(I18nService).language()).toBe('en');
    TestBed.resetTestingModule();
    localStorage.setItem('mine.ui.language', 'invalid');
    expect(TestBed.inject(I18nService).language()).toBe('es');
  });
  it('continues in memory when browser storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const service = TestBed.inject(I18nService);
    service.setLanguage('en');
    expect(service.language()).toBe('en');
  });
  it('retranslates visible messages, preserves parameters and escapes no user data into HTML', () => {
    const service = TestBed.inject(I18nService);
    const message = service.message({ key: 'common.selectedCount', params: { count: 2 } });
    expect(message()).toBe('2 seleccionados');
    service.setLanguage('en');
    expect(message()).toBe('2 selected');
    expect(service.format('{name}', { name: '<script>test</script>' })).toBe(
      '<script>test</script>',
    );
  });
  it('formats numbers with the current locale and uses a readable missing-key fallback', () => {
    const service = TestBed.inject(I18nService);
    service.setLanguage('en');
    expect(service.number(1250.5)).toBe('1,250.5');
    expect(service.text('missing.key')).toBe('Text unavailable');
  });
  it('retranslates API requirements and coded parameters while retaining unknown catalog labels', () => {
    const service = TestBed.inject(I18nService);
    const message = service.message(
      service.errorRef({
        error: {
          code: 'phase_validation_failed',
          issueDetails: [
            { code: 'catalog_selection_invalid', parameters: { field: 'review.finding' } },
            { code: 'veles_evidence_required', parameters: { sensor: 'Magnetometría' } },
          ],
        },
      }),
    );
    expect(message()).toContain("'dictamen'");
    service.setLanguage('en');
    expect(message()).toContain("'finding'");
    expect(message()).toContain('Magnetometry');
    expect(message()).not.toContain('Magnetometría');
    expect(service.catalogLabel('soils', 'new-soil', 'Custom soil')).toBe('Custom soil');
    expect(service.catalogLabel('soils', 'constructor', 'Unknown')).toBe('Unknown');
  });
  it('uses localized fallbacks for network, unknown and malformed error responses', () => {
    const service = TestBed.inject(I18nService);
    service.setLanguage('en');
    expect(service.render(service.errorRef({ status: 0 }))).toBe(
      service.dictionary().common.networkError,
    );
    expect(
      service.render(
        service.errorRef({ error: { code: 'new_code', issueDetails: [null, 'bad'] } }),
      ),
    ).toBe(service.dictionary().common.unexpectedError);
    expect(service.render(service.errorRef({ error: { issueDetails: 'not-an-array' } }))).toBe(
      service.dictionary().common.unexpectedError,
    );
  });
});
