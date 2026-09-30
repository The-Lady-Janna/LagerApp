// Typen zu scripts/i18n-check.mjs (damit der Vitest-Test das Skript typsicher importieren kann).

export const DEFAULT_ROOT: string
export const SOURCE_LANGUAGE: string

export type I18nProblemCategory =
  | 'missingLanguage'
  | 'missingNamespace'
  | 'missingKey'
  | 'extraKey'
  | 'placeholderMismatch'
  | 'emptyValue'
  | 'undefinedKey'
  | 'unusedKey'

export interface I18nCheckResult {
  /** true, wenn keine Kategorie Einträge hat. */
  ok: boolean
  problems: Record<I18nProblemCategory, string[]>
  stats: { languages: string[]; namespaces: string[]; keys: number }
}

export function flatten(value: Record<string, unknown>, prefix?: string, into?: Map<string, string>): Map<string, string>
/** { sprache: { namespace: Map(schlüssel → text) } } */
export function loadLocales(root?: string): Record<string, Record<string, Map<string, string>>>
/** { 'src/pfad/datei.tsx': text } — alle Quelltexte unter src/ ohne Tests und Sprachdateien. */
export function loadSources(root?: string): Record<string, string>
export function scanSources(sources: Record<string, string>, namespaces?: string[]): { exact: Map<string, Set<string>>; prefixes: Map<string, Set<string>> }
/** Die reine Prüfung auf Daten im Speicher (für Tests). */
export function analyze(input: { locales: Record<string, Record<string, Map<string, string>>>; sources: Record<string, string> }): I18nCheckResult
/** Die Prüfung auf der Platte. */
export function checkI18n(options?: { root?: string }): I18nCheckResult
export function formatReport(result: I18nCheckResult): string
