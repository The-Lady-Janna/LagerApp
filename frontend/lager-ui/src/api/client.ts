import axios, { type InternalAxiosRequestConfig } from 'axios'
import { t } from '../i18n'
import { useAuth } from '../state/auth'

export const apiClient = axios.create({
  baseURL: '/api',
  headers: {
    'Content-Type': 'application/json',
  },
})

/** Fehler-Code des Servers (403), solange ein Pflicht-Passwortwechsel aussteht. */
export const PASSWORD_CHANGE_REQUIRED = 'password_change_required'

// Bei diesen Endpunkten bedeutet ein 401 "Zugangsdaten falsch" und NICHT "Token
// ungültig": ein falsches Passwort darf die bestehende Session nicht beenden.
const CREDENTIAL_ENDPOINTS = ['/auth/login', '/auth/change-password']

function isCredentialRequest(config: InternalAxiosRequestConfig | undefined): boolean {
  const path = (config?.url ?? '').split('?')[0].replace(/\/+$/, '').toLowerCase()
  return CREDENTIAL_ENDPOINTS.some((endpoint) => path === endpoint || path === `/api${endpoint}`)
}

// Outbound: append Bearer token if we have one. Pulled from the store at send-
// time so token rotation / logout takes effect immediately on the next call.
apiClient.interceptors.request.use((config) => {
  const token = useAuth.getState().token
  if (token) {
    config.headers.set('Authorization', `Bearer ${token}`)
  }
  return config
})

apiClient.interceptors.response.use(
  (res) => res,
  (err) => {
    const status: number | undefined = err.response?.status

    // 401 = token invalid / expired / missing → kick the session and let the
    // App's auth gate redirect to /login. We do NOT redirect from here directly
    // so React Query's normal error handling still fires.
    // Ausnahmen: Login / Passwortwechsel (falsches Passwort ≠ Session ungültig),
    // bereits ausgeloggt (späte Antwort eines alten Requests) sowie Antworten auf
    // Requests, die noch mit einem ALTEN Token gesendet wurden (z. B. nach einem
    // Passwortwechsel mit frischem Token).
    if (status === 401 && useAuth.getState().token && !isCredentialRequest(err.config) && !isStaleTokenRequest(err.config)) {
      useAuth.getState().logout(t('errors:status.401'))
    }

    // 403 + code "password_change_required": der Server sperrt alles außer dem
    // Passwortwechsel → im Auth-Store merken, die App zeigt nur noch den Dialog.
    if (status === 403 && err.response?.data?.code === PASSWORD_CHANGE_REQUIRED) {
      useAuth.getState().requirePasswordChange()
    }

    if (err.response?.data) {
      console.error('API error:', err.response.data)
    }
    return Promise.reject(err)
  }
)

/** true, wenn der Request mit einem anderen Token gesendet wurde als dem aktuellen. */
function isStaleTokenRequest(config: InternalAxiosRequestConfig | undefined): boolean {
  const sent = config?.headers?.get?.('Authorization')
  return typeof sent === 'string' && sent !== `Bearer ${useAuth.getState().token}`
}
