/// <summary>
/// Die aktive Sprache und die dazu passende Intl-Locale — bewusst OHNE Abhängigkeiten (kein i18next, kein Vite): lib/format.ts
/// liest die Locale hier, und die .NET-Tests führen lib/format.ts direkt unter Node aus (ohne Bundler). Ohne Initialisierung
/// gilt Deutsch. src/i18n/index.ts hält den Wert bei jedem Sprachwechsel aktuell (setActiveLanguage).
/// </summary>

export const SUPPORTED_LANGUAGES = ['de', 'en'] as const
export type Language = (typeof SUPPORTED_LANGUAGES)[number]
export const DEFAULT_LANGUAGE: Language = 'de'

/** Die Sprache ("de" oder "en") zu einem beliebigen Sprachcode ("en-US" → "en"); Unbekanntes → Standardsprache. */
export function normalizeLanguage(code: string | undefined | null): Language {
  const base = (code ?? '').toLowerCase().split(/[-_]/)[0]
  return (SUPPORTED_LANGUAGES as readonly string[]).includes(base) ? (base as Language) : DEFAULT_LANGUAGE
}

let active: Language = DEFAULT_LANGUAGE

/** Setzt die aktive Sprache (nur src/i18n/index.ts ruft das auf). */
export function setActiveLanguage(code: string | undefined | null): void {
  active = normalizeLanguage(code)
}

/** Die aktive Sprache. */
export function currentLanguage(): Language {
  return active
}

/** Locale für Intl (Datum, Zahl, Währung) zur aktiven Sprache. Englisch = britisch: Tag zuerst, 24-Stunden-Uhr, Euro als "€". */
export function intlLocale(): string {
  return active === 'en' ? 'en-GB' : 'de-DE'
}
