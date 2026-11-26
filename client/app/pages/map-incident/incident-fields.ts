import { es } from '../../core/i18n/map-dictionary';

function label(key: string, dictionary: typeof es): string {
  return (
    (dictionary.fields as Record<string, string>)[key] ??
    key.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase())
  );
}
export function incidentFields(
  value: unknown,
  dictionary: typeof es = es,
  formatValue?: (value: unknown, key: string, path: readonly string[]) => string | undefined,
): { label: string; value: string }[] {
  const result: { label: string; value: string }[] = [];
  const visit = (item: unknown, path: string[], keys: string[]) => {
    if (Array.isArray(item)) {
      if (!item.length) {
        result.push({ label: path.join(' · '), value: dictionary.emptyRecords });
        return;
      }
      if (item.every((v) => v === null || typeof v !== 'object')) {
        result.push({
          label: path.join(' · '),
          value: item
            .map((value) => formatValue?.(value, keys.at(-1) ?? '', keys) ?? String(value))
            .join(', '),
        });
        return;
      }
      item.forEach((entry, index) => visit(entry, [...path, String(index + 1)], keys));
      return;
    }
    if (item && typeof item === 'object') {
      for (const [key, entry] of Object.entries(item))
        visit(entry, [...path, label(key, dictionary)], [...keys, key]);
      return;
    }
    result.push({
      label: path.join(' · '),
      value:
        formatValue?.(item, keys.at(-1) ?? '', keys) ??
        (item === null || item === ''
          ? dictionary.emptyData
          : typeof item === 'boolean'
            ? item
              ? dictionary.yes
              : dictionary.no
            : String(item)),
    });
  };
  visit(value, [], []);
  return result;
}
