import { useTranslation } from 'react-i18next'
import { StatusPill } from '../../components/StatusPill'
import { describeDays, daysUntilExpiry, EXPIRY_TONES, expiryStatusOf, type ExpiryStatus } from './expiry'

interface Props {
  /** MHD als Datum ("2026-12-31" oder mit Uhrzeit); ohne MHD rendert die Komponente nichts. */
  expiryDate: string | null | undefined
  /** Status vom Server (MHD-Warnliste); ohne Angabe wird er aus dem Datum berechnet. */
  status?: ExpiryStatus
  /** Tage bis zum Ablauf vom Server; ohne Angabe aus dem Datum berechnet. */
  days?: number
  /** Bezugszeitpunkt der Berechnung (Tests). */
  now?: Date
  small?: boolean
}

/// <summary>
/// Farbige Status-Anzeige für das MHD einer Charge (abgelaufen / kritisch / bald / OK). Der Tooltip nennt die Tage
/// ("noch 5 Tage", "seit 2 Tagen abgelaufen"); der Status steht zusätzlich als data-status am Element.
/// </summary>
export function ExpiryPill({ expiryDate, status, days, now, small = true }: Props) {
  const { t } = useTranslation()
  const resolvedDays = days ?? daysUntilExpiry(expiryDate, now)
  if (resolvedDays === null) return null
  const resolved = status ?? expiryStatusOf(resolvedDays)
  return (
    <span title={describeDays(resolvedDays)}>
      <StatusPill status={resolved} tone={EXPIRY_TONES[resolved]} label={t(`status:expiry.${resolved}`)} small={small} />
    </span>
  )
}
