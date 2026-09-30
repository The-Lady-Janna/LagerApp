import { useEffect, type CSSProperties } from 'react'
import { useTranslation } from 'react-i18next'
import { parseApiError } from '../api/errors'
import { dismissToastsForError } from '../state/toasts'

interface Props {
  /** Der Fehler (AxiosError, Error, String …). Ohne Fehler (null/undefined/false) rendert die Komponente nichts. */
  error: unknown
  /** Kurzer Vorspann, z. B. "Speichern fehlgeschlagen:". */
  title?: string
  /** Fallback-Text, wenn dem Fehler selbst keine Meldung zu entnehmen ist. */
  fallback?: string
  /** Zeigt "Erneut versuchen" (common:retry; z. B. refetch einer fehlgeschlagenen Query). */
  onRetry?: () => void
  /** Zeigt ein "×" zum Ausblenden (z. B. mutation.reset). */
  onDismiss?: () => void
  style?: CSSProperties
}

/// <summary>
/// Einheitliche Fehleranzeige (role="alert"): die verständliche Meldung aus api/errors.ts statt eines rohen String-Casts, bei
/// Server-Fehlern mit "Referenz: <correlationId>" für den Support. Zeigt die Seite den Fehler selbst an, wird der
/// globale Toast zum selben Fehler zurückgenommen (kein Doppel-Hinweis).
/// </summary>
export function ErrorBanner({ error, title, fallback, onRetry, onDismiss, style }: Props) {
  const { t } = useTranslation()
  const hasError = error !== null && error !== undefined && error !== false
  useEffect(() => {
    if (hasError) dismissToastsForError(error)
  }, [error, hasError])

  if (!hasError) return null
  const info = parseApiError(error, fallback)

  return (
    <div className="error error-banner" role="alert" style={style}>
      <div className="error-banner-text">
        {title && <strong>{title} </strong>}
        <span>{info.message}</span>
        {info.correlationId && <div className="error-banner-reference">{t('errors:reference', { id: info.correlationId })}</div>}
      </div>
      {(onRetry || onDismiss) && (
        <div className="error-banner-actions">
          {onRetry && <button type="button" onClick={onRetry}>{t('common:retry')}</button>}
          {onDismiss && <button type="button" onClick={onDismiss} aria-label={t('errors:dismissAria')}>×</button>}
        </div>
      )}
    </div>
  )
}
