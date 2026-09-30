import { describe, expect, it } from 'vitest'
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios'
import { getCorrelationId, getErrorCode, getErrorMessage, getErrorStatus, isHandledElsewhere, parseApiError } from '../../api/errors'

/** AxiosError mit Serverantwort (status) bzw. ohne (Netzwerk/Timeout: status undefined, code steuerbar). */
function axiosError(status: number | undefined, data?: unknown, options: { code?: string; headers?: Record<string, string> } = {}): AxiosError {
  const config = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig
  const response = status === undefined
    ? undefined
    : { data, status, statusText: '', headers: new AxiosHeaders(options.headers), config }
  return new AxiosError('Request failed', options.code ?? AxiosError.ERR_BAD_RESPONSE, config, {}, response)
}

describe('getErrorMessage — ProblemDetails (Vertrag von WP12)', () => {
  it('zeigt das deutsche detail eines 409 statt "Request failed with status code 409"', () => {
    const error = axiosError(409, {
      type: 'about:blank', title: 'Conflict', status: 409,
      detail: 'Die Bestellung wurde bereits storniert.', code: 'order_not_cancellable', correlationId: 'c0ffee',
    })
    expect(getErrorMessage(error)).toBe('Die Bestellung wurde bereits storniert.')
    expect(getErrorCode(error)).toBe('order_not_cancellable')
    expect(getErrorStatus(error)).toBe(409)
    expect(getCorrelationId(error)).toBe('c0ffee')
  })

  it('fasst Validierungsfehler (errors) zusammen, statt den englischen Standardtitel zu zeigen', () => {
    const error = axiosError(400, {
      title: 'One or more validation errors occurred.', status: 400,
      errors: { Name: ['Name ist erforderlich.'], Sku: ['SKU ist zu lang.', 'Name ist erforderlich.'], Weight: ['Gewicht muss positiv sein.'], Extra: ['Vierte Meldung.'] },
    })
    const message = getErrorMessage(error)
    expect(message).toBe('Eingabe ungültig: Name ist erforderlich. SKU ist zu lang. Gewicht muss positiv sein. (+1 weitere)')
    expect(message).not.toContain('One or more')
    expect(parseApiError(error).fieldErrors).toMatchObject({ Name: ['Name ist erforderlich.'] })
  })

  it('nimmt title, wenn er eine echte Meldung ist, und ignoriert Standardtitel', () => {
    expect(getErrorMessage(axiosError(422, { title: 'Bestand reicht nicht aus', status: 422 }))).toBe('Bestand reicht nicht aus')
    expect(getErrorMessage(axiosError(404, { title: 'Not Found', status: 404 }))).toBe('Der Datensatz wurde nicht gefunden.')
  })

  it('nutzt die Klartext-Tabelle zum Fehlercode, wenn der Server keinen Text mitschickt', () => {
    expect(getErrorMessage(axiosError(409, { status: 409, code: 'insufficient_stock' }))).toBe('Der Bestand reicht für diese Aktion nicht aus.')
    expect(getErrorMessage(axiosError(409, { status: 409, code: 'duplicate_order_number' }))).toBe('Diese Auftragsnummer existiert bereits.')
    expect(getErrorMessage(axiosError(403, { status: 403, code: 'password_change_required' }))).toBe('Bitte zuerst das Passwort ändern.')
  })

  it('liest die correlationId auch aus dem Header X-Correlation-Id', () => {
    const error = axiosError(500, null, { headers: { 'X-Correlation-Id': 'abc123' } })
    expect(getCorrelationId(error)).toBe('abc123')
  })
})

