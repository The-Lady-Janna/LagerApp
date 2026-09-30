import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useCreateUser, useResetUserPassword, useToggleUser, useUpdateUser, useUsersList } from '../api/hooks'
import { PASSWORD_MIN_LENGTH, type UserDto } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { Modal } from '../components/Modal'
import { formatDateTime } from '../lib/format'
import { useAuth } from '../state/auth'

const ALL_ROLES = ['Admin', 'Manager', 'Receiver', 'Picker', 'Packer', 'Viewer']

/// <summary>
/// Admin-only User-Verwaltung. Liste + Inline-Rollen-Edit + Reset-Password-
/// Dialog + Aktivieren/Deaktivieren. Hard-deletes nicht angeboten —
/// Deaktivieren bewahrt den Audit-Trail. Deaktivieren und Rollenänderungen fragen vorher nach; der
/// eigene Account lässt sich nicht deaktivieren und verliert seine Admin-Rolle nicht (Aussperr-Schutz).
/// </summary>
export function UsersPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useUsersList()
  const ownId = useAuth((s) => s.user?.id)
  const createMut = useCreateUser()
  const updateMut = useUpdateUser()
  const resetMut = useResetUserPassword()
  const toggleMut = useToggleUser()

  const [showCreate, setShowCreate] = useState(false)
  const [resetFor, setResetFor] = useState<UserDto | null>(null)
  const [confirm, setConfirm] = useState<{ kind: 'deactivate'; user: UserDto } | { kind: 'roles'; user: UserDto; roles: string[] } | null>(null)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('users:what')} onRetry={() => void refetch()} />

  // Nur eine Änderung gleichzeitig: solange eine läuft, sind die Zeilen-Buttons gesperrt.
  const actionPending = updateMut.isPending || toggleMut.isPending

  const runConfirmed = () => {
    if (confirm?.kind === 'deactivate') toggleMut.mutate({ id: confirm.user.id, active: false })
    else if (confirm?.kind === 'roles') {
      const { user, roles } = confirm
      // Aussperr-Schutz auch hier: der eigene Account behält die Admin-Rolle.
      const safeRoles = user.id === ownId && !roles.includes('Admin') ? [...roles, 'Admin'] : roles
      updateMut.mutate({ id: user.id, req: { roles: safeRoles, email: user.email, displayName: user.displayName } })
    }
    setConfirm(null)
  }

  return (
    <>
      <h2>{t('users:title')}</h2>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <button className="primary" onClick={() => setShowCreate(true)}>{t('users:new')}</button>
      </div>

      <ErrorBanner error={updateMut.error} title={t('users:saveFailed')} onDismiss={updateMut.reset} />
      <ErrorBanner error={toggleMut.error} title={t('users:toggleFailed')} onDismiss={toggleMut.reset} />

      <div className="table-wrap">
      <table style={{ width: '100%' }}>
        <caption className="visually-hidden">{t('users:caption')}</caption>
        <thead>
          <tr>
            <th scope="col">{t('users:col.username')}</th><th scope="col">{t('users:col.displayName')}</th><th scope="col">{t('users:col.email')}</th><th scope="col">{t('users:col.roles')}</th>
            <th scope="col">{t('users:col.lastLogin')}</th><th scope="col">{t('users:col.status')}</th><th scope="col">{t('users:col.actions')}</th>
          </tr>
        </thead>
        <tbody>
          {data.map((u) => (
            <UserRow
              key={u.id}
              user={u}
              isSelf={u.id === ownId}
              pending={actionPending}
              onSave={(roles) => setConfirm({ kind: 'roles', user: u, roles })}
              onReset={() => { resetMut.reset(); setResetFor(u) }}
              onToggle={() => (u.isActive ? setConfirm({ kind: 'deactivate', user: u }) : toggleMut.mutate({ id: u.id, active: true }))}
            />
          ))}
        </tbody>
      </table>
      </div>

      {showCreate && (
        <CreateUserDialog
          onCancel={() => setShowCreate(false)}
          onCreate={(req) => createMut.mutate(req, { onSuccess: () => setShowCreate(false) })}
          pending={createMut.isPending}
          error={createMut.error}
        />
      )}

      {resetFor && (
        <ResetPasswordDialog
          user={resetFor}
          onCancel={() => { resetMut.reset(); setResetFor(null) }}
          onConfirm={(pw) =>
            resetMut.mutate({ id: resetFor.id, req: { newPassword: pw, mustChangeOnNextLogin: true } }, { onSuccess: () => setResetFor(null) })
          }
          pending={resetMut.isPending}
          error={resetMut.error}
        />
      )}

      <ConfirmDialog
        open={confirm !== null}
        title={confirm?.kind === 'deactivate' ? t('users:confirm.deactivateTitle') : t('users:confirm.rolesTitle')}
        confirmLabel={confirm?.kind === 'deactivate' ? t('users:deactivate') : t('users:confirm.saveRoles')}
        danger={confirm?.kind === 'deactivate'}
        onConfirm={runConfirmed}
        onCancel={() => setConfirm(null)}
      >
        {confirm?.kind === 'deactivate' ? (
          <p><Trans i18nKey="users:confirm.deactivateBody" values={{ name: confirm.user.username }} components={{ strong: <strong /> }} /></p>
        ) : (
          <p><Trans i18nKey="users:confirm.rolesBody" values={{ name: confirm?.user.username, roles: confirm?.roles.join(', ') || t('users:confirm.noRole') }} components={{ strong: <strong /> }} /></p>
        )}
      </ConfirmDialog>
    </>
  )
}

