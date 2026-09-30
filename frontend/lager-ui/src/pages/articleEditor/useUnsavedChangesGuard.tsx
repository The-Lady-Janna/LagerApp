import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { ConfirmDialog } from '../../components/ConfirmDialog'

/// <summary>
/// Warnung vor dem Verlassen mit ungespeicherten Änderungen. Die App nutzt den deklarativen BrowserRouter (kein useBlocker),
/// deshalb greift der Schutz an drei Stellen:
///  - Tab schließen/Seite neu laden: der Browser fragt selbst (beforeunload).
///  - Klick auf einen internen Link (Seitenleiste, Breadcrumbs ...): wird abgefangen, ein Bestätigungsdialog fragt nach.
///  - Programmatisches Verlassen über <c>requestLeave</c> (z. B. "Abbrechen").
/// Der Zurück-Button des Browsers lässt sich mit diesem Router nicht abfangen.
/// Ist nichts geändert (<c>dirty</c> = false), passiert nichts Besonderes: <c>requestLeave</c> navigiert sofort.
/// </summary>
export function useUnsavedChangesGuard(dirty: boolean): { requestLeave: (path: string) => void; leaveDialog: ReactNode } {
  const navigate = useNavigate()
  const { t } = useTranslation()
  const [pendingPath, setPendingPath] = useState<string | null>(null)

  useEffect(() => {
    if (!dirty) return
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = '' // ältere Browser verlangen einen gesetzten Wert
    }
    window.addEventListener('beforeunload', onBeforeUnload)
    return () => window.removeEventListener('beforeunload', onBeforeUnload)
  }, [dirty])

  useEffect(() => {
    if (!dirty) return
    // Capture-Phase am Dokument: läuft vor dem Klick-Handler des React-Router-Links und kann ihn stoppen.
    const onClick = (event: MouseEvent) => {
      if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
      const anchor = event.target instanceof Element ? event.target.closest('a[href]') : null
      if (!(anchor instanceof HTMLAnchorElement) || anchor.target === '_blank' || anchor.hasAttribute('download')) return
      const url = new URL(anchor.href, window.location.href)
      if (url.origin !== window.location.origin) return
      event.preventDefault()
      event.stopPropagation()
      setPendingPath(`${url.pathname}${url.search}${url.hash}`)
    }
    document.addEventListener('click', onClick, true)
    return () => document.removeEventListener('click', onClick, true)
  }, [dirty])

  const requestLeave = useCallback(
    (path: string) => {
      if (dirty) setPendingPath(path)
      else navigate(path)
    },
    [dirty, navigate],
  )

  const leaveDialog = (
    <ConfirmDialog
      open={pendingPath !== null}
      title={t('articles:leave.title')}
      confirmLabel={t('articles:leave.confirm')}
      cancelLabel={t('articles:leave.cancel')}
      danger
      onConfirm={() => {
        const path = pendingPath
        setPendingPath(null)
        if (path) navigate(path)
      }}
      onCancel={() => setPendingPath(null)}
    >
      <p>{t('articles:leave.body')}</p>
    </ConfirmDialog>
  )

  return { requestLeave, leaveDialog }
}
