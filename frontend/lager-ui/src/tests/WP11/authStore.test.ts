import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClient } from '../../api/client'
import { queryClient } from '../../lib/queryClient'
import { sessionExpiredNotice, startSessionWatcher } from '../../lib/sessionWatcher'
import { isAuthenticated, useAuth, type AuthUser } from '../../state/auth'
import { useActiveWarehouse } from '../../state/activeWarehouse'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'

// Auth-Store-Ablauf aus WP04: Session setzen, Ablauf (Server-expiresAt und JWT-exp), Logout (Cache + Lagerauswahl leeren),
// automatischer Logout per Timer und die 401/403-Regeln des Axios-Clients.
const user: AuthUser = {
  id: 'u1',
  username: 'anna',
  email: null,
  displayName: null,
  roles: ['Picker'],
  isActive: true,
  mustChangePassword: false,
}

const inSeconds = (s: number) => new Date(Date.now() + s * 1000).toISOString()

/** Unsignierter Test-JWT mit exp-Claim (Sekunden seit Epoch) — das Frontend liest nur den Claim. */
function jwtWithExp(expSeconds: number): string {
  const b64 = (o: object) => btoa(JSON.stringify(o)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_')
  return `${b64({ alg: 'none' })}.${b64({ exp: expSeconds })}.sig`
}

describe('Auth-Store', () => {
  afterEach(() => {
    vi.useRealTimers()
    useAuth.getState().logout()
  })

  it('setSession macht den Nutzer angemeldet; nur Token, Ablauf und Nutzer werden persistiert', () => {
    useAuth.getState().requirePasswordChange()
    useAuth.getState().setSession('token-1', inSeconds(3600), user)

    expect(isAuthenticated()).toBe(true)
    expect(useAuth.getState().passwordChangeRequired).toBe(false) // setSession räumt das Flag ab
    const stored = JSON.parse(localStorage.getItem('lager.auth')!).state
    expect(Object.keys(stored).sort()).toEqual(['expiresAt', 'token', 'user'])
  })

  it('gilt als abgemeldet, sobald der frühere von expiresAt und JWT-exp erreicht ist', () => {
    // Server sagt "noch 1 h", das Token selbst läuft aber vor 10 s ab → der frühere Zeitpunkt zählt.
    useAuth.getState().setSession(jwtWithExp(Math.floor(Date.now() / 1000) - 10), inSeconds(3600), user)
    expect(isAuthenticated()).toBe(false)

    // Umgekehrt: expiresAt liegt in der Vergangenheit.
    useAuth.getState().setSession(jwtWithExp(Math.floor(Date.now() / 1000) + 3600), inSeconds(-5), user)
    expect(isAuthenticated()).toBe(false)

    useAuth.getState().setSession(jwtWithExp(Math.floor(Date.now() / 1000) + 3600), inSeconds(3600), user)
    expect(isAuthenticated()).toBe(true)
  })

  it('Logout leert Session, Query-Cache und Lagerauswahl und merkt sich den Hinweis für den Login-Screen', () => {
    useAuth.getState().setSession('token-1', inSeconds(3600), user)
    useActiveWarehouse.getState().setActive('w1')
    queryClient.setQueryData(['articles'], [{ id: 'a1' }])

    useAuth.getState().logout('Sitzung abgelaufen')

    expect(useAuth.getState()).toMatchObject({ token: null, expiresAt: null, user: null, notice: 'Sitzung abgelaufen' })
    expect(queryClient.getQueryData(['articles'])).toBeUndefined() // der nächste Nutzer sieht keine Daten des Vorgängers
    expect(useActiveWarehouse.getState().activeId).toBeNull()
    expect(JSON.parse(localStorage.getItem('lager.auth')!).state.token).toBeNull()
  })

  it('hasRole spiegelt die Server-Policies (Admin > Manager > Rolle)', () => {
    useAuth.getState().setSession('t', inSeconds(3600), { ...user, roles: ['Manager'] })
    expect(useAuth.getState().hasRole('Picker')).toBe(true)
    expect(useAuth.getState().hasRole('Admin')).toBe(false)

    useAuth.getState().updateUser({ ...user, roles: ['Admin'] })
    expect(useAuth.getState().hasRole('Admin')).toBe(true)
  })
})

describe('Session-Watcher', () => {
  afterEach(() => {
    vi.useRealTimers()
    useAuth.getState().logout()
  })

  it('meldet automatisch ab, wenn das Token abläuft', () => {
    vi.useFakeTimers()
    useAuth.getState().setSession('token-1', inSeconds(60), user)
    const stop = startSessionWatcher()

    vi.advanceTimersByTime(59_000)
    expect(useAuth.getState().token).toBe('token-1')

    vi.advanceTimersByTime(2_000)
    expect(useAuth.getState().token).toBeNull()
    expect(useAuth.getState().notice).toBe(sessionExpiredNotice())
    stop()
  })

  it('verwirft ein abgelaufenes, aus dem localStorage rehydriertes Token beim Start sofort', () => {
    useAuth.getState().setSession('token-1', inSeconds(-1), user)

    const stop = startSessionWatcher()

    expect(useAuth.getState().token).toBeNull()
    expect(useAuth.getState().notice).toBe(sessionExpiredNotice())
    stop()
  })

  it('setzt den Timer bei einem neuen Token (z. B. nach Passwortwechsel) neu', () => {
    vi.useFakeTimers()
    useAuth.getState().setSession('alt', inSeconds(30), user)
    const stop = startSessionWatcher()

    useAuth.getState().setSession('neu', inSeconds(600), user)
    vi.advanceTimersByTime(60_000)

    expect(useAuth.getState().token).toBe('neu') // der alte 30-s-Timer hat die neue Session nicht beendet
    stop()
  })
})

describe('Axios-Client und Session', () => {
  let api: MockApi
  beforeEach(() => {
    useAuth.getState().setSession('token-1', inSeconds(3600), user)
    api = installMockApi()
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('sendet das Token als Bearer-Header', async () => {
    api.setRoute('GET /stock', [])

    await apiClient.get('/stock')

    expect(api.requests[0].authorization).toBe('Bearer token-1')
  })

  it('beendet die Session bei 401 einer normalen Anfrage', async () => {
    api.setRoute('GET /stock', fail(401))

    await expect(apiClient.get('/stock')).rejects.toBeDefined()

    expect(useAuth.getState().token).toBeNull()
    expect(useAuth.getState().notice).toBe('Die Sitzung ist nicht mehr gültig. Bitte neu anmelden.')
  })

  it('beendet die Session NICHT bei 401 auf Login/Passwortwechsel (falsches Passwort ≠ ungültiges Token)', async () => {
    api.setRoute('POST /auth/login', fail(401, { error: 'Benutzername oder Passwort falsch' }))
    api.setRoute('POST /auth/change-password', fail(401, { error: 'Aktuelles Passwort falsch' }))

    await expect(apiClient.post('/auth/login', {})).rejects.toBeDefined()
    await expect(apiClient.post('/auth/change-password', {})).rejects.toBeDefined()

    expect(useAuth.getState().token).toBe('token-1')
  })

  it('ignoriert ein spätes 401 auf einen Request, der noch mit einem alten Token gesendet wurde', async () => {
    api.setRoute('GET /stock', () => {
      // Während der Request unterwegs ist, wechselt die Session (z. B. Passwortwechsel mit frischem Token).
      useAuth.getState().setSession('token-2', inSeconds(3600), user)
      return fail(401)
    })

    await expect(apiClient.get('/stock')).rejects.toBeDefined()

    expect(useAuth.getState().token).toBe('token-2')
  })

  it('merkt sich 403 "password_change_required" im Store', async () => {
    api.setRoute('GET /stock', fail(403, { code: 'password_change_required' }))

    await expect(apiClient.get('/stock')).rejects.toBeDefined()

    expect(useAuth.getState().passwordChangeRequired).toBe(true)
    expect(useAuth.getState().token).toBe('token-1') // Session bleibt, nur der Passwort-Dialog ist erreichbar
  })
})
