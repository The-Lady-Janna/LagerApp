import { describe, expect, it, vi } from 'vitest'
import { exportFileName, exportUrl, importUrl } from '../../api/importExportHooks'
import routes from '../../features/importexport/route'
import { applyBlockedReason, canApplyImport, errorKey, formatBytes, writableCount } from '../../features/importexport/importFormat'
import { hasRequiredRole, type RoleName } from '../../lib/roles'
import { buildNavGroups, STATIC_NAV_GROUPS, visibleNavGroups } from '../../routes/navigation'
import { collectFeatureRoutes, featureRoutes, RESERVED_PATHS } from '../../routes/registry'
import { makeResult, makeResultWithErrors } from './fixtures'

/** hasRole wie im Auth-Store: Admin > Manager > Rolle. */
const asRoles = (roles: string[]) => (role: RoleName) => hasRequiredRole(roles, role)

describe('CSV: Adressen der Endpunkte', () => {
  it('importUrl: alle Parameter im Query-String, skipErrors nur wenn gesetzt', () => {
    expect(importUrl({ kind: 'articles', delimiter: 'semicolon', dryRun: true })).toBe('/import/articles?dryRun=true&delimiter=semicolon')
    expect(importUrl({ kind: 'stock', delimiter: 'comma', dryRun: false, skipErrors: true })).toBe('/import/stock?dryRun=false&delimiter=comma&skipErrors=true')
    expect(importUrl({ kind: 'orders', delimiter: 'semicolon', dryRun: false, skipErrors: false })).toBe('/import/orders?dryRun=false&delimiter=semicolon')
  })

  it('exportUrl: Zeitraum nur für Bewegungen und Audit, Benutzer nur für Audit, leere Filter entfallen', () => {
    expect(exportUrl('articles', { delimiter: 'semicolon', from: '2026-01-01', user: 'x' })).toBe('/export/articles.csv?delimiter=semicolon')
    expect(exportUrl('movements', { delimiter: 'comma', from: '2026-09-01', to: '2026-09-30', user: 'x' }))
      .toBe('/export/movements.csv?delimiter=comma&from=2026-09-01&to=2026-09-30')
    expect(exportUrl('movements', { delimiter: 'semicolon', from: '', to: '' })).toBe('/export/movements.csv?delimiter=semicolon')
    expect(exportUrl('audit', { delimiter: 'semicolon', user: ' Max Muster ' })).toBe('/export/audit.csv?delimiter=semicolon&user=Max+Muster')
    expect(exportUrl('audit', { delimiter: 'semicolon', user: '   ' })).toBe('/export/audit.csv?delimiter=semicolon')
  })

  it('exportFileName: Dateiname aus Content-Disposition, sonst einer mit UTC-Zeit; nie ein Pfad', () => {
    expect(exportFileName('articles', 'attachment; filename="articles-20260930T101500Z.csv"')).toBe('articles-20260930T101500Z.csv')
    expect(exportFileName('articles', "attachment; filename*=UTF-8''stock%20neu.csv")).toBe('stock neu.csv')
    const now = new Date(Date.UTC(2026, 8, 30, 10, 15, 0))
    expect(exportFileName('audit', undefined, now)).toBe('audit-20260930T101500Z.csv')
    expect(exportFileName('audit', 'attachment', now)).toBe('audit-20260930T101500Z.csv')
    expect(exportFileName('audit', 'attachment; filename="../../etc/passwd"', now)).toBe('audit-20260930T101500Z.csv')
    expect(exportFileName('audit', 'attachment; filename="a\\b.csv"', now)).toBe('audit-20260930T101500Z.csv')
  })
})

describe('CSV: Entscheidung "Übernehmen"', () => {
  it('formatBytes: Größen mit deutschem Dezimalkomma, ungültige Werte als Strich', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1536)).toBe('1,5 KB')
    expect(formatBytes(5 * 1024 * 1024)).toBe('5 MB')
    expect(formatBytes(-1)).toBe('—')
    expect(formatBytes(Number.NaN)).toBe('—')
  })

  it('übernehmen geht nur nach einem Trockenlauf mit etwas zu schreiben und ohne offene Fehler', () => {
    expect(canApplyImport(null, false)).toBe(false)
    expect(canApplyImport(makeResult(), false)).toBe(true)
    expect(canApplyImport(makeResult({ dryRun: false, applied: true }), false)).toBe(false)           // eine Übernahme ist keine Prüfung
    expect(canApplyImport(makeResult({ created: 0, updated: 0, unchanged: 4 }), false)).toBe(false)   // nichts zu schreiben

    const withErrors = makeResultWithErrors()
    expect(canApplyImport(withErrors, false)).toBe(false)                                            // Fehler: erst korrigieren ...
    expect(canApplyImport(withErrors, true)).toBe(true)                                              // ... oder ausdrücklich auslassen
    expect(canApplyImport(makeResult({ created: 0, updated: 0, errorCount: 3 }), true)).toBe(false)  // alles fehlerhaft: auch mit Auslassen nichts
  })

  it('nennt den Grund, warum Übernehmen gesperrt ist', () => {
    expect(applyBlockedReason(null, false)).toBe('Zuerst die Datei prüfen.')
    expect(applyBlockedReason(makeResult(), false)).toBeNull()
    expect(applyBlockedReason(makeResultWithErrors(), false)).toContain('Zeilenfehler')
    expect(applyBlockedReason(makeResultWithErrors(), true)).toBeNull()
    expect(applyBlockedReason(makeResult({ created: 0, updated: 0, errorCount: 2 }), true)).toContain('fehlerfrei')
    expect(applyBlockedReason(makeResult({ created: 0, updated: 0, unchanged: 5 }), false)).toContain('nichts zu übernehmen')
  })

  it('writableCount und errorKey', () => {
    expect(writableCount({ created: 2, updated: 3 })).toBe(5)
    expect(errorKey('SKU-1')).toBe('SKU-1')
    expect(errorKey(null)).toBe('—')
    expect(errorKey('  ')).toBe('—')
  })
})

describe('CSV: Anmeldung in der Route-Registry', () => {
  it('meldet /import-export für Manager an (Navigation und Route), nicht für Rollen darunter', () => {
    expect(routes).toHaveLength(1)
    expect(routes[0]).toMatchObject({ path: '/import-export', label: 'Import & Export', roles: ['Manager'] })

    const groups = buildNavGroups(STATIC_NAV_GROUPS, routes)
    const labelsFor = (roles: string[]) =>
      visibleNavGroups(groups, asRoles(roles)).flatMap((g) => g.items.map((i) => i.label))
    expect(labelsFor(['Manager'])).toContain('Import & Export')
    expect(labelsFor(['Admin'])).toContain('Import & Export')
    for (const role of ['Viewer', 'Picker', 'Packer', 'Receiver']) expect(labelsFor([role])).not.toContain('Import & Export')
  })

  it('liegt in einer Gruppe, die Manager sehen (die Gruppe "System" ist Admins vorbehalten)', () => {
    const groupId = routes[0].group
    const group = STATIC_NAV_GROUPS.find((g) => g.id === groupId)
    expect(group).toBeDefined()
    expect(group?.roles).toBeUndefined()
  })

  it('wird von der echten Registry gefunden, ohne einen festen Pfad zu überschreiben', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})

    expect(RESERVED_PATHS).not.toContain('/import-export')
    expect(collectFeatureRoutes({ '../features/importexport/route.tsx': { default: routes } }, RESERVED_PATHS)).toHaveLength(1)
    expect(warn).not.toHaveBeenCalled()
    expect(featureRoutes.map((r) => r.path)).toContain('/import-export')
  })
})
