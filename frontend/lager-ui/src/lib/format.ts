/// <summary>
/// Einheitliche Anzeige von Zeitpunkten, Datumswerten, Zahlen und Geldbeträgen in der aktiven Sprache (Deutsch: de-DE,
/// Englisch: en-GB — siehe src/i18n). Die Locale wird bei JEDEM Aufruf neu gelesen, ein Sprachwechsel wirkt sofort.
///
/// Warum parseServerDate: Der Server speichert und liefert Zeitpunkte als UTC,
/// aber OHNE Zeitzonen-Kennung ("2025-01-01T10:00:00", System.Text.Json
/// serialisiert DateTimeKind.Unspecified ohne "Z"). `new Date(...)` deutet so
/// einen String als LOKALE Zeit — die Anzeige wäre in Deutschland 1-2 Stunden
/// zu früh. Ein ISO-String mit Uhrzeit, aber ohne Zone, wird deshalb als UTC
/// gelesen; Strings mit "Z" oder "+02:00" bleiben unverändert.
/// </summary>

// Mit Endung .ts und aus ./locale: die .NET-Tests laden diese Datei direkt unter Node (ohne Bundler, ohne i18next).
import { intlLocale } from '../i18n/locale.ts'

/** Platzhalter für fehlende oder nicht lesbare Werte. */
export const NO_VALUE = '—'

const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/
const HAS_TIME = /^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}/
// Zone am Ende der UHRZEIT: "Z", "+02:00", "+0200" oder "+02". Geprüft wird nur der
// Teil nach dem Datum, damit die "-" im Datum nicht als Zone gelten.
const HAS_ZONE = /(?:[zZ]|[+-]\d{2}(?::?\d{2})?)$/

export interface FormatOptions {
  /** IANA-Zeitzone für die Ausgabe (Standard: Zeitzone des Browsers). Vor allem für Tests. */
  timeZone?: string
  /** Text für fehlende/ungültige Werte (Standard: "—"). */
  fallback?: string
}

/**
 * Liest einen Zeitpunkt aus der API. ISO mit Uhrzeit, aber ohne Zeitzone, gilt
 * als UTC; ein reines Datum ("2025-01-01") als lokaler Kalendertag (Mitternacht
 * der Browser-Zeitzone), damit der Tag beim Formatieren nicht wandert.
 * null, leer oder ungültig → null.
 */
export function parseServerDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value
  if (typeof value === 'number') {
    const d = new Date(value)
    return Number.isNaN(d.getTime()) ? null : d
  }

  const text = value.trim()
  if (text === '') return null

  const dateOnly = DATE_ONLY.exec(text)
  if (dateOnly) {
    const d = new Date(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3]))
    return Number.isNaN(d.getTime()) ? null : d
  }

  let iso = text
  if (HAS_TIME.test(iso)) {
    iso = iso.replace(' ', 'T')
    // .NET liefert bis zu 7 Nachkommastellen; ECMAScript kennt nur Millisekunden.
    iso = iso.replace(/(\.\d{3})\d+/, '$1')
    if (!HAS_ZONE.test(iso.slice(10))) iso += 'Z'
  }
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? null : d
}

/** "01.01.2025, 11:00" (en: "01/01/2025, 11:00") — Datum und Uhrzeit in der Ortszeit. */
export function formatDateTime(value: string | number | Date | null | undefined, opts: FormatOptions = {}): string {
  const d = parseServerDate(value)
  if (!d) return opts.fallback ?? NO_VALUE
  return new Intl.DateTimeFormat(intlLocale(), {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: opts.timeZone,
  }).format(d)
}

/** "01.01.2025" (en: "01/01/2025") — Datum eines ZEITPUNKTS in der Ortszeit. */
export function formatDate(value: string | number | Date | null | undefined, opts: FormatOptions = {}): string {
  const d = parseServerDate(value)
  if (!d) return opts.fallback ?? NO_VALUE
  return new Intl.DateTimeFormat(intlLocale(), {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    timeZone: opts.timeZone,
  }).format(d)
}

/**
 * "25.05.2026" (en: "25/05/2026") — für reine KALENDERTAGE (Haltbarkeit, erwartetes Lieferdatum,
 * Saison-Grenzen). Der Server liefert sie als "2026-05-25" oder als Mitternacht
 * ohne Zone ("2026-05-25T00:00:00"); ein Kalendertag ist kein Zeitpunkt und
 * darf durch keine Zeitzonen-Umrechnung auf den Vor- oder Folgetag rutschen.
 */
export function formatDateOnly(value: string | null | undefined, fallback: string = NO_VALUE): string {
  if (!value) return fallback
  const m = /^(\d{4})-(\d{2})-(\d{2})(?:$|[T ])/.exec(value.trim())
  if (!m) return fallback
  // Ein Kalendertag ist kein Zeitpunkt: als UTC-Mitternacht gebildet und in UTC ausgegeben, damit er in keiner Zeitzone wandert.
  const day = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])))
  if (Number.isNaN(day.getTime())) return fallback
  return new Intl.DateTimeFormat(intlLocale(), { day: '2-digit', month: '2-digit', year: 'numeric', timeZone: 'UTC' }).format(day)
}

/**
 * Wert eines `<input type="datetime-local">` ("2026-05-25T18:00", Ortszeit ohne Zone) als UTC-Zeitpunkt
 * ("2026-05-25T16:00:00.000Z") für den Server. Der Server legt Zeitpunkte als UTC ab und `parseServerDate` liest
 * sie so: ein unverändert gesendeter Ortszeit-String würde beim Anzeigen um den UTC-Versatz (1-2 h) verschoben.
 * Leer oder ungültig → null.
 */
export function localDateTimeToUtcIso(value: string | null | undefined): string | null {
  if (!value) return null
  const d = new Date(value)
  return Number.isNaN(d.getTime()) ? null : d.toISOString()
}

/** "1.234,5" (en: "1,234.5") — Zahl in der aktiven Sprache; `options` wie bei Intl.NumberFormat. */
export function formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
  return new Intl.NumberFormat(intlLocale(), options).format(value)
}

/**
 * "12,34 €" (en: "€12.34") — Betrag aus Cent mit der Währung des Datensatzes (Standard EUR).
 * Eine unbekannte Währungskennung fällt auf "12,34 XYZ" zurück, statt zu werfen.
 */
export function formatMoney(cents: number, currency: string = 'EUR'): string {
  const amount = cents / 100
  try {
    return new Intl.NumberFormat(intlLocale(), { style: 'currency', currency }).format(amount)
  } catch {
    return `${new Intl.NumberFormat(intlLocale(), { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount)} ${currency}`
  }
}
