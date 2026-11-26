import { spanish, english } from './dictionary';

function entries(value: unknown, prefix = ''): Record<string, string> {
  return Object.fromEntries(
    Object.entries(value as Record<string, unknown>).flatMap(([key, item]) => {
      const path = prefix ? `${prefix}.${key}` : key;
      return typeof item === 'string' ? [[path, item]] : Object.entries(entries(item, path));
    }),
  );
}
function parameters(text: string): string[] {
  return [...text.matchAll(/\{([a-zA-Z][a-zA-Z0-9_]*)\}/g)].map((match) => match[1]).sort();
}
describe('translation dictionary contract', () => {
  it('provides the same nonempty keys and interpolation parameters in both languages', () => {
    const es = entries(spanish);
    const en = entries(english);
    expect(Object.keys(en).sort()).toEqual(Object.keys(es).sort());
    for (const [key, value] of Object.entries(es)) {
      expect(value.trim(), key).not.toBe('');
      expect(en[key].trim(), key).not.toBe('');
      expect(parameters(en[key]), key).toEqual(parameters(value));
    }
  });
});
