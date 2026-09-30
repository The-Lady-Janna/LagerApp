import { RESTORE_CONFIRMATION, type BackupSettingsDto } from '../../api/systemHooks'
import { intlLocale, t } from '../../i18n'

/// <summary>
/// Kleine, reine Hilfen der System-Seite (Texte und Formatierung), getrennt von der Seite, damit sie sich ohne Rendern testen lassen.
/// </summary>

const SIZE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const

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

/** true, wenn die Eingabe genau dem Bestätigungswort entspricht (Groß-/Kleinschreibung zählt; Leerraum außen wird ignoriert). */
export function isRestoreConfirmed(input: string): boolean {
  return input.trim() === RESTORE_CONFIRMATION
}

/** Der Zeitplan als Text: "täglich 02:00 UTC", "aus" oder "ungültig". */
export function describeSchedule(settings: Pick<BackupSettingsDto, 'schedule' | 'scheduleValid' | 'scheduleTimeZone'>): string {
  if (!settings.schedule) return t('system:schedule.off')
  if (!settings.scheduleValid) return t('system:schedule.invalid')
  return t('system:schedule.daily', { schedule: settings.schedule, timeZone: settings.scheduleTimeZone })
}

/** Die Aufbewahrung als Text: "die letzten 14 Backups" bzw. "unbegrenzt" (Zahl unter 1 = nie automatisch löschen). */
export function describeRetention(retentionCount: number): string {
  return retentionCount >= 1 ? t('system:retention.last', { count: retentionCount }) : t('system:retention.unlimited')
}
