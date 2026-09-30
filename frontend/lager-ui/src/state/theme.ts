import { useSyncExternalStore } from 'react'
import { create } from 'zustand'
import { persist } from 'zustand/middleware'

export type Theme = 'light' | 'dark' | 'auto'
/** Das tatsächlich angewendete Farbschema ("auto" ist aufgelöst). */
export type EffectiveTheme = 'light' | 'dark'

interface ThemeState {
  theme: Theme
  setTheme: (t: Theme) => void
}

/// <summary>
/// Persistierte Theme-Präferenz. "auto" folgt prefers-color-scheme.
/// Das effektive Schema landet als data-theme UND als color-scheme auf <html>
/// (applyTheme) — so folgen auch native Controls, Scrollbars und Date-Picker
/// dem gewählten Theme, nicht dem des Betriebssystems.
/// </summary>
export const useTheme = create<ThemeState>()(
  persist(
    (set) => ({
      theme: 'auto',
      setTheme: (t) => {
        set({ theme: t })
        applyTheme(t)
      },
    }),
    { name: 'lager.theme' }
  )
)

const DARK_QUERY = '(prefers-color-scheme: dark)'

/** Bevorzugt das Betriebssystem Dunkel? (Umgebungen ohne matchMedia, z. B. Tests, gelten als hell.) */
function systemPrefersDark(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia(DARK_QUERY).matches
}

/** Löst "auto" anhand von prefers-color-scheme auf. */
export function resolveTheme(theme: Theme): EffectiveTheme {
  if (theme === 'auto') return systemPrefersDark() ? 'dark' : 'light'
  return theme
}

// Das aufgelöste Schema für Komponenten, die es in JS brauchen (Canvas-Farben) — siehe useEffectiveTheme.
let currentEffective: EffectiveTheme = 'light'
const effectiveListeners = new Set<() => void>()

export function applyTheme(theme: Theme) {
  const root = document.documentElement
  const effective = resolveTheme(theme)
  root.dataset.theme = effective
  // color-scheme folgt dem Theme (nicht statisch "light dark"): native Controls/Scrollbars passen zur Oberfläche.
  root.style.colorScheme = effective
  const changed = effective !== currentEffective
  currentEffective = effective
  if (changed) effectiveListeners.forEach((listener) => listener())
}

/** Das aktuell angewendete Schema als Hook (reagiert auf Theme-Wechsel und auf das Betriebssystem im Auto-Modus). */
export function useEffectiveTheme(): EffectiveTheme {
  return useSyncExternalStore(
    (listener) => {
      effectiveListeners.add(listener)
      return () => { effectiveListeners.delete(listener) }
    },
    () => currentEffective,
    () => 'light' as EffectiveTheme,
  )
}

let mediaListenerCleanup: (() => void) | null = null

/// <summary>
/// Initialize on load — read store + apply ASAP to avoid flash. Im Auto-Modus
/// folgt die Oberfläche Änderungen des Betriebssystem-Themes live. Gibt eine
/// Aufräumfunktion zurück; ein erneuter Aufruf ersetzt den alten Listener.
/// </summary>
export function initTheme(): () => void {
  applyTheme(useTheme.getState().theme)
  mediaListenerCleanup?.()
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return () => {}
  const media = window.matchMedia(DARK_QUERY)
  const onChange = () => {
    if (useTheme.getState().theme === 'auto') applyTheme('auto')
  }
  media.addEventListener('change', onChange)
  mediaListenerCleanup = () => {
    media.removeEventListener('change', onChange)
    mediaListenerCleanup = null
  }
  return mediaListenerCleanup
}
