import type { RoleName } from '../lib/roles'
import type { FeatureRoute } from './registry'

// `roles`: Rollen, von denen mindestens eine nötig ist (Admin > Manager > Rolle, wie bei den Routen-Guards).
// Ohne Angabe sieht den Eintrag jeder eingeloggte User.
export interface NavItem { to: string; label: string; roles?: RoleName[] }
export interface NavGroup { id: string; label: string; items: NavItem[]; roles?: RoleName[] }

/// <summary>
/// Die feste Sidebar-Navigation der bestehenden Seiten. Neue Features tragen sich NICHT hier ein, sondern über
/// src/features/<name>/route.tsx (routes/registry.ts) — buildNavGroups fügt sie in die Gruppen ein.
/// </summary>
export const STATIC_NAV_GROUPS: NavGroup[] = [
  {
    id: 'stammdaten',
    label: 'Stammdaten',
    items: [
      { to: '/articles', label: 'Artikel' },
      { to: '/suppliers', label: 'Lieferanten' },
      { to: '/customers', label: 'Kunden' },
      { to: '/layout', label: 'Lager-Layout' },
      { to: '/cart-configs', label: 'Pickwagen', roles: ['Manager'] },
    ],
  },
  {
    id: 'wareneingang',
    label: 'Wareneingang',
    items: [
      { to: '/inbound', label: 'Wareneingang' },
      { to: '/purchase-orders', label: 'Beschaffung' },
      { to: '/replenishment', label: 'Replenishment' },
    ],
  },
  {
    id: 'bestand',
    label: 'Bestand',
    items: [
      { to: '/stock', label: 'Bestand' },
      { to: '/inventory', label: 'Inventur' },
    ],
  },
  {
    id: 'auslieferung',
    label: 'Auslieferung',
    items: [
      { to: '/orders', label: 'Bestellungen' },
      { to: '/picklists', label: 'Picklisten' },
      { to: '/waves', label: 'Wellen' },
      { to: '/packing', label: 'Packen' },
      { to: '/shipments', label: 'Versand' },
      { to: '/returns', label: 'Retouren' },
    ],
  },
  {
    id: 'auswertung',
    label: 'Auswertung',
    items: [
      { to: '/reports', label: 'Reports' },
      { to: '/audit', label: 'Audit', roles: ['Manager'] },
    ],
  },
  {
    id: 'system',
    label: 'System',
    roles: ['Admin'],
    items: [
      { to: '/users', label: 'Benutzer' },
    ],
  },
]

/** Gruppe für Feature-Einträge, die keine angeben. */
const FALLBACK_GROUP = { id: 'weitere', label: 'Weitere' }

/**
 * Fügt die Feature-Einträge in die feste Navigation ein: in die Gruppe mit der Id `group` (hinter die bestehenden
 * Einträge, nach `order`), oder — wenn es sie nicht gibt — in eine neue Gruppe am Ende. Einträge ohne Label
 * (reine Routen) erscheinen nicht. Die Eingabe bleibt unverändert.
 */
export function buildNavGroups(base: readonly NavGroup[], features: readonly FeatureRoute[]): NavGroup[] {
  const groups: NavGroup[] = base.map((g) => ({ ...g, items: [...g.items] }))
  for (const feature of features) {
    if (!feature.label) continue
    const id = feature.group ?? FALLBACK_GROUP.id
    let group = groups.find((g) => g.id === id)
    if (!group) {
      group = { id, label: feature.groupLabel ?? (id === FALLBACK_GROUP.id ? FALLBACK_GROUP.label : id), items: [] }
      groups.push(group)
    }
    group.items.push({ to: feature.path, label: feature.label, roles: feature.roles })
  }
  return groups
}

/** Die für diese Rollenprüfung sichtbaren Gruppen/Einträge; Gruppen ohne sichtbaren Eintrag entfallen. */
export function visibleNavGroups(groups: readonly NavGroup[], hasRole: (role: RoleName) => boolean): NavGroup[] {
  const allowed = (roles?: RoleName[]) => !roles || roles.length === 0 || roles.some(hasRole)
  return groups
    .filter((g) => allowed(g.roles))
    .map((g) => ({ ...g, items: g.items.filter((item) => allowed(item.roles)) }))
    .filter((g) => g.items.length > 0)
}

/**
 * Gruppe der aktiven Route: der Eintrag mit dem längsten gemeinsamen Pfad-Präfix
 * (z. B. /picklists/abc-123 → /picklists).
 */
export function activeGroupId(groups: readonly NavGroup[], currentPath: string): string | null {
  let best: { id: string; len: number } | null = null
  for (const g of groups) {
    for (const item of g.items) {
      if (currentPath === item.to || currentPath.startsWith(item.to + '/')) {
        if (!best || item.to.length > best.len) best = { id: g.id, len: item.to.length }
      }
    }
  }
  return best?.id ?? null
}
