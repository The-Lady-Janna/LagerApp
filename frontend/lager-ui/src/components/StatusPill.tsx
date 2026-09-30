import { useTranslation } from 'react-i18next'
import { toneFor, type PillTone, type StatusDomain } from '../lib/statusTones'

interface Props {
  /** Status des Servers, z. B. "Received". Wird ohne `label` aus status:pill.<domain>.<status> angezeigt (unbekannt: unverändert). */
  status: string
  /** Bereich, aus dem der Status stammt — bestimmt die Farbe (lib/statusTones.ts). */
  domain?: StatusDomain
  /** Ton direkt vorgeben (ersetzt die Zuordnung über domain/status). */
  tone?: PillTone
  /** Anzeigetext, falls er vom Status abweichen soll (bereits übersetzt). */
  label?: string
  /** Kleinere Schrift für enge Zellen (QC-Ergebnis in Zeilen). */
  small?: boolean
}

/// <summary>
/// Einheitliche Status-Anzeige: farbige Pill, deren Farbe zentral aus dem Status abgeleitet wird
/// (lib/statusTones.ts) und aus Design-Tokens besteht — lesbar in Hell und Dunkel. Der Status steht
/// zusätzlich als data-status/data-tone am Element (Tests, Styling).
/// </summary>
export function StatusPill({ status, domain, tone, label, small = false }: Props) {
  const { t } = useTranslation()
  const resolved: PillTone = tone ?? (domain ? toneFor(domain, status) : 'neutral')
  return (
    <span className={`pill pill--${resolved}`} data-status={status} data-tone={resolved} style={small ? { fontSize: 11 } : undefined}>
      {label ?? (domain ? t(`status:pill.${domain}.${status}`, { defaultValue: status }) : status)}
    </span>
  )
}
