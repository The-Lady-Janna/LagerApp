import { isAxiosError } from 'axios'
import { DEFAULT_LANGUAGE, currentLanguage, i18n, t } from '../i18n'

/// <summary>
/// Zentrale Fehlerauswertung des Frontends: macht aus einem beliebigen Fehler (AxiosError, Error, String)
/// eine verständliche Meldung (Deutsch oder Englisch, je nach Sprache der Oberfläche) — statt "AxiosError: Request failed with status code 409".
///
/// Der Server antwortet je nach Alter des Endpunkts in unterschiedlichen Formen; alle werden verstanden:
///   - application/problem+json (Vertrag von WP12): { title, detail, status, code, correlationId, errors }
///     (Validierung: errors = { Feld: ["Meldung", …] })
///   - ältere Formen: { error: "Meldung" }, { error: "code", message, detail } (Concurrency-409)
///   - kein/leerer Body (z. B. 500 in Production) und Netzwerkfehler/Timeout: dann entscheidet der Status.
/// </summary>

/// <summary>
/// Maschinenlesbare Fehlercodes (snake_case) des Servers → Klartext aus der Tabelle "codes" im Namespace errors
/// (src/locales/<sprache>/errors.json). Backend-Meldungen selbst bleiben deutsch und werden NICHT übersetzt; die Tabelle
/// fängt die häufigen Codes ab. Deutsch: der Text des Servers hat Vorrang, die Tabelle ist nur Fallback, wenn er keinen
/// mitschickt. Andere Sprachen: ein bekannter Code wird aus der Tabelle übersetzt, ein unbekannter zeigt weiter das deutsche detail.
/// </summary>
function codeMessage(code: string | undefined): string | undefined {
  if (code === undefined || !/^[a-z0-9_]+$/.test(code)) return undefined
  const key = `errors:codes.${code}`
  return i18n.exists(key) ? t(key) : undefined
}

export type ApiErrorKind = 'http' | 'network' | 'timeout' | 'canceled' | 'unknown'

export interface ApiErrorInfo {
  kind: ApiErrorKind
  /** HTTP-Status; bei Netzwerkfehler/Timeout nicht vorhanden. */
  status?: number
  /** Maschinenlesbarer Fehlercode des Servers (snake_case). */
  code?: string
  /** Fertige, deutsche Meldung für Menschen (nie leer). */
  message: string
  /** Nur die vom Server mitgelieferte Meldung (ohne Status-Fallback) — für Aufrufer mit eigenem Fallback (Login). */
  serverMessage?: string
  /** Referenz zum Server-Log (Body oder Header X-Correlation-Id). */
  correlationId?: string
  /** Validierungsfehler pro Feld (ProblemDetails.errors). */
  fieldErrors?: Record<string, string[]>
}

const MAX_TEXT_LENGTH = 300
const MAX_FIELD_MESSAGES = 3
const CORRELATION_HEADER = 'x-correlation-id'

/** Englische Standardtitel von ASP.NET/HTTP, die für Menschen nichts sagen — dann entscheidet der Status. */
const GENERIC_TITLE =
  /^(bad request|unauthorized|forbidden|not found|conflict|unprocessable (entity|content)|internal server error|service unavailable|bad gateway|gateway timeout|too many requests|one or more validation errors occurred\.?|an error occurred while processing your request\.?)$/i

/** "concurrency_conflict", "insufficient_stock" … sind Codes, keine Meldungen für Menschen. */
function isMachineCode(value: string): boolean {
  return /^[a-z0-9]+(?:_[a-z0-9]+)+$/.test(value)
}

function asText(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined
  const text = value.trim()
  return text !== '' ? text : undefined
}

const KNOWN_STATUS = new Set([400, 401, 403, 404, 408, 409, 422, 429])

function statusMessage(status: number, fallback: string): string {
  if (KNOWN_STATUS.has(status)) return t(`errors:status.${status}`)
  if (status >= 500) return t('errors:serverError', { status })
  return t('errors:withStatus', { message: fallback, status })
}

/** Alle Meldungen aus ProblemDetails.errors ({ Feld: ["Meldung", …] } bzw. { Feld: "Meldung" }), ohne Duplikate. */
function collectFieldErrors(errors: unknown): Record<string, string[]> | undefined {
  if (typeof errors !== 'object' || errors === null || Array.isArray(errors)) return undefined
  const result: Record<string, string[]> = {}
  for (const [field, value] of Object.entries(errors as Record<string, unknown>)) {
    const messages = (Array.isArray(value) ? value : [value]).map(asText).filter((m): m is string => m !== undefined)
    if (messages.length > 0) result[field] = messages
  }
  return Object.keys(result).length > 0 ? result : undefined
}

function summarizeFieldErrors(fieldErrors: Record<string, string[]>): string {
  const all = [...new Set(Object.values(fieldErrors).flat())]
  const shown = all.slice(0, MAX_FIELD_MESSAGES).join(' ')
  const more = all.length - MAX_FIELD_MESSAGES
  return more > 0 ? t('errors:validationMore', { messages: shown, count: more }) : t('errors:validation', { messages: shown })
}

function readCorrelationHeader(headers: unknown): string | undefined {
  if (typeof headers !== 'object' || headers === null) return undefined
  const source = headers as { get?: (name: string) => unknown } & Record<string, unknown>
  const value = typeof source.get === 'function' ? source.get(CORRELATION_HEADER) : source[CORRELATION_HEADER]
  return asText(value)
}

