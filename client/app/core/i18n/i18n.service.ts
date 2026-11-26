import { DOCUMENT } from '@angular/common';
import { Injectable, computed, inject, signal } from '@angular/core';
import { dictionaries, spanish } from './dictionary';

export type Language = 'es' | 'en';
export type TextParams = Readonly<Record<string, string | number>>;
export interface TextRef {
  key: string;
  params?: TextParams;
}
export type Message = string | TextRef | readonly Message[];
export interface MessageSignal {
  (): string;
  set(value: Message): void;
}
const STORAGE_KEY = 'mine.ui.language';

export function formatText(template: string, params: TextParams = {}): string {
  return template.replace(/\{([a-zA-Z][a-zA-Z0-9_]*)\}/g, (token, key: string) =>
    Object.hasOwn(params, key) ? String(params[key]) : token,
  );
}

@Injectable({ providedIn: 'root' })
export class I18nService {
  private readonly document = inject(DOCUMENT);
  private readonly selectedLanguage = signal<Language>(this.readPreference());
  readonly language = this.selectedLanguage.asReadonly();
  readonly locale = computed(() => (this.language() === 'en' ? 'en-US' : 'es-MX'));
  readonly dictionary = computed(() => dictionaries[this.language()]);

  constructor() {
    this.document.documentElement.lang = this.language();
  }

  setLanguage(language: Language): void {
    if (language !== 'en' && language !== 'es') return;
    this.selectedLanguage.set(language);
    this.document.documentElement.lang = language;
    try {
      this.document.defaultView?.localStorage.setItem(STORAGE_KEY, language);
    } catch {
      /* Memory selection still works. */
    }
  }

  format(template: string, params: TextParams = {}): string {
    return formatText(template, params);
  }

  text(key: string, params: TextParams = {}): string {
    const template = this.lookup(this.dictionary(), key) ?? this.lookup(spanish, key);
    const displayParams = { ...params };
    if (key.startsWith('api.errors.')) {
      if (
        typeof params['field'] === 'string' &&
        Object.hasOwn(this.dictionary().api.fields, params['field'])
      )
        displayParams['field'] = this.dictionary().api.fields[params['field']];
      if (typeof params['sensor'] === 'string')
        displayParams['sensor'] = this.catalogLabel('sensors', params['sensor']);
    }
    return this.format(template ?? this.dictionary().common.unavailableText, displayParams);
  }

  has(key: string): boolean {
    return this.lookup(this.dictionary(), key) !== undefined;
  }

  message(initial: Message = ''): MessageSignal {
    const state = signal<Message>(initial);
    const rendered = computed(() => this.render(state()));
    return Object.assign(rendered, { set: (value: Message) => state.set(value) });
  }

  render(message: Message): string {
    if (typeof message === 'string') return message;
    if (Array.isArray(message))
      return message
        .map((part) => this.render(part))
        .filter(Boolean)
        .join(' ');
    const ref = message as TextRef;
    return this.text(ref.key, ref.params);
  }

  errorRef(error: unknown, fallbackKey = 'common.unexpectedError'): Message {
    const response = error as {
      status?: number;
      error?: {
        code?: string;
        parameters?: TextParams;
        issueDetails?: Array<{ code: string; parameters?: TextParams }>;
      };
    } | null;
    if (response?.status === 0) return { key: 'common.networkError' };
    const body = response?.error;
    const key = body?.code ? `api.errors.${body.code}` : fallbackKey;
    const messages: Message[] = [
      { key: this.has(key) ? key : fallbackKey, params: body?.parameters },
    ];
    for (const issue of Array.isArray(body?.issueDetails) ? body.issueDetails : []) {
      if (!issue || typeof issue.code !== 'string') continue;
      const issueKey = `api.errors.${issue.code}`;
      if (this.has(issueKey)) messages.push({ key: issueKey, params: issue.parameters });
    }
    return messages;
  }

  date(
    value: string | Date | null | undefined,
    options: Intl.DateTimeFormatOptions = { dateStyle: 'medium', timeStyle: 'short' },
  ): string {
    if (!value) return '';
    const date = value instanceof Date ? value : new Date(value);
    return Number.isFinite(date.getTime())
      ? new Intl.DateTimeFormat(this.locale(), options).format(date)
      : String(value);
  }

  number(value: number, options?: Intl.NumberFormatOptions): string {
    return new Intl.NumberFormat(this.locale(), options).format(value);
  }

  catalogLabel(catalog: string, code: string, original: string = code): string {
    const catalogs = this.dictionary().api.catalogs;
    if (!Object.hasOwn(catalogs, catalog)) return original;
    const labels = catalogs[catalog];
    return Object.hasOwn(labels, code) ? labels[code] : original;
  }

  private lookup(root: unknown, key: string): string | undefined {
    let value = root;
    for (const part of key.split('.')) {
      if (!value || typeof value !== 'object' || !Object.hasOwn(value, part)) return undefined;
      value = (value as Record<string, unknown>)[part];
    }
    return typeof value === 'string' ? value : undefined;
  }

  private readPreference(): Language {
    try {
      return this.document.defaultView?.localStorage.getItem(STORAGE_KEY) === 'en' ? 'en' : 'es';
    } catch {
      return 'es';
    }
  }
}
