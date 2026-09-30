import { describe, expect, it } from 'vitest'
import { STATIC_NAV_GROUPS, buildNavGroups, visibleNavGroups } from '../../routes/navigation'
import { RESERVED_PATHS, collectFeatureRoutes, featureRoutes } from '../../routes/registry'
import { hasRequiredRole } from '../../lib/roles'
import warehouseRoutes from '../../features/warehouses/route'
import {
  MAX_DIMENSION_MM, parseDimension, parseKilogramsAsGrams, parseThreshold, parseWholeNumber,
} from '../../features/warehouses/formFields'
import {
  allBinCodes, compareCodes, countInWarehouse, describeCounts, nextFreeBinCode, nextFreeCode, sortByCode,
} from '../../features/warehouses/treeModel'
import { makeAisle, makeBin, makeShelf, makeTree, makeWarehouse, makeZone } from './fixtures'

describe('Lagerstruktur: Zählungen und Codes', () => {
  it('zählt, was unter einem Lager hängt, und beschreibt es in Klartext (Einzahl/Mehrzahl, nur Vorhandenes)', () => {
    const counts = countInWarehouse(makeTree()[0])
    expect(counts).toEqual({ zones: 1, aisles: 1, shelves: 1, bins: 2 })
    expect(describeCounts(counts)).toBe('1 Zone, 1 Gang, 1 Regal, 2 Lagerplätze')

    const big = makeWarehouse('w', 'W', 'W', [
      makeZone('z1', 'Z1', 'Z1', [makeAisle('a1', 'A1', [makeShelf('s1', 'S1'), makeShelf('s2', 'S2', [makeBin('b', 'B')])]), makeAisle('a2', 'A2')]),
      makeZone('z2', 'Z2', 'Z2'),
    ])
    expect(describeCounts(countInWarehouse(big))).toBe('2 Zonen, 2 Gänge, 2 Regale, 1 Lagerplatz')
    expect(describeCounts({ zones: 0, aisles: 0, shelves: 0, bins: 0 })).toBe('')
  })

  it('sortiert Codes mit Zahlen numerisch und ohne Groß-/Kleinschreibung', () => {
    const codes = ['A1-10', 'a1-2', 'A1-01', 'B-1'].map((code) => ({ code }))
    expect(sortByCode(codes).map((c) => c.code)).toEqual(['A1-01', 'a1-2', 'A1-10', 'B-1'])
    expect(compareCodes('S-2', 'S-10')).toBeLessThan(0)
  })

  it('schlägt den nächsten freien Lagerplatz-Code vor und überspringt Codes anderer Regale', () => {
    expect(nextFreeBinCode('S1', [], [])).toBe('S1-01')
    expect(nextFreeBinCode('S1', ['S1-01', 'S1-02'], ['S1-01', 'S1-02'])).toBe('S1-03')
    // hinter der höchsten Nummer des Regals (Lücken bleiben Lücken)
    expect(nextFreeBinCode('S1', ['S1-01', 'S1-05'], ['S1-01', 'S1-05'])).toBe('S1-06')
    // ein anderes Regal trägt S1-03 schon (Codes sind im ganzen System eindeutig, ohne Groß-/Kleinschreibung)
    expect(nextFreeBinCode('S1', ['S1-01', 'S1-02'], ['S1-01', 'S1-02', 's1-03', 'S1-04'])).toBe('S1-05')
  })

  it('schlägt den nächsten freien Zonen-/Gang-Code vor', () => {
    expect(nextFreeCode('Z', [])).toBe('Z1')
    expect(nextFreeCode('Z', ['Z1', 'z2', 'Z-A'])).toBe('Z3')
  })

  it('sammelt alle Lagerplatz-Codes aller Lager', () => {
    const other = makeWarehouse('w2', 'WH02', 'Zweit', [makeZone('z2', 'Z1', 'Z', [makeAisle('a2', 'A1', [makeShelf('s2', 'S9', [makeBin('b9', 'S9-01')])])])])
    expect(allBinCodes([...makeTree(), other]).sort()).toEqual(['S1-01', 'S1-02', 'S9-01'])
  })
})

describe('Lagerstruktur: Eingabefelder', () => {
  it('liest Abmessungen (1..100000 mm, ganze Zahlen), Kilogramm und Schwellen streng', () => {
    expect(parseDimension('600')).toBe(600)
    expect(parseDimension(' 1 ')).toBe(1)
    expect(parseDimension(String(MAX_DIMENSION_MM))).toBe(MAX_DIMENSION_MM)
    for (const bad of ['0', '-5', '100001', '12.5', '', 'abc']) expect(parseDimension(bad)).toBeNull()

    expect(parseKilogramsAsGrams('50')).toBe(50_000)
    expect(parseKilogramsAsGrams('1,5')).toBe(1_500)
    expect(parseKilogramsAsGrams('0')).toBe(0)
    for (const bad of ['-1', '', 'x', '100001']) expect(parseKilogramsAsGrams(bad)).toBeNull()

    expect(parseThreshold('0')).toBe(0)
    expect(parseThreshold('10')).toBe(10)
    for (const bad of ['-1', '1.5', '', '1000001']) expect(parseThreshold(bad)).toBeNull()
    expect(parseWholeNumber('-3')).toBe(-3)
  })
})

describe('Lagerstruktur: Anmeldung der Seite (Route-Registry)', () => {
  it('meldet /warehouses in der Gruppe Stammdaten für Manager an, ohne einen festen Pfad zu belegen', () => {
    const [route] = warehouseRoutes
    expect(route).toMatchObject({ path: '/warehouses', label: 'Lagerstruktur', group: 'stammdaten', roles: ['Manager'] })
    expect(RESERVED_PATHS).not.toContain('/warehouses')

    // Die Registry sammelt sie (per Glob über features/*/route.tsx) ein ...
    expect(featureRoutes.map((r) => r.path)).toContain('/warehouses')
    expect(collectFeatureRoutes({ '../features/warehouses/route.tsx': { default: warehouseRoutes } }, RESERVED_PATHS)).toHaveLength(1)

    // ... die Sidebar zeigt sie in "Stammdaten" nur Managern und Admins.
    const groups = buildNavGroups(STATIC_NAV_GROUPS, warehouseRoutes)
    const labelsFor = (roles: string[]) =>
      visibleNavGroups(groups, (role) => hasRequiredRole(roles, role)).find((g) => g.id === 'stammdaten')!.items.map((i) => i.label)
    expect(labelsFor(['Manager'])).toContain('Lagerstruktur')
    expect(labelsFor(['Admin'])).toContain('Lagerstruktur')
    expect(labelsFor(['Viewer'])).not.toContain('Lagerstruktur')
    expect(labelsFor(['Picker'])).not.toContain('Lagerstruktur')
  })
})
