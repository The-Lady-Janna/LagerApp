import { createElement } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { hasRequiredRole, type RoleName } from '../../lib/roles'
import { activeGroupId, buildNavGroups, STATIC_NAV_GROUPS, visibleNavGroups } from '../../routes/navigation'
import { collectFeatureRoutes, RESERVED_PATHS, type FeatureRoute, type FeatureRouteModule } from '../../routes/registry'

const Page = () => createElement('div')
const route = (overrides: Partial<FeatureRoute> = {}): FeatureRoute => ({ path: '/demo', element: Page, ...overrides })
const modules = (entries: Record<string, FeatureRoute | FeatureRoute[] | undefined>): Record<string, FeatureRouteModule> =>
  Object.fromEntries(Object.entries(entries).map(([file, value]) => [file, { default: value }]))

/** hasRole wie im Auth-Store: Admin > Manager > Rolle. */
const asRoles = (roles: string[]) => (role: RoleName) => hasRequiredRole(roles, role)

describe('Route-Registry: Einsammeln der Feature-Routen', () => {
  it('sammelt Einträge (einzeln oder als Array) aller Module ein und sortiert nach order, dann Label', () => {
    const routes = collectFeatureRoutes(modules({
      '../features/b/route.tsx': [route({ path: '/b2', label: 'Zeta', order: 10 }), route({ path: '/b1', label: 'Alpha', order: 10 })],
      '../features/a/route.tsx': route({ path: '/a', label: 'Erste', order: 1 }),
      '../features/c/route.tsx': route({ path: '/c', label: 'Ohne Order' }),
    }))

    expect(routes.map((r) => r.path)).toEqual(['/a', '/b1', '/b2', '/c'])
  })

  it('überspringt ungültige Einträge, vergebene Pfade und feste Seiten mit einer Warnung, statt die App zu brechen', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})

    const routes = collectFeatureRoutes(modules({
      '../features/a/route.tsx': route({ path: '/ok' }),
      '../features/b/route.tsx': route({ path: '/ok' }),          // schon vom ersten Feature vergeben
      '../features/c/route.tsx': route({ path: '/orders' }),      // feste Seite
      '../features/d/route.tsx': { path: 'ohne-slash', element: Page } as FeatureRoute,
      '../features/e/route.tsx': { path: '/kein-element' } as unknown as FeatureRoute,
      '../features/f/route.tsx': undefined,
    }), RESERVED_PATHS)

    expect(routes.map((r) => r.path)).toEqual(['/ok'])
    expect(warn).toHaveBeenCalledTimes(4)
    expect(warn.mock.calls.map((c) => String(c[0])).join('\n')).toContain('/orders')
  })

  it('kennt alle festen Seiten aus App.tsx als reserviert', () => {
    for (const path of ['/articles', '/orders/:id', '/picklists/:id/pack', '/replenishment']) expect(RESERVED_PATHS).toContain(path)
  })
})

describe('Navigation aus fester Liste plus Registry', () => {
  it('fügt Feature-Einträge in die Gruppe mit passender Id ein (hinter die festen), unbekannte Gruppen entstehen am Ende', () => {
    const groups = buildNavGroups(STATIC_NAV_GROUPS, [
      route({ path: '/labels', label: 'Etiketten', group: 'bestand', order: 1 }),
      route({ path: '/import', label: 'Import', group: 'daten', groupLabel: 'Daten' }),
      route({ path: '/misc', label: 'Sonstiges' }),
    ])

    const bestand = groups.find((g) => g.id === 'bestand')!
    expect(bestand.items.map((i) => i.label)).toEqual(['Bestand', 'Inventur', 'Etiketten'])
    expect(groups.at(-2)).toMatchObject({ id: 'daten', label: 'Daten' })
    expect(groups.at(-1)).toMatchObject({ id: 'weitere', label: 'Weitere' })
    expect(groups.at(-1)!.items).toEqual([{ to: '/misc', label: 'Sonstiges', roles: undefined }])
  })

  it('zeigt Routen ohne Label (Detailseiten) nicht in der Navigation und lässt die statische Liste unverändert', () => {
    const before = JSON.stringify(STATIC_NAV_GROUPS)

    const groups = buildNavGroups(STATIC_NAV_GROUPS, [route({ path: '/labels/:id', group: 'bestand' })])

    expect(groups.flatMap((g) => g.items).map((i) => i.to)).not.toContain('/labels/:id')
    expect(JSON.stringify(STATIC_NAV_GROUPS)).toBe(before)
  })

  it('filtert nach Rollen: Feature-Einträge mit roles erscheinen nur für berechtigte Nutzer, leere Gruppen entfallen', () => {
    const groups = buildNavGroups(STATIC_NAV_GROUPS, [
      route({ path: '/backup', label: 'Backup', group: 'wartung', groupLabel: 'Wartung', roles: ['Admin'] }),
      route({ path: '/labels', label: 'Etiketten', group: 'bestand', roles: ['Manager', 'Receiver'] }),
    ])
    const labelsFor = (roles: string[]) => visibleNavGroups(groups, asRoles(roles)).flatMap((g) => g.items.map((i) => i.label))

    expect(labelsFor(['Admin'])).toEqual(expect.arrayContaining(['Backup', 'Etiketten', 'Benutzer']))
    expect(labelsFor(['Manager'])).toContain('Etiketten')
    expect(labelsFor(['Manager'])).not.toContain('Backup')
    expect(labelsFor(['Receiver'])).toContain('Etiketten')       // eine der Rollen genügt
    expect(labelsFor(['Viewer'])).not.toContain('Etiketten')
    expect(visibleNavGroups(groups, asRoles(['Viewer'])).map((g) => g.id)).not.toContain('wartung')   // Gruppe ohne sichtbaren Eintrag entfällt
    expect(visibleNavGroups(groups, asRoles(['Viewer'])).map((g) => g.id)).not.toContain('system')    // feste Admin-Gruppe wie bisher
  })

  it('bestimmt die aktive Gruppe über den längsten Pfad-Präfix, auch für Feature-Detailseiten', () => {
    const groups = buildNavGroups(STATIC_NAV_GROUPS, [route({ path: '/labels', label: 'Etiketten', group: 'bestand' })])

    expect(activeGroupId(groups, '/labels/abc-1')).toBe('bestand')
    expect(activeGroupId(groups, '/picklists/x')).toBe('auslieferung')
    expect(activeGroupId(groups, '/unbekannt')).toBeNull()
  })
})
