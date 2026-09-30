import { useMemo, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { NavLink } from 'react-router-dom'
import { activeGroupId, visibleNavGroups, type NavGroup } from '../routes/navigation'
import { useAuth } from '../state/auth'
import { useTheme, type Theme } from '../state/theme'
import { ChangePasswordDialog } from './ChangePasswordDialog'
import { LanguageSwitcher } from './LanguageSwitcher'
import { WarehouseSelector } from './WarehouseSelector'

// ---------------------------------------------------------------------------
// Gruppierte Sidebar-Navigation
//
// Bei ~20 Items wird eine flache Liste unübersichtlich. Items werden nach
// Workflow gruppiert, jede Gruppe ist auf-/zuklappbar. Der Auf/Zu-Status
// landet in localStorage, damit jeder seinen Lieblings-Workflow-Bereich
// dauerhaft offen lassen kann. Die Gruppe der aktiven Route wird zusätzlich
// automatisch ausgeklappt — sonst wäre der Link beim Wechseln nicht sichtbar.
// Die Gruppen kommen aus routes/navigation.ts (feste Liste + Feature-Registry).
// ---------------------------------------------------------------------------

const STORAGE_KEY = 'lager:sidebarOpenGroups'

function loadOpenState(): Record<string, boolean> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return {}
    return JSON.parse(raw) as Record<string, boolean>
  } catch {
    return {}
  }
}

interface SidebarProps {
  /** DOM-Id (Ziel von aria-controls des Hamburger-Knopfs). */
  id?: string
  groups: NavGroup[]
  currentPath: string
  ref?: React.Ref<HTMLElement>
}

/// <summary>
/// Die Seitenleiste: Logo, Lager-Auswahl, Navigation (gefiltert nach Rollen), Suchhinweis und Benutzer-Fuß.
/// Unter 900 px Breite ist sie ein ausklappbarer Drawer (index.css); das Ein-/Ausblenden steuert App.tsx.
/// </summary>
export function Sidebar({ id, groups, currentPath, ref }: SidebarProps) {
  const { t } = useTranslation()
  return (
    <aside id={id} ref={ref} className="sidebar" aria-label={t('nav:sidebar')}>
      <h1>Lager</h1>
      <WarehouseSelector />
      <SidebarNav groups={groups} currentPath={currentPath} />
      <div className="sidebar-hint">
        🔍 Cmd/Ctrl+K
      </div>
      <UserFooter />
    </aside>
  )
}

/** Schlüssel-Teil einer Route für die Navigationstexte: "/cart-configs" → "cart-configs". */
function routeKey(path: string): string {
  return path.replace(/^\/+/, '').replace(/\//g, '-')
}

function SidebarNav({ groups, currentPath }: { groups: NavGroup[]; currentPath: string }) {
  const { t } = useTranslation()
  const hasRole = useAuth((s) => s.hasRole)
  // Beim ersten Mount aus localStorage laden. Default: alle Gruppen offen —
  // ein leeres Object bedeutet "keine Info, also auf".
  const [openState, setOpenState] = useState<Record<string, boolean>>(loadOpenState)

  const activeId = useMemo(() => activeGroupId(groups, currentPath), [groups, currentPath])
  const visible = visibleNavGroups(groups, hasRole)

  const isOpen = (id: string) => {
    if (id === activeId) return true  // aktive Gruppe immer auf
    const v = openState[id]
    return v === undefined ? true : v
  }

  const toggle = (id: string) => {
    setOpenState((prev) => {
      const next = { ...prev, [id]: !isOpen(id) }
      try { localStorage.setItem(STORAGE_KEY, JSON.stringify(next)) } catch { /* quota */ }
      return next
    })
  }

  return (
    <nav aria-label={t('nav:main')}>
      {visible.map((g) => {
        const open = isOpen(g.id)
        const itemsId = `nav-group-${g.id}`
        return (
          <div key={g.id} className="nav-group">
            <button
              type="button"
              className="nav-group-header"
              onClick={() => toggle(g.id)}
              aria-expanded={open}
              aria-controls={open ? itemsId : undefined}
            >
              <span className="nav-group-caret" aria-hidden="true">{open ? '▾' : '▸'}</span>
              <span>{t(`nav:groups.${g.id}`, { defaultValue: g.label })}</span>
            </button>
            {open && (
              <div className="nav-group-items" id={itemsId}>
                {g.items.map((item) => (
                  // NavLink setzt bei der aktiven Route aria-current="page" und die Klasse "active".
                  <NavLink key={item.to} to={item.to}>{t(`nav:routes.${routeKey(item.to)}`, { defaultValue: item.label })}</NavLink>
                ))}
              </div>
            )}
          </div>
        )
      })}
    </nav>
  )
}

/// <summary>
/// Footer im Sidebar zeigt eingeloggten User + Theme-Wahl + Passwort-ändern-Link + Logout.
/// Der Pflicht-Passwortwechsel (`mustChangePassword=true` bzw. 403
/// "password_change_required") wird VOR der App-Shell abgefangen
/// (components/ForcePasswordChange.tsx); hier ist der Dialog immer freiwillig.
/// </summary>
function UserFooter() {
  const user = useAuth((s) => s.user)
  const logout = useAuth((s) => s.logout)
  const theme = useTheme((s) => s.theme)
  const setTheme = useTheme((s) => s.setTheme)
  const [showPwDialog, setShowPwDialog] = useState(false)
  const { t } = useTranslation()

  if (!user) return null
  return (
    <div className="sidebar-footer">
      <div className="sidebar-user">
        <Trans i18nKey="nav:footer.loggedInAs" values={{ name: user.displayName ?? user.username }} components={{ strong: <strong /> }} />
      </div>
      <div className="sidebar-roles">
        {user.roles.join(' · ') || t('nav:footer.noRole')}
      </div>
      <div className="theme-switch" role="group" aria-label={t('nav:theme.label')}>
        {(['light', 'dark', 'auto'] as Theme[]).map((mode) => (
          <button
            key={mode}
            type="button"
            onClick={() => setTheme(mode)}
            title={t('nav:theme.title', { name: t(`nav:theme.names.${mode}`) })}
            aria-pressed={theme === mode}
          >
            {t(`nav:theme.short.${mode}`)}
          </button>
        ))}
      </div>
      <LanguageSwitcher />
      <button type="button" className="sidebar-button" style={{ marginTop: 8 }} onClick={() => setShowPwDialog(true)}>
        {t('nav:footer.changePassword')}
      </button>
      <button type="button" className="sidebar-button" onClick={() => logout()}>
        {t('common:logout')}
      </button>
      {showPwDialog && <ChangePasswordDialog onClose={() => setShowPwDialog(false)} />}
    </div>
  )
}
