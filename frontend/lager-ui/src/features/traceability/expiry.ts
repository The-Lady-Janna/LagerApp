import { t } from '../../i18n'
import type { PillTone } from '../../lib/statusTones'

/// <summary>
/// MHD-Status einer Charge in der Oberfläche. Spiegelt die Regeln des Servers (ReportService.ExpiryCriticalDays,
/// Kalendertage in UTC): abgelaufen = MHD liegt vor dem heutigen Tag (am Ablauftag ist die Ware noch verwendbar),
/// kritisch = höchstens 7 Tage bis zum Ablauf (auch heute), bald = bis 30 Tage (Standardfrist der MHD-Warnliste), sonst OK.
/// </summary>
export type ExpiryStatus = 'Expired' | 'Critical' | 'Soon' | 'Ok'

/** Ab so vielen Tagen bis zum Ablauf (einschließlich) gilt eine Charge als kritisch — wie ReportService.ExpiryCriticalDays. */
export const EXPIRY_CRITICAL_DAYS = 7

/** Bis so viele Tage gilt eine Charge in der Bestandsansicht als "bald" ablaufend (Standardfrist der Warnliste). */
export const EXPIRY_SOON_DAYS = 30

export const EXPIRY_TONES: Readonly<Record<ExpiryStatus, PillTone>> = {
  Expired: 'danger',
  Critical: 'warning',
  Soon: 'info',
  Ok: 'success',
}

const MS_PER_DAY = 86_400_000
const DATE_PART = /^(\d{4})-(\d{2})-(\d{2})/

/**
 * Kalendertage von HEUTE (UTC) bis zum MHD; negativ = abgelaufen, 0 = läuft heute ab. Das MHD ist ein Kalendertag:
 * nur der Datumsteil ("2026-12-31", auch "2026-12-31T00:00:00Z") zählt, keine Zeitzonen-Umrechnung. Kein/ungültiges MHD → null.
 */
export function daysUntilExpiry(expiryDate: string | null | undefined, now: Date = new Date()): number | null {
  const m = expiryDate ? DATE_PART.exec(expiryDate.trim()) : null
  if (!m) return null
  const expiry = Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]))
  const today = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate())
  if (Number.isNaN(expiry)) return null
  return Math.round((expiry - today) / MS_PER_DAY)
}

/** Status zu den Tagen bis zum Ablauf (siehe <see cref="ExpiryStatus"/>). */
export function expiryStatusOf(days: number): ExpiryStatus {
  if (days < 0) return 'Expired'
  if (days <= EXPIRY_CRITICAL_DAYS) return 'Critical'
  if (days <= EXPIRY_SOON_DAYS) return 'Soon'
  return 'Ok'
}

/** Kurztext zu den Tagen: "seit 3 Tagen abgelaufen", "läuft heute ab", "noch 5 Tage" (Sprache der Oberfläche). */
export function describeDays(days: number): string {
  if (days < -1) return t('traceability:days.expiredDays', { count: -days })
  if (days === -1) return t('traceability:days.expiredYesterday')
  if (days === 0) return t('traceability:days.today')
  if (days === 1) return t('traceability:days.tomorrow')
  return t('traceability:days.inDays', { count: days })
}

/** Heutiges Datum als "yyyy-MM-dd" in der Ortszeit des Nutzers (Vorbelegung/Untergrenze von Datumsfeldern, Vergleich als Text). */
export function todayIso(now: Date = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')
  return `${now.getFullYear()}-${month}-${day}`
}

/** Sortierschlüssel FEFO: frühestes MHD zuerst, ohne MHD zuletzt. */
export function compareExpiry(a: string | null | undefined, b: string | null | undefined): number {
  const da = a ? a.slice(0, 10) : null
  const db = b ? b.slice(0, 10) : null
  if (da === db) return 0
  if (da === null) return 1
  if (db === null) return -1
  return da < db ? -1 : 1
}

/** Pfad der Rückverfolgungsseite für eine Chargennummer (Link aus Bestand und MHD-Karte). */
export function traceabilityPath(lotNumber: string): string {
  return `/traceability?lot=${encodeURIComponent(lotNumber)}`
}
