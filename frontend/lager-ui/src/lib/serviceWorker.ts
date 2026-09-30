import { useAppUpdate } from './appUpdate'

// Ein lang geöffneter Tab prüft stündlich, ob ein neuer sw.js vorliegt.
const UPDATE_CHECK_INTERVAL_MS = 60 * 60 * 1000

/// <summary>
/// Registriert den Service-Worker (public/sw.js) — nur im Production-Build.
/// Im Dev-Betrieb werden bereits installierte Worker samt Caches entfernt,
/// sonst gäbe es Cache-Probleme bei HMR.
///
/// Sobald ein NEUER Worker die Kontrolle übernimmt (sw.js hat skipWaiting +
/// clients.claim), erscheint das Update-Banner. Beim allerersten Besuch gibt es
/// keinen Vorgänger, dort wird kein Hinweis gezeigt.
/// </summary>
export function registerServiceWorker(): void {
  if (!('serviceWorker' in navigator)) return

  if (!import.meta.env.PROD) {
    void navigator.serviceWorker.getRegistrations().then((regs) => regs.forEach((r) => void r.unregister()))
    if ('caches' in window) void caches.keys().then((keys) => keys.forEach((k) => void caches.delete(k)))
    return
  }

  const hadController = navigator.serviceWorker.controller !== null
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    if (hadController) useAppUpdate.getState().setUpdateAvailable(true)
  })

  window.addEventListener('load', () => {
    navigator.serviceWorker
      .register('/sw.js')
      .then((registration) => {
        setInterval(() => void registration.update().catch(() => {}), UPDATE_CHECK_INTERVAL_MS)
      })
      .catch((e) => console.warn('Service-Worker-Registrierung fehlgeschlagen:', e))
  })
}
