import { t } from '../i18n'
import { useAuth } from '../state/auth'
import { getSessionExpiryMs } from './jwt'

/** Hinweis auf dem Login-Screen nach einem automatischen Logout, in der aktuellen Sprache (wird bei jedem Aufruf neu übersetzt). */
export function sessionExpiredNotice(): string {
  return t('auth:sessionExpired')
}

// setTimeout kann höchstens 2^31-1 ms (~24,8 Tage) warten; längere Zeiträume
// werden in Etappen abgewartet.
const MAX_TIMEOUT_MS = 2_147_483_647

/// <summary>
/// Überwacht den Ablauf der Session und loggt automatisch aus, sobald das Token
/// abläuft. Ein abgelaufenes, aus dem localStorage rehydriertes Token wird
/// beim Start sofort verworfen (der Aufruf in main.tsx passiert vor dem ersten
/// Render). Rückgabe: Funktion zum Beenden der Überwachung.
/// </summary>
export function startSessionWatcher(): () => void {
  let timer: ReturnType<typeof setTimeout> | undefined

  const arm = () => {
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
    const { token, expiresAt } = useAuth.getState()
    if (!token) return
    const expiry = getSessionExpiryMs(token, expiresAt)
    if (expiry === null) return // Ablauf unbekannt → der Server entscheidet per 401
    const remaining = expiry - Date.now()
    if (remaining <= 0) {
      useAuth.getState().logout(sessionExpiredNotice())
      return
    }
    timer = setTimeout(arm, Math.min(remaining, MAX_TIMEOUT_MS))
  }

  arm()

  // Bei neuem/entferntem Token bzw. geänderter Ablaufzeit den Timer neu setzen.
  const unsubscribe = useAuth.subscribe((state, prev) => {
    if (state.token !== prev.token || state.expiresAt !== prev.expiresAt) arm()
  })

  // Timer laufen im Hintergrund-Tab/Standby unzuverlässig → beim Zurückkehren neu prüfen.
  const onVisible = () => {
    if (document.visibilityState === 'visible') arm()
  }
  document.addEventListener('visibilitychange', onVisible)

  return () => {
    if (timer !== undefined) clearTimeout(timer)
    unsubscribe()
    document.removeEventListener('visibilitychange', onVisible)
  }
}
