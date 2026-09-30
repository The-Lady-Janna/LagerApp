import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useToasts, type ToastItem } from '../state/toasts'

/** Anzeigedauer in ms. Fehler bleiben länger stehen: sie sollen gelesen werden, bevor sie verschwinden. */
const DURATION_MS: Record<ToastItem['kind'], number> = { error: 12_000, success: 4_000, info: 6_000 }

/// <summary>
/// Zeigt die globalen Meldungen aus state/toasts.ts unten rechts an. Der Container ist eine aria-live-Region
/// (Screenreader lesen neue Meldungen vor); Fehler tragen zusätzlich role="alert". Mit der Maus über einem Toast
/// (oder per Tastaturfokus) läuft der Ablauf-Timer nicht weiter. Einmal in main.tsx gemountet, damit die Toasts auch
/// außerhalb der App-Shell (Mobile-Picker, Login) erscheinen.
/// </summary>
export function ToastHost() {
  const toasts = useToasts((s) => s.toasts)
  return (
    <div className="toast-region" aria-live="polite" aria-relevant="additions text">
      {toasts.map((toast) => <ToastView key={toast.id} toast={toast} />)}
    </div>
  )
}

function ToastView({ toast }: { toast: ToastItem }) {
  const { t } = useTranslation()
  const dismiss = useToasts((s) => s.dismiss)
  const [paused, setPaused] = useState(false)

  // stamp: eine Wiederholung derselben Meldung startet den Timer neu.
  useEffect(() => {
    if (paused) return
    const timer = setTimeout(() => dismiss(toast.id), DURATION_MS[toast.kind])
    return () => clearTimeout(timer)
  }, [dismiss, paused, toast.id, toast.kind, toast.stamp])

  return (
    <div
      className={`toast toast-${toast.kind}`}
      role={toast.kind === 'error' ? 'alert' : 'status'}
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
    >
      <div className="toast-body">
        {toast.title && <strong className="toast-title">{toast.title}</strong>}
        <span className="toast-message">{toast.message}</span>
        {toast.count > 1 && <span className="toast-count"> (×{toast.count})</span>}
        {toast.reference && <div className="toast-reference">{t('errors:reference', { id: toast.reference })}</div>}
      </div>
      <button type="button" className="toast-close" onClick={() => dismiss(toast.id)} aria-label={t('common:closeMessage')}>×</button>
    </div>
  )
}