describe('getErrorMessage — ältere Antwortformen', () => {
  it('{ error: "Meldung" }', () => {
    expect(getErrorMessage(axiosError(400, { error: 'Es sind noch nicht alle Positionen gezählt' }))).toBe('Es sind noch nicht alle Positionen gezählt')
  })

  it('Concurrency-409 { error: "concurrency_conflict", message, detail }: der Code ist keine Meldung', () => {
    const error = axiosError(409, { error: 'concurrency_conflict', message: 'Der Datensatz wurde zwischenzeitlich geändert.', detail: 'technische Details' })
    // Hier ist message der Text für Menschen (detail die technische Ausnahme) — und nie der Code selbst.
    expect(getErrorMessage(error)).toBe('Der Datensatz wurde zwischenzeitlich geändert.')
    expect(getErrorCode(error)).toBe('concurrency_conflict')
    // Nur der Code: Klartext aus der Tabelle.
    expect(getErrorMessage(axiosError(409, { error: 'concurrency_conflict' }))).toContain('zwischenzeitlich von jemand anderem geändert')
  })

  it('{ message } und reiner Text-Body', () => {
    expect(getErrorMessage(axiosError(400, { message: 'Ungültige Menge' }))).toBe('Ungültige Menge')
    expect(getErrorMessage(axiosError(400, 'Ungültige Menge'))).toBe('Ungültige Menge')
  })

  it('eine HTML-Fehlerseite (Development) ist keine Meldung — dann entscheidet der Status', () => {
    expect(getErrorMessage(axiosError(500, '<!DOCTYPE html><html><body>Boom</body></html>'))).toBe('Serverfehler (HTTP 500). Bitte später erneut versuchen.')
  })
})

describe('getErrorMessage — Status, Netzwerk und sonstige Fehler', () => {
  it.each([
    [401, 'Die Sitzung ist nicht mehr gültig. Bitte neu anmelden.'],
    [403, 'Keine Berechtigung für diese Aktion.'],
    [404, 'Der Datensatz wurde nicht gefunden.'],
    [409, 'Konflikt: Die Daten wurden zwischenzeitlich geändert. Bitte neu laden und erneut versuchen.'],
    [422, 'Die Eingabe konnte nicht verarbeitet werden.'],
    [500, 'Serverfehler (HTTP 500). Bitte später erneut versuchen.'],
    [503, 'Serverfehler (HTTP 503). Bitte später erneut versuchen.'],
  ])('ohne Body erklärt HTTP %i sich auf Deutsch', (status, expected) => {
    expect(getErrorMessage(axiosError(status))).toBe(expected)
  })

  it('unterscheidet Netzwerkfehler und Timeout', () => {
    expect(getErrorMessage(axiosError(undefined, undefined, { code: AxiosError.ERR_NETWORK }))).toContain('Keine Verbindung zum Server')
    expect(getErrorMessage(axiosError(undefined, undefined, { code: AxiosError.ECONNABORTED }))).toContain('Zeitüberschreitung')
    expect(parseApiError(axiosError(undefined, undefined, { code: AxiosError.ETIMEDOUT })).kind).toBe('timeout')
    expect(getErrorMessage(axiosError(undefined, undefined, { code: AxiosError.ERR_NETWORK }))).not.toContain('Network Error')
  })

  it('unbekannter Status nutzt den Fallback des Aufrufers', () => {
    expect(getErrorMessage(axiosError(418), 'Speichern fehlgeschlagen.')).toBe('Speichern fehlgeschlagen. (HTTP 418)')
  })

  it('Nicht-Axios-Fehler: Error-Text, String oder Fallback — nie "[object Object]"', () => {
    expect(getErrorMessage(new Error('kaputt'))).toBe('kaputt')
    expect(getErrorMessage('Fehler: Bin nicht gefunden')).toBe('Fehler: Bin nicht gefunden')
    expect(getErrorMessage({ irgendwas: 1 }, 'Aktion fehlgeschlagen.')).toBe('Aktion fehlgeschlagen.')
    expect(getErrorMessage(undefined)).toBe('Aktion fehlgeschlagen.')
  })
})

describe('isHandledElsewhere', () => {
  it('401 (Login-Screen), Pflicht-Passwortwechsel und abgebrochene Requests brauchen keinen Toast', () => {
    expect(isHandledElsewhere(axiosError(401))).toBe(true)
    expect(isHandledElsewhere(axiosError(403, { code: 'password_change_required' }))).toBe(true)
    expect(isHandledElsewhere(axiosError(undefined, undefined, { code: AxiosError.ERR_CANCELED }))).toBe(true)
    expect(isHandledElsewhere(axiosError(403))).toBe(false)
    expect(isHandledElsewhere(axiosError(409, { detail: 'Konflikt' }))).toBe(false)
    expect(isHandledElsewhere(new Error('x'))).toBe(false)
  })
})
