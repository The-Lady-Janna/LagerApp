import { formatDateOnly } from '../../lib/format'
import { t } from '../../i18n'

/// <summary>
/// Saison-Fenster eines Artikels (ValidFrom/ValidUntil). Beide Grenzen sind KALENDERTAGE: das Ende ist der letzte
/// Gültigkeitstag und gilt noch vollständig (wie Article.IsCurrentlyActive auf dem Server, UTC-Kalendertag).
/// Der Server liefert Mitternacht ohne Zeitzone ("2026-09-30T00:00:00") oder mit "Z"; der Editor zeigt und speichert Tage.
/// </summary>

const DATE_PREFIX = /^(\d{4})-(\d{2})-(\d{2})/
const HAS_ZONE = /(?:[zZ]|[+-]\d{2}(?::?\d{2})?)$/

/** Der Tag eines Server-Werts als "YYYY-MM-DD" für &lt;input type="date"&gt;; ohne lesbaren Tag ''. */
export function dateInputValue(value: string | null | undefined): string {
  const match = value ? DATE_PREFIX.exec(value.trim()) : null
  return match ? `${match[1]}-${match[2]}-${match[3]}` : ''
}

/** Der Wert eines Datumsfelds ("YYYY-MM-DD") als Server-Wert (Mitternacht, wie der Server ihn liefert); leer → null. */
export function seasonValueFromInput(input: string): string | null {
  return DATE_PREFIX.test(input) ? `${input.slice(0, 10)}T00:00:00` : null
}

/** Meldung, wenn das Ende vor dem Beginn liegt (am selben Tag ist ein Ein-Tages-Fenster), sonst null. */
export function seasonWindowError(validFrom: string | null | undefined, validUntil: string | null | undefined): string | null {
  const from = dateInputValue(validFrom)
  const until = dateInputValue(validUntil)
  if (from !== '' && until !== '' && until < from) return t('articles:season.endBeforeStart')
  return null
}

/** Zeitpunkt eines Server-Werts als UTC (ein Wert ohne Zonenangabe gilt als UTC, wie auf dem Server); nicht lesbar → null. */
function parseUtc(value: string | null | undefined): Date | null {
  if (!value) return null
  let text = value.trim().replace(' ', 'T')
  if (DATE_PREFIX.test(text) && !text.includes('T')) text += 'T00:00:00'
  text = text.replace(/(\.\d{3})\d+/, '$1') // .NET liefert bis zu 7 Nachkommastellen, ECMAScript kennt Millisekunden
  if (!HAS_ZONE.test(text.slice(10))) text += 'Z'
  const date = new Date(text)
  return Number.isNaN(date.getTime()) ? null : date
}

/**
 * Ist der Artikel zum Zeitpunkt <paramref name="now"/> im Saison-Fenster? Der Beginn gilt ab dem angegebenen Zeitpunkt (bei einem
 * Datum ab 00:00 UTC), das Ende als Kalendertag inklusive: bis 23:59 UTC des letzten Tages bestellbar, erst am Folgetag nicht mehr.
 * Ohne Grenzen immer wahr. (Der Server rechnet dasselbe und liefert es als <c>isCurrentlyActive</c>.)
 */
export function isInSeason(validFrom: string | null | undefined, validUntil: string | null | undefined, now: Date = new Date()): boolean {
  const from = parseUtc(validFrom)
  if (from && now.getTime() < from.getTime()) return false
  const until = parseUtc(validUntil)
  if (until && now.toISOString().slice(0, 10) > until.toISOString().slice(0, 10)) return false
  return true
}

/** Hat der Artikel überhaupt ein Saison-Fenster? */
export function hasSeasonWindow(validFrom: string | null | undefined, validUntil: string | null | undefined): boolean {
  return dateInputValue(validFrom) !== '' || dateInputValue(validUntil) !== ''
}

/** Text zum Saison-Fenster: "01.03.2026 bis 30.09.2026", "ab 01.03.2026", "bis 30.09.2026" (das Ende gilt inklusive). */
export function seasonWindowText(validFrom: string | null, validUntil: string | null): string {
  const from = validFrom ? formatDateOnly(validFrom, '') : ''
  const until = validUntil ? formatDateOnly(validUntil, '') : ''
  if (from && until) return t('articles:season.range', { from, until })
  if (from) return t('articles:season.from', { from })
  if (until) return t('articles:season.until', { until })
  return t('articles:season.allYear')
}
