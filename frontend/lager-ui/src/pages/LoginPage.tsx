import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { apiClient } from '../api/client'
import { parseApiError } from '../api/errors'
import { LanguageSwitcher } from '../components/LanguageSwitcher'
import { useAuth } from '../state/auth'
import type { AuthUser } from '../state/auth'

interface LoginResponse {
  token: string
  expiresAt: string
  user: AuthUser
}

/// <summary>
/// Vollbild-Login. Wird vom App-Shell gerendert wenn kein gültiger Token
/// vorhanden ist. Nach erfolgreichem Login wird der Auth-Store gesetzt, die
/// Komponente unmountet sich, und die normale App-Sicht erscheint.
/// </summary>
export function LoginPage() {
  const { t } = useTranslation()
  const setSession = useAuth((s) => s.setSession)
  // Hinweis nach automatischem Logout / Passwortwechsel (z. B. "Sitzung abgelaufen").
  const notice = useAuth((s) => s.notice)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!username || !password) return
    setPending(true)
    setError(null)
    try {
      const { data } = await apiClient.post<LoginResponse>('/auth/login', { username, password })
      // Bei mustChangePassword zeigt die App-Shell danach nur den Passwort-Dialog
      // (components/ForcePasswordChange.tsx) — kein alert() nötig.
      setSession(data.token, data.expiresAt, data.user)
    } catch (err: unknown) {
      // Meldung des Servers (ProblemDetails/{error}); ohne sie bei 401/400 eine generische Meldung (kein Leak, welche
      // Stelle versagt hat), bei Netzwerkfehler/Serverfehler dagegen der passende Hinweis ("Keine Verbindung …").
      const info = parseApiError(err)
      const generic = info.status === undefined || info.status >= 500 ? info.message : t('auth:login.failed')
      setError(info.serverMessage ?? generic)
    } finally {
      setPending(false)
    }
  }

  return (
    <div
      style={{
        minHeight: '100dvh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'var(--c-sidebar-bg)',
        padding: 16,
      }}
    >
      <form
        onSubmit={submit}
        style={{
          background: 'var(--c-surface)',
          color: 'var(--c-text)',
          border: '1px solid var(--c-border)',
          padding: 32,
          borderRadius: 12,
          boxShadow: '0 20px 60px var(--c-shadow)',
          width: '100%',
          maxWidth: 380,
        }}
      >
        <LanguageSwitcher variant="plain" />
        <h1 style={{ marginTop: 0, marginBottom: 4, fontSize: 28 }}>Lager</h1>
        <p className="muted" style={{ marginTop: 0, marginBottom: 24, fontSize: 13 }}>
          {t('auth:login.title')}
        </p>

        <label style={{ display: 'block', marginBottom: 12 }}>
          <span style={{ fontSize: 12, color: 'var(--c-muted)' }}>{t('auth:login.username')}</span>
          <input
            type="text"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            autoFocus
            required
            style={{ display: 'block', width: '100%', marginTop: 4, padding: 8, fontSize: 16 }}
          />
        </label>

        <label style={{ display: 'block', marginBottom: 16 }}>
          <span style={{ fontSize: 12, color: 'var(--c-muted)' }}>{t('auth:login.password')}</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
            style={{ display: 'block', width: '100%', marginTop: 4, padding: 8, fontSize: 16 }}
          />
        </label>

        {notice && !error && (
          <div role="status" className="warning" style={{ marginBottom: 12, padding: 8, fontSize: 13 }}>
            {notice}
          </div>
        )}

        {error && (
          <div className="error" role="alert" style={{ marginBottom: 12, padding: 8 }}>
            {error}
          </div>
        )}

        <button
          type="submit"
          className="primary"
          disabled={pending || !username || !password}
          style={{ width: '100%', padding: 12, fontSize: 16, fontWeight: 600 }}
        >
          {pending ? t('auth:login.pending') : t('auth:login.submit')}
        </button>

        {/* Erststart-Hinweis (Text: auth:login.firstStart); der Benutzername bleibt leer, es gibt keine vorbelegten Zugangsdaten. */}
        <p className="muted" style={{ marginTop: 16, fontSize: 11, textAlign: 'center' }}>
          {t('auth:login.firstStart')}
        </p>
      </form>
    </div>
  )
}
