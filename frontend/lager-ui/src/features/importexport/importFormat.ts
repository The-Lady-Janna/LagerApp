import type { ExportKind, ImportKind, ImportResultDto } from '../../api/importExportHooks'
import { intlLocale, t } from '../../i18n'

/// <summary>
/// Kleine, reine Hilfen der Seite "Import & Export" (Texte, Formatierung, Entscheidungen), getrennt von den Komponenten,
/// damit sie sich ohne Rendern testen lassen.
/// </summary>

const SIZE_UNITS = ['B', 'KB', 'MB', 'GB'] as const

/** "1,5 MB" (en: "1.5 MB") — Dateigröße im Zahlenformat der aktiven Sprache (1 KB = 1024 Byte); ungültige Werte ergeben "—". */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—'
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < SIZE_UNITS.length - 1) {
    value /= 1024
    unit += 1
  }
  const text = new Intl.NumberFormat(intlLocale(), { maximumFractionDigits: unit === 0 ? 0 : 1 }).format(value)
  return `${text} ${SIZE_UNITS[unit]}`
}

/// Die Beschriftungen, Beschreibungen und Pflichtspalten der Import-/Export-Arten stehen im Namespace importexport
/// (import.<art>.label|description|required, export.<art>.label|description).

export interface ExportInfo {
  /** Hat die Datei einen Zeitraum-Filter (von/bis)? */
  hasRange?: boolean
  /** Hat die Datei einen Benutzer-Filter (Audit)? */
  hasUser?: boolean
}

export const EXPORT_INFO: Record<ExportKind, ExportInfo> = {
  articles: {},
  stock: {},
  orders: {},
  movements: { hasRange: true },
  audit: { hasRange: true, hasUser: true },
}

export const EXPORT_KINDS: readonly ExportKind[] = ['articles', 'stock', 'orders', 'movements', 'audit']
export const IMPORT_KINDS: readonly ImportKind[] = ['articles', 'stock', 'orders']

/** Wie viele Datensätze die Übernahme schreiben würde (neu plus aktualisiert). */
export function writableCount(result: Pick<ImportResultDto, 'created' | 'updated'>): number {
  return result.created + result.updated
}

/**
 * Darf "Übernehmen" gedrückt werden? Nur nach einem Trockenlauf, wenn es etwas zu schreiben gibt und entweder kein Fehler vorliegt
 * oder die fehlerhaften Zeilen ausdrücklich ausgelassen werden.
 */
export function canApplyImport(preview: ImportResultDto | null, skipErrors: boolean): boolean {
  if (!preview || !preview.dryRun) return false
  if (writableCount(preview) === 0) return false
  return preview.errorCount === 0 || skipErrors
}

/** Warum "Übernehmen" gesperrt ist (Text für den Nutzer), sonst null. */
export function applyBlockedReason(preview: ImportResultDto | null, skipErrors: boolean): string | null {
  if (!preview) return t('importexport:blocked.check')
  if (writableCount(preview) === 0) {
    return preview.errorCount > 0 ? t('importexport:blocked.noneFree') : t('importexport:blocked.nothing')
  }
  if (preview.errorCount > 0 && !skipErrors) return t('importexport:blocked.rowErrors')
  return null
}

/** Die Fehlerzeile ohne Schlüssel bekommt einen Strich, damit die Tabellenzelle nicht leer wirkt. */
export function errorKey(key: string | null): string {
  return key && key.trim() !== '' ? key : '—'
}
