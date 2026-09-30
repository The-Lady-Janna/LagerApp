import type { ReactNode } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useAuth } from '../state/auth'
import type { RoleName } from '../lib/roles'

interface Props {
  /** Nötige Rolle (mit der Hierarchie Admin > Manager > Rolle). */
  role?: RoleName
  /** Alternativ mehrere Rollen: es genügt eine davon (Routen-Registry: FeatureRoute.roles). */
  roles?: RoleName[]
  children: ReactNode
}

/// <summary>
/// Route-Guard: zeigt die Kinder nur, wenn der eingeloggte User die Rolle hat
/// (Admin > Manager > Rolle, siehe lib/roles.ts). Sonst erscheint ein
/// "Zugriff verweigert"-Hinweis statt der Seite. Das ist UX — der Server
/// erzwingt die Rechte unabhängig davon per 403. Ohne role/roles ist jeder
/// eingeloggte User zugelassen.
/// </summary>
export function RequireRole({ role, roles, children }: Props) {
  const { t } = useTranslation()
  const required = [...(role ? [role] : []), ...(roles ?? [])]
  const allowed = useAuth((s) => required.length === 0 || required.some((r) => s.hasRole(r)))
  if (allowed) return <>{children}</>
  return (
    <div className="card" role="alert">
      <h2 style={{ marginTop: 0 }}>{t('errors:denied.title')}</h2>
      <p>
        <Trans
          i18nKey={required.length === 1 ? 'errors:denied.oneRole' : 'errors:denied.anyRole'}
          values={{ roles: required.join(t('errors:denied.or')) }}
          components={{ strong: <strong /> }}
        />
      </p>
      <Link to="/">{t('common:toHome')}</Link>
    </div>
  )
}
