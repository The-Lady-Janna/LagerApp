import { CancelledError, MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { isAxiosError } from 'axios'
import { isHandledElsewhere } from '../api/errors'
import { t } from '../i18n'
import { showErrorToast } from '../state/toasts'

/// <summary>
/// Retry-Regel für Queries: Bei 4xx (falsche Eingabe, 401/403/404) bringt ein
/// Wiederholen nichts — es würde nur den Fehler verzögern und bei 401/403
/// unnötig Requests erzeugen. Sonst wie der TanStack-Default (3 Wiederholungen).
/// </summary>
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
  if (isAxiosError(error)) {
    const status = error.response?.status
    if (status !== undefined && status >= 400 && status < 500) return false
  }
  return failureCount < 3
}

/// <summary>
/// Meldet einen Query-/Mutationsfehler global als Toast — außer die Query/Mutation setzt `meta: { silent: true }`
/// (Aufrufer mit eigener Fehlerbehandlung) oder der Fehler wird ohnehin anderswo sichtbar (401 → Login-Screen,
/// Pflicht-Passwortwechsel → Dialog, abgebrochene Requests). Zeigt eine Seite den Fehler selbst an (ErrorBanner/
/// LoadState), nimmt sie den Toast wieder zurück.
/// </summary>
export function reportGlobalError(error: unknown, meta: Record<string, unknown> | undefined, title: string): void {
  if (meta?.silent === true) return
  if (error instanceof CancelledError || isHandledElsewhere(error)) return
  showErrorToast(error, title)
}

/// <summary>
/// Singleton, damit der Auth-Store beim Logout den Cache leeren kann
/// (`queryClient.clear()`). Sonst würde ein zweiter User im selben Tab (geteiltes
/// Lager-Terminal) kurz die gecachten Daten des Vorgängers sehen.
/// Fehler landen zentral als Toast (QueryCache/MutationCache.onError), damit keine Aktion still scheitert.
/// </summary>
export const queryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error, query) => reportGlobalError(error, query.meta, t('errors:loadFailedTitle')),
  }),
  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => reportGlobalError(error, mutation.meta, t('errors:actionFailedTitle')),
  }),
  defaultOptions: {
    queries: { staleTime: 30_000, refetchOnWindowFocus: false, retry: shouldRetryQuery },
  },
})
