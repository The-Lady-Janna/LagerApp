import { onlineManager } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ErrorBanner } from './ErrorBanner'

interface Props {
  isLoading: boolean
  /** Fehler der Query (null/undefined = keiner). */
  error: unknown
  /** Sind schon Daten da (z. B. aus dem Cache), wird ein fehlgeschlagener Hintergrund-Refresh nicht als Vollbild-Fehler gezeigt. */
  hasData?: boolean
  /** Was geladen wird, für die Meldung ("die Retouren"; übersetzt, z. B. t('returns:what')). Ohne Angabe: "die Daten". */
  what?: string
  onRetry?: () => void
  /** Wird gezeigt, wenn weder geladen wird noch ein Fehler vorliegt. */
  children?: ReactNode
}

/// <summary>
/// Einheitlicher Lade-/Fehlerzustand für Seiten mit Query: solange geladen wird "Lade …" (role="status"), bei einem
/// Fehler ein ErrorBanner mit "Erneut versuchen". So erscheint weder eine leere Tabelle noch ein irreführendes
/// "Keine Daten", während die Daten noch fehlen oder gar nicht ankommen.
/// Verwendung: `if (!data) return <LoadState isLoading={isLoading} error={error} … />` (Seite erst rendern, wenn Daten da
/// sind) bzw. mit `hasData` und Kindern als Wrapper um einen Teilbereich.
/// </summary>
export function LoadState({ isLoading, error, hasData = false, what, onRetry, children }: Props) {
  const { t } = useTranslation()
  const subject = what ?? t('errors:load.whatDefault')
  if (isLoading && !hasData) return <p className="muted" role="status">{t('errors:load.loading', { what: subject })}</p>
  if (error && !hasData) {
    return <ErrorBanner error={error} title={t('errors:load.failed', { what: subject })} onRetry={onRetry} />
  }
  if (!hasData && !isLoading && children === undefined) {
    // Weder Daten noch Fehler noch laufender Abruf: ohne Netz pausiert React Query die Abfrage. Die Seite darf dann nicht
    // leer bleiben — der Zustand wird benannt, und mit dem Netz läuft der Abruf von selbst weiter.
    return (
      <p className="muted" role="status">
        {onlineManager.isOnline() ? t('errors:load.loading', { what: subject }) : t('errors:load.offline')}
      </p>
    )
  }
  return <>{children}</>
}
