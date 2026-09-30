import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { apiClient } from '../api/client'
import { getErrorMessage } from '../api/errors'
import { useChangeMyPassword } from '../api/hooks'
import { PASSWORD_MAX_BYTES, PASSWORD_MIN_LENGTH } from '../api/types'
import { useAuth } from '../state/auth'
import type { AuthUser } from '../state/auth'
import { dismissToastsForError } from '../state/toasts'
import { Modal } from './Modal'

/// <summary>
/// Modal-Dialog für den aktuellen User. Der Server antwortet auf den
/// Passwortwechsel mit 200 + neuem Token (neuer Vertrag) — dann wird die Session
/// im Store ersetzt — oder mit 204 (alter Vertrag) — dann wird ausgeloggt und
/// der Nutzer meldet sich mit dem neuen Passwort neu an.
///
/// Ein falsches Passwort im Feld "Aktuelles Passwort" beendet die Session NICHT
/// (der 401-Interceptor nimmt /auth/change-password aus); der Fehler erscheint
/// im Dialog.
///
/// Escape-Hatch: auch wenn `mustChangePassword=true` ist, gibt's einen
/// Logout-Button. Sonst wäre der User gefangen falls sein lokaler State stale
/// ist (Passwort wurde z.B. in einer früheren Session schon geändert, aber
/// der User-DTO im Auth-Store ist noch alt).
/// </summary>
export function ChangePasswordDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation()
  const user = useAuth((s) => s.user)
  const passwordChangeRequired = useAuth((s) => s.passwordChangeRequired)
  const setSession = useAuth((s) => s.setSession)
  const updateUser = useAuth((s) => s.updateUser)
  const clearPasswordChangeRequired = useAuth((s) => s.clearPasswordChangeRequired)
  const logout = useAuth((s) => s.logout)
  const mut = useChangeMyPassword()
  // Pflichtwechsel: laut User-DTO oder laut 403 "password_change_required".
  const forced = user?.mustChangePassword === true || passwordChangeRequired
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [refreshing, setRefreshing] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    // Dieselben Längenregeln wie der Server (PasswordPolicy); Benutzername und bisheriges Passwort prüft der Server.
    if (next.length < PASSWORD_MIN_LENGTH) return setError(t('auth:password.tooShort', { min: PASSWORD_MIN_LENGTH }))
    if (new TextEncoder().encode(next).length > PASSWORD_MAX_BYTES) return setError(t('auth:password.tooLong', { max: PASSWORD_MAX_BYTES }))
    if (next !== confirm) return setError(t('auth:password.mismatch'))
    try {
      const result = await mut.mutateAsync({ currentPassword: current, newPassword: next })
      if (result) {
        // Neuer Vertrag: frisches Token + User übernehmen (setzt das Pflicht-Flag zurück).
        setSession(result.token, result.expiresAt, result.user)
        onClose()
      } else {
        // Alter Vertrag (204): kein neues Token → neu anmelden lassen.
        logout(t('auth:password.changedNotice'))
      }
    } catch (err: unknown) {
      // Der Fehler steht im Dialog — der globale Toast dazu wäre doppelt.
      dismissToastsForError(err)
      setError(getErrorMessage(err, t('auth:password.failed')))
    }
  }

  // Refresh: hole den frischen User vom Backend. Falls das Passwort schon
  // geändert wurde (Backend `MustChangePassword=false`) aber der lokale Store
  // noch alt ist, schließt sich der Dialog danach automatisch.
  const refreshFromServer = async () => {
    setRefreshing(true)
    setError(null)
    try {
      const { data } = await apiClient.get<AuthUser>('/auth/me')
      updateUser(data)
      if (!data.mustChangePassword) {
        clearPasswordChangeRequired()
        onClose()
      }
    } catch {
      setError(t('auth:password.reloadFailed'))
    } finally {
      setRefreshing(false)
    }
  }

  return (
    <Modal
      title={t('auth:password.title')}
      width={400}
      // Pflichtwechsel: weder Escape noch Klick daneben schließen — der Dialog verschwindet erst, wenn die App-Shell ihn entsperrt.
      onClose={forced ? undefined : onClose}
      closeOnEscape={!forced}
      closeOnBackdrop={!forced}
    >
      <form onSubmit={submit}>
        {forced && (
          <div className="warning" style={{ fontSize: 13, marginBottom: 12, padding: 8 }}>
            {t('auth:password.forced')}
            <br />
            <span style={{ fontSize: 12 }}>
              {t('auth:password.ifChanged')}{' '}
              <button type="button" className="link-button" onClick={refreshFromServer} disabled={refreshing}>
                {refreshing ? t('common:loading') : t('auth:password.refresh')}
              </button>
            </span>
          </div>
        )}
        <label style={{ marginBottom: 8 }}>
          {t('auth:password.current')}
          <input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} data-autofocus autoComplete="current-password" required />
        </label>
        <label style={{ marginBottom: 8 }}>
          {t('auth:password.new', { min: PASSWORD_MIN_LENGTH })}
          <input type="password" value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" required />
        </label>
        <p className="muted" style={{ fontSize: 12, margin: '0 0 8px' }}>
          {t('auth:password.rules', { min: PASSWORD_MIN_LENGTH, max: PASSWORD_MAX_BYTES })}
        </p>
        <label style={{ marginBottom: 8 }}>
          {t('auth:password.repeat')}
          <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" required />
        </label>
        {error && <div className="error" role="alert" style={{ marginTop: 8 }}>{error}</div>}
        <div className="toolbar" style={{ marginTop: 16 }}>
          <button type="submit" className="primary" disabled={mut.isPending}>
            {mut.isPending ? t('common:saving') : t('auth:password.submit')}
          </button>
          {!forced && <button type="button" onClick={onClose}>{t('common:cancel')}</button>}
          {/* Escape-Hatch: auch im Zwangs-Modus immer abmelden können — sonst
              ist der User gefangen wenn der lokale State stale ist. */}
          <button
            type="button"
            className="text-danger"
            onClick={() => { logout(); onClose() }}
            style={{ marginLeft: 'auto' }}
          >
            {t('common:logout')}
          </button>
        </div>
      </form>
    </Modal>
  )
}
