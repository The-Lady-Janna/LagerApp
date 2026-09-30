import { isAxiosError } from 'axios'
import { t } from '../i18n'

/// <summary>
/// Kurze Fehlermeldung (Sprache der Oberfläche) zu einem fehlgeschlagenen API-Aufruf, z. B. für
/// Mutationen, deren Fehler sonst still verschwinden würden (Picking abschließen,
/// Retouren-QC). Der Server antwortet je nach Endpunkt mit { error }, { message },
/// { code, error } oder ProblemDetails { title, detail, errors } — daraus wird die
/// erste brauchbare Meldung gezogen; ohne Antwort-Body entscheidet der HTTP-Status.
/// </summary>
export function describeApiError(err: unknown, fallback: string = t('errors:actionFailed')): string {
  if (!isAxiosError(err)) {
    return err instanceof Error && err.message ? err.message : fallback
  }

  const status = err.response?.status
  const fromBody = messageFromBody(err.response?.data)
  if (fromBody) return status ? t('errors:withStatus', { message: fromBody, status }) : fromBody

  if (status === undefined) return t('errors:noConnection')
  if (status === 401) return t('errors:status.401')
  if (status === 403) return t('errors:status.403')
  if (status === 404) return t('errors:notFoundShort')
  if (status === 409) return t('errors:conflictShort')
  if (status >= 500) return t('errors:serverErrorShort', { status })
  return t('errors:withStatus', { message: fallback, status })
}

function messageFromBody(data: unknown): string | null {
  if (typeof data === 'string') return data.trim() !== '' && data.length <= 300 ? data.trim() : null
  if (typeof data !== 'object' || data === null) return null
  const body = data as Record<string, unknown>

  // Bewusst formulierte Meldung des Servers hat Vorrang vor Fehler-Codes wie "concurrency_conflict".
  for (const key of ['message', 'error', 'detail', 'title']) {
    const value = body[key]
    if (typeof value === 'string' && value.trim() !== '' && !isMachineCode(value)) return value.trim()
  }

  // ProblemDetails-Validierung: { errors: { Feld: ["Meldung", …] } }
  const errors = body.errors
  if (typeof errors === 'object' && errors !== null) {
    for (const value of Object.values(errors as Record<string, unknown>)) {
      if (Array.isArray(value) && typeof value[0] === 'string') return value[0]
    }
  }
  return null
}

/** "concurrency_conflict", "password_change_required" … sind Codes, keine Meldungen für Menschen. */
function isMachineCode(value: string): boolean {
  return /^[a-z0-9]+(?:_[a-z0-9]+)+$/.test(value)
}
