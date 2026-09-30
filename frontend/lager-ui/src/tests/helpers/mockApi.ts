import { AxiosError, type AxiosAdapter, type AxiosResponse, type InternalAxiosRequestConfig } from 'axios'
import { apiClient } from '../../api/client'

/// <summary>
/// Gemockte API für Frontend-Tests: ersetzt den HTTP-Adapter des echten
/// apiClient, sodass Hooks, Interceptors und Seiten unverändert laufen, aber
/// keine Netzwerkverbindung nötig ist. Jede Anfrage wird protokolliert
/// (Methode, Pfad, Body), damit Tests prüfen können, WAS gesendet wurde.
///
/// Routen: "METHOD /pfad" (Pfad ohne /api und ohne Query, ":name" ist ein
/// Platzhalter). Antwort: ein Wert (JSON), eine Funktion (request, params) → Wert
/// oder fail(status, body) für einen Fehler.
/// </summary>

export interface RecordedRequest {
  method: string
  /** Pfad ohne baseURL ("/api") und ohne Query, z. B. "/articles/a1". */
  path: string
  query: string
  /** Gesendeter JSON-Body (geparst) oder undefined. */
  body: unknown
  /** Wert des Authorization-Headers, falls gesendet. */
  authorization?: string
}

interface Failure {
  readonly __failure: true
  readonly status: number
  readonly data: unknown
}

/** Antwort mit HTTP-Fehlerstatus (z. B. fail(500, { error: 'Bestand unzureichend' })). */
export function fail(status: number, data: unknown = null): Failure {
  return { __failure: true, status, data }
}

function isFailure(value: unknown): value is Failure {
  return typeof value === 'object' && value !== null && (value as { __failure?: unknown }).__failure === true
}

type HandlerFn = (request: RecordedRequest, params: Record<string, string>) => unknown
/** Feste Antwort (JSON-Wert) oder Funktion. (Kein `unknown` im Union, sonst geht die Typinferenz für Funktionen verloren.) */
type Handler = HandlerFn | object | string | number | boolean | null

export interface MockApi {
  /** Alle bisherigen Anfragen in der Reihenfolge des Eintreffens. */
  requests: RecordedRequest[]
  /** Anfragen einer Methode, deren Pfad dem Muster entspricht (":id" = Platzhalter, sonst exakt). */
  calls: (method: string, pathPattern: string) => RecordedRequest[]
  /** Setzt eine Route neu/um (für Tests, in denen sich die Serverantwort ändert). */
  setRoute: (key: string, handler: Handler) => void
  restore: () => void
}

interface Route {
  method: string
  pattern: RegExp
  handler: Handler
}

function toPattern(path: string): RegExp {
  const source = path
    .split('/')
    .map((segment) => (segment.startsWith(':') ? '(?<' + segment.slice(1) + '>[^/]+)' : segment.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
    .join('/')
  return new RegExp(`^${source}$`)
}

function parseKey(key: string): { method: string; path: string } {
  const space = key.indexOf(' ')
  return { method: key.slice(0, space).toUpperCase(), path: key.slice(space + 1) }
}

function parseBody(data: unknown): unknown {
  if (typeof data !== 'string') return data ?? undefined
  try {
    return JSON.parse(data)
  } catch {
    return data
  }
}

export function installMockApi(routeTable: Record<string, Handler> = {}): MockApi {
  const routes: Route[] = []
  const requests: RecordedRequest[] = []
  const originalAdapter = apiClient.defaults.adapter

  const setRoute = (key: string, handler: Handler) => {
    const { method, path } = parseKey(key)
    const index = routes.findIndex((r) => r.method === method && r.pattern.source === toPattern(path).source)
    const route = { method, pattern: toPattern(path), handler }
    if (index >= 0) routes[index] = route
    else routes.push(route)
  }
  for (const [key, handler] of Object.entries(routeTable)) setRoute(key, handler)

  const respond = (config: InternalAxiosRequestConfig, status: number, data: unknown): AxiosResponse => ({
    data,
    status,
    statusText: String(status),
    headers: {},
    config,
    request: {},
  })

  const adapter: AxiosAdapter = async (config) => {
    const [path, query = ''] = (config.url ?? '').split('?')
    const request: RecordedRequest = {
      method: (config.method ?? 'get').toUpperCase(),
      path,
      query,
      body: parseBody(config.data),
      authorization: String(config.headers?.get?.('Authorization') ?? '') || undefined,
    }
    requests.push(request)

    let reply: unknown
    const route = routes.find((r) => r.method === request.method && r.pattern.test(path))
    if (!route) {
      reply = fail(404, { error: `Kein Mock für ${request.method} ${path}` })
    } else {
      const params = route.pattern.exec(path)?.groups ?? {}
      reply = typeof route.handler === 'function'
        ? (route.handler as HandlerFn)(request, params)
        : route.handler
    }

    if (isFailure(reply)) {
      const response = respond(config, reply.status, reply.data)
      throw new AxiosError(`Request failed with status code ${reply.status}`, AxiosError.ERR_BAD_RESPONSE, config, {}, response)
    }
    // JSON-Kopie: Änderungen an der Antwort dürfen die Fixture des Tests nicht verändern.
    return respond(config, 200, reply === undefined ? undefined : JSON.parse(JSON.stringify(reply)))
  }

  apiClient.defaults.adapter = adapter

  return {
    requests,
    calls: (method, pathPattern) => {
      const pattern = toPattern(pathPattern)
      return requests.filter((r) => r.method === method.toUpperCase() && pattern.test(r.path))
    },
    setRoute,
    restore: () => {
      apiClient.defaults.adapter = originalAdapter
    },
  }
}
