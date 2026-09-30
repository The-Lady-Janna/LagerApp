import { describe, expect, it, vi } from 'vitest'
import routes from '../../features/system/route'
import { hasRequiredRole, type RoleName } from '../../lib/roles'
import { buildNavGroups, STATIC_NAV_GROUPS, visibleNavGroups } from '../../routes/navigation'
import { collectFeatureRoutes, featureRoutes, RESERVED_PATHS } from '../../routes/registry'
import { describeRetention, describeSchedule, formatBytes, isRestoreConfirmed } from '../../features/system/backupFormat'

/** hasRole wie im Auth-Store: Admin > Manager > Rolle. */
const asRoles = (roles: string[]) => (role: RoleName) => hasRequiredRole(roles, role)

describe('System-Seite: Anmeldung in der Route-Registry', () => {
  it('meldet /system in der Gruppe "System" nur für Admins an', () => {
    expect(routes).toHaveLength(1)
    expect(routes[0]).toMatchObject({ path: '/system', label: 'Backup & Restore', group: 'system', roles: ['Admin'] })

    const groups = buildNavGroups(STATIC_NAV_GROUPS, routes)
    const system = groups.find((g) => g.id === 'system')!
    expect(system.items.map((i) => i.to)).toEqual(['/users', '/system'])

    const forAdmin = visibleNavGroups(groups, asRoles(['Admin'])).find((g) => g.id === 'system')!
    expect(forAdmin.items.map((i) => i.label)).toEqual(['Benutzer', 'Backup & Restore'])
    // Die ganze Gruppe "System" ist Admin-Sache: Manager sehen weder sie noch den Eintrag
    expect(visibleNavGroups(groups, asRoles(['Manager'])).find((g) => g.id === 'system')).toBeUndefined()
  })

  it('wird von der echten Registry gefunden, ohne einen festen Pfad zu überschreiben', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})

    expect(RESERVED_PATHS).not.toContain('/system')
    expect(collectFeatureRoutes({ '../features/system/route.tsx': { default: routes } }, RESERVED_PATHS)).toHaveLength(1)
    expect(warn).not.toHaveBeenCalled()
    expect(featureRoutes.map((r) => r.path)).toContain('/system')
  })
})

describe('System-Seite: Formatierung und Bestätigung', () => {
  it('formatBytes: Größen mit deutschem Dezimalkomma, ungültige Werte als Strich', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1023)).toBe('1.023 B')
    expect(formatBytes(1536)).toBe('1,5 KB')
    expect(formatBytes(3 * 1024 * 1024)).toBe('3 MB')
    expect(formatBytes(1024 ** 3 * 1.25)).toBe('1,3 GB')
    expect(formatBytes(-1)).toBe('—')
    expect(formatBytes(Number.NaN)).toBe('—')
  })

  it('isRestoreConfirmed: nur genau "RESTORE" (Groß-/Kleinschreibung zählt, Leerraum außen egal)', () => {
    expect(isRestoreConfirmed('RESTORE')).toBe(true)
    expect(isRestoreConfirmed('  RESTORE ')).toBe(true)
    for (const wrong of ['', 'restore', 'Restore', 'RESTOR', 'RESTORE!', 'RESTORE RESTORE', 'JA']) expect(isRestoreConfirmed(wrong)).toBe(false)
  })

  it('beschreibt Zeitplan und Aufbewahrung in Worten', () => {
    expect(describeSchedule({ schedule: '02:00', scheduleValid: true, scheduleTimeZone: 'UTC' })).toBe('täglich 02:00 UTC')
    expect(describeSchedule({ schedule: null, scheduleValid: true, scheduleTimeZone: 'UTC' })).toBe('aus')
    expect(describeSchedule({ schedule: '25:99', scheduleValid: false, scheduleTimeZone: 'UTC' })).toBe('ungültig')
    expect(describeRetention(14)).toBe('die letzten 14 Backups')
    expect(describeRetention(0)).toBe('unbegrenzt')
  })
})