interface ParsedBody {
  code?: string
  /** Bewusst formulierter Text des Servers (detail/message/error), nie ein Maschinencode. */
  text?: string
  title?: string
  correlationId?: string
  fieldErrors?: Record<string, string[]>
}

function parseBody(data: unknown): ParsedBody {
  if (typeof data === 'string') {
    // Kurzer Klartext des Servers; HTML-Fehlerseiten (Development) sind keine brauchbare Meldung.
    const text = data.trim()
    return text !== '' && text.length <= MAX_TEXT_LENGTH && !text.startsWith('<') ? { text } : {}
  }
  if (typeof data !== 'object' || data === null || Array.isArray(data)) return {}
  const body = data as Record<string, unknown>

  const error = asText(body.error)
  // Ältere Antworten: { error: "concurrency_conflict", message, detail } → error ist dann der Code, message der Text
  // für Menschen und detail die technische Ausnahme (die ProblemDetails-Reihenfolge detail → message gilt dort nicht).
  const legacyCode = asText(body.code) === undefined && error !== undefined && isMachineCode(error)
  const code = asText(body.code) ?? (legacyCode ? error : undefined)
  let text: string | undefined
  for (const candidate of legacyCode ? [body.message, body.detail] : [body.detail, body.message, body.error]) {
    const value = asText(candidate)
    if (value !== undefined && !isMachineCode(value)) { text = value; break }
  }
  const title = asText(body.title)

  return {
    code,
    text,
    title: title !== undefined && !GENERIC_TITLE.test(title) && !isMachineCode(title) ? title : undefined,
    correlationId: asText(body.correlationId) ?? asText(body.traceId),
    fieldErrors: collectFieldErrors(body.errors),
  }
}

/**
 * Wertet einen Fehler vollständig aus (Status, Code, Meldung, Referenz). Die Meldung stammt aus dieser Reihenfolge:
 * Feld-Validierung → detail/message/error des Servers → title → Klartext zum Fehlercode → Text zum HTTP-Status.
 */
export function parseApiError(err: unknown, fallback: string = t('errors:actionFailed')): ApiErrorInfo {
  if (!isAxiosError(err)) {
    if (typeof err === 'string' && err.trim() !== '') return { kind: 'unknown', message: err.trim() }
    if (err instanceof Error && err.message.trim() !== '') return { kind: 'unknown', message: err.message.trim() }
    return { kind: 'unknown', message: fallback }
  }

  const response = err.response
  if (!response) {
    if (err.code === 'ERR_CANCELED') return { kind: 'canceled', message: t('errors:canceled') }
    if (err.code === 'ECONNABORTED' || err.code === 'ETIMEDOUT') {
      return { kind: 'timeout', message: t('errors:timeout') }
    }
    return { kind: 'network', message: t('errors:network') }
  }

  const status = response.status
  const body = parseBody(response.data)
  const codeText = codeMessage(body.code)
  const serverMessage = body.fieldErrors ? summarizeFieldErrors(body.fieldErrors) : body.text ?? body.title
  // Deutsch: der (deutsche) Text des Servers hat Vorrang. Andere Sprachen: ein bekannter Code wird übersetzt; ohne Übersetzung
  // bleibt der deutsche Servertext (Backend-Meldungen werden nicht übersetzt). Validierungsfehler sind immer die Serverliste.
  const preferCode = currentLanguage() !== DEFAULT_LANGUAGE && codeText !== undefined && !body.fieldErrors

  return {
    kind: 'http',
    status,
    code: body.code,
    message: preferCode ? codeText : serverMessage ?? codeText ?? statusMessage(status, fallback),
    serverMessage,
    correlationId: body.correlationId ?? readCorrelationHeader(response.headers),
    fieldErrors: body.fieldErrors,
  }
}

/** Verständliche Fehlermeldung zu einem beliebigen Fehler (nie leer, nie "AxiosError: …"). */
export function getErrorMessage(err: unknown, fallback: string = t('errors:actionFailed')): string {
  return parseApiError(err, fallback).message
}

/** Maschinenlesbarer Fehlercode des Servers (z. B. "insufficient_stock"), sonst undefined. */
export function getErrorCode(err: unknown): string | undefined {
  return parseApiError(err).code
}

/** HTTP-Status der Serverantwort; undefined bei Netzwerkfehler/Timeout/Nicht-HTTP-Fehlern. */
export function getErrorStatus(err: unknown): number | undefined {
  return parseApiError(err).status
}

/** Referenz zum Server-Log (correlationId aus Body oder Header), falls vorhanden. */
export function getCorrelationId(err: unknown): string | undefined {
  return parseApiError(err).correlationId
}

/**
 * true für Fehler, die der Auth-Ablauf ohnehin sichtbar macht: 401 (der Interceptor meldet ab, der Login-Screen zeigt den
 * Hinweis) und der Pflicht-Passwortwechsel (die App zeigt nur noch den Dialog) sowie abgebrochene Anfragen. Ein globaler
 * Toast wäre dafür nur doppelt.
 */
export function isHandledElsewhere(err: unknown): boolean {
  const info = parseApiError(err)
  return info.kind === 'canceled' || info.status === 401 || info.code === 'password_change_required'
}