function UserRow({ user, isSelf, pending, onSave, onReset, onToggle }: {
  user: UserDto
  /** Der eigene Account: nicht deaktivierbar, Admin-Rolle nicht entziehbar. */
  isSelf: boolean
  pending: boolean
  onSave: (roles: string[]) => void
  onReset: () => void
  onToggle: () => void
}) {
  const { t } = useTranslation()
  const [roles, setRoles] = useState(new Set(user.roles))
  const dirty = roles.size !== user.roles.length || user.roles.some((r) => !roles.has(r))
  const toggle = (r: string) => setRoles((s) => {
    const n = new Set(s)
    if (n.has(r)) n.delete(r); else n.add(r)
    return n
  })
  return (
    <tr style={{ opacity: user.isActive ? 1 : 0.5 }}>
      <td><strong>{user.username}</strong>{isSelf && <span className="muted"> {t('users:row.you')}</span>}</td>
      <td>{user.displayName ?? <span className="muted">—</span>}</td>
      <td>{user.email ?? <span className="muted">—</span>}</td>
      <td>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {ALL_ROLES.map((r) => (
            <label key={r} className={roles.has(r) ? 'pill pill--info' : 'pill pill--neutral'} style={{ fontSize: 11, padding: '2px 6px', flexDirection: 'row', gap: 0 }}>
              <input
                type="checkbox" checked={roles.has(r)} onChange={() => toggle(r)} style={{ marginRight: 4 }}
                disabled={isSelf && r === 'Admin'}
                title={isSelf && r === 'Admin' ? t('users:row.selfAdminTip') : undefined}
              />
              {r}
            </label>
          ))}
        </div>
      </td>
      <td className="muted" style={{ fontSize: 12 }}>
        {formatDateTime(user.lastLoginAt, { fallback: t('users:row.never') })}
      </td>
      <td>{user.isActive ? t('users:row.active') : t('users:row.inactive')}{user.mustChangePassword ? t('users:row.pwChange') : ''}</td>
      <td style={{ whiteSpace: 'nowrap' }}>
        {dirty && <button onClick={() => onSave(Array.from(roles))} className="primary" disabled={pending}>{t('common:save')}</button>}
        <button onClick={onReset} style={{ marginLeft: 4 }}>{t('users:row.pwReset')}</button>
        <button
          onClick={onToggle}
          disabled={pending || (isSelf && user.isActive)}
          title={isSelf && user.isActive ? t('users:row.selfDeactivateTip') : undefined}
          style={{ marginLeft: 4 }}
        >
          {user.isActive ? t('users:deactivate') : t('users:activate')}
        </button>
      </td>
    </tr>
  )
}

function CreateUserDialog({ onCancel, onCreate, pending, error }: {
  onCancel: () => void
  onCreate: (req: { username: string; password: string; roles: string[]; email?: string; displayName?: string }) => void
  pending: boolean
  error: unknown
}) {
  const { t } = useTranslation()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [email, setEmail] = useState('')
  const [roles, setRoles] = useState(new Set(['Viewer']))
  return (
    <Modal title={t('users:create.title')} width={600} onClose={onCancel} closeOnEscape={!pending}>
      <div className="grid-2">
        <label>{t('users:col.username')}<input value={username} onChange={(e) => setUsername(e.target.value)} data-autofocus /></label>
        <label>{t('users:create.initialPassword', { min: PASSWORD_MIN_LENGTH })}<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
        <label>{t('users:col.displayName')}<input value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></label>
        <label>{t('users:col.email')}<input type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
      </div>
      <div style={{ marginTop: 12 }}>
        <span id="create-user-roles" className="muted" style={{ fontSize: 12 }}>{t('users:col.roles')}</span>
        <div role="group" aria-labelledby="create-user-roles" style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 4 }}>
          {ALL_ROLES.map((r) => (
            <label key={r} className={roles.has(r) ? 'pill pill--info' : 'pill pill--neutral'} style={{ padding: '4px 8px', flexDirection: 'row', gap: 0 }}>
              <input
                type="checkbox" checked={roles.has(r)}
                onChange={() => setRoles((s) => { const n = new Set(s); if (n.has(r)) n.delete(r); else n.add(r); return n })}
                style={{ marginRight: 4 }}
              />{r}
            </label>
          ))}
        </div>
      </div>
      <ErrorBanner error={error} title={t('users:create.failed')} style={{ marginTop: 8 }} />
      <div className="toolbar" style={{ marginTop: 16 }}>
        <button className="primary" disabled={pending || username.length < 3 || password.length < PASSWORD_MIN_LENGTH}
          onClick={() => onCreate({ username, password, roles: Array.from(roles), email: email || undefined, displayName: displayName || undefined })}>
          {pending ? t('common:saving') : t('users:create.submit')}
        </button>
        <button onClick={onCancel}>{t('common:cancel')}</button>
      </div>
    </Modal>
  )
}

function ResetPasswordDialog({ user, onCancel, onConfirm, pending, error }: {
  user: UserDto
  onCancel: () => void
  onConfirm: (pw: string) => void
  pending: boolean
  error: unknown
}) {
  const { t } = useTranslation()
  const [pw, setPw] = useState('')
  return (
    <ConfirmDialog
      open
      title={t('users:reset.title', { name: user.username })}
      confirmLabel={t('users:reset.label')}
      pending={pending}
      confirmDisabled={pw.length < PASSWORD_MIN_LENGTH}
      onConfirm={() => onConfirm(pw)}
      onCancel={onCancel}
    >
      <p className="muted">
        {t('users:reset.hint')}
      </p>
      <label>
        {t('users:reset.newPassword', { min: PASSWORD_MIN_LENGTH })}
        <input type="password" value={pw} onChange={(e) => setPw(e.target.value)} data-autofocus />
      </label>
      <ErrorBanner error={error} title={t('users:reset.failed')} style={{ marginTop: 8 }} />
    </ConfirmDialog>
  )
}
