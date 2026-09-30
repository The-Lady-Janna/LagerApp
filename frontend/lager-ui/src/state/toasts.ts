import { create } from 'zustand'
import { parseApiError } from '../api/errors'
import { t } from '../i18n'

export type ToastKind = 'error' | 'success' | 'info'

export interface ToastItem {
  id: number
  kind: ToastKind
  /** Kurzer Titel, z. B. "Aktion fehlgeschlagen". */
  title?: string
  message: string
  /** Referenz zum Server-Log (correlationId), nur bei Fehlern. */
  reference?: string
  /** Der Fehler, aus dem der Toast entstand — damit eine Seite, die ihn selbst anzeigt, den Toast zurücknehmen kann. */
  source?: unknown
  /** Wie oft dieselbe Meldung eingetroffen ist (identische Meldungen werden zusammengefasst). */
  count: number
  /** Änderungsstempel: bei jeder Wiederholung neu, startet den Ablauf-Timer erneut. */
  stamp: number
}

interface ToastState {
  toasts: ToastItem[]
  push: (toast: Omit<ToastItem, 'id' | 'count' | 'stamp'>) => number
  dismiss: (id: number) => void
  dismissForSource: (source: unknown) => void
  clear: () => void
}

/** Mehr gleichzeitige Toasts überfordern den Nutzer; die ältesten fallen heraus. */
const MAX_TOASTS = 4

let nextId = 1
let nextStamp = 1

/// <summary>
/// Globale Meldungen (Fehler, Erfolg, Hinweis). Der Store ist unabhängig von React, damit auch der QueryClient
/// (lib/queryClient.ts) Fehler melden kann; angezeigt werden sie von components/Toast.tsx.
/// Identische Meldungen (gleiche Art + Text) werden nicht gestapelt, sondern gezählt — sonst würde ein
/// ausgefallener Server mit jedem fehlschlagenden Request einen weiteren Toast erzeugen.
/// </summary>
export const useToasts = create<ToastState>()((set) => ({
  toasts: [],
  push: (toast) => {
    let id = 0
    set((state) => {
      const same = state.toasts.find((t) => t.kind === toast.kind && t.message === toast.message && t.title === toast.title)
      if (same) {
        id = same.id
        return {
          toasts: state.toasts.map((t) => (t.id === same.id ? { ...t, count: t.count + 1, stamp: nextStamp++, source: toast.source } : t)),
        }
      }
      id = nextId++
      const next = [...state.toasts, { ...toast, id, count: 1, stamp: nextStamp++ }]
      return { toasts: next.slice(-MAX_TOASTS) }
    })
    return id
  },
  dismiss: (id) => set((state) => ({ toasts: state.toasts.filter((t) => t.id !== id) })),
  dismissForSource: (source) => set((state) => {
    const remaining = state.toasts.filter((t) => t.source !== source)
    return remaining.length === state.toasts.length ? state : { toasts: remaining }
  }),
  clear: () => set({ toasts: [] }),
}))

/** Zeigt eine Erfolgs-/Hinweismeldung als Toast. */
export function showToast(kind: Exclude<ToastKind, 'error'>, message: string, title?: string): number {
  return useToasts.getState().push({ kind, message, title })
}

/** Zeigt einen Fehler als Toast (Meldung über api/errors.ts, mit Referenz zum Server-Log, falls vorhanden). */
export function showErrorToast(error: unknown, title: string = t('errors:actionFailedTitle')): number {
  const info = parseApiError(error)
  return useToasts.getState().push({ kind: 'error', title, message: info.message, reference: info.correlationId, source: error })
}

/**
 * Nimmt den globalen Toast zu diesem Fehler zurück. Für Stellen, die den Fehler selbst anzeigen (ErrorBanner,
 * LoadState, eigene Statuszeile): der Nutzer soll dieselbe Meldung nicht doppelt sehen.
 */
export function dismissToastsForError(error: unknown): void {
  if (error === null || error === undefined) return
  useToasts.getState().dismissForSource(error)
}
