import { t } from '../i18n'
import { formatDateOnly } from './format'

/// <summary>
/// Texte der Kennzahlen-Kacheln der Reports. Der Server liefert Label und Hinweis als deutschen Klartext
/// (ReportService.BuildDashboard); Backend-Meldungen werden nicht übersetzt, die bekannten Kennzahlen schon: eine Tabelle
/// im Namespace reports (kpi.*) deckt die vier Standardkacheln ab. Unbekannte Texte erscheinen unverändert (Deutsch).
/// </summary>

const KNOWN_LABELS: Readonly<Record<string, string>> = {
  'Bestellungen (Zeitraum)': 'orders',
  'Picklisten (Zeitraum)': 'pickLists',
  'Picks pro Stunde': 'picksPerHour',
  'Ø Pickdistanz': 'avgDistance',
}

const KNOWN_HINTS: Readonly<Record<string, string>> = {
  'gepickte Positionen / Zeitspanne erster bis letzter Pick': 'picksPerHour',
  'über abgeschlossene Picklisten': 'completedPickLists',
}

/** Beschriftung einer Kennzahl in der Sprache der Oberfläche. */
export function kpiLabel(label: string): string {
  const key = Object.hasOwn(KNOWN_LABELS, label) ? KNOWN_LABELS[label] : undefined
  return key ? t(`reports:kpi.label.${key}`) : label
}

/** Hinweistext einer Kennzahl ("seit 01.01.2026", "3 fertig", …) in der Sprache der Oberfläche. */
export function kpiHint(hint: string): string {
  const fixed = Object.hasOwn(KNOWN_HINTS, hint) ? KNOWN_HINTS[hint] : undefined
  if (fixed) return t(`reports:kpi.hint.${fixed}`)
  const since = /^seit (\d{2})\.(\d{2})\.(\d{4})$/.exec(hint)
  if (since) return t('reports:kpi.hint.since', { date: formatDateOnly(`${since[3]}-${since[2]}-${since[1]}`, hint) })
  const done = /^(\d+) fertig$/.exec(hint)
  if (done) return t('reports:kpi.hint.done', { count: Number(done[1]) })
  return hint
}
