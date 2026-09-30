import { describe, expect, it } from 'vitest'
import {
  EMPTY_BIN_FILTER, articleLabel, binLabel, compareCodes, filterBins, flattenBins, matchesQuery, orderLabel, parseIds, parseKind,
  selectedLabels,
} from '../../features/labels/labelSelection'
import { makeArticle } from '../helpers/fixtures'
import { makeLayout } from './labelFixtures'

describe('Sortierung und Suche', () => {
  it('sortiert Codes natürlich: A-01-2 vor A-01-10, Groß-/Kleinschreibung nachrangig', () => {
    expect(['A-01-10', 'A-01-2', 'a-01-1', 'B-1'].sort(compareCodes)).toEqual(['a-01-1', 'A-01-2', 'A-01-10', 'B-1'])
    expect(compareCodes('X', 'X')).toBe(0)
  })

  it('sucht ohne Beachtung von Groß-/Kleinschreibung und Leerraum am Rand; ohne Suche passt alles', () => {
    expect(matchesQuery(['Schraube M8', null], '  schr ')).toBe(true)
    expect(matchesQuery([undefined, 'ART-1'], 'art-2')).toBe(false)
    expect(matchesQuery(['x'], '')).toBe(true)
  })
})

describe('Lagerplätze wählen (Sammeldruck)', () => {
  const rows = flattenBins(makeLayout())

  it('holt alle Lagerplätze aus dem Layout, sortiert nach Code, mit Regal, Gang und Lager', () => {
    expect(rows.map((r) => r.code)).toEqual(['A-01-1', 'A-01-2', 'A-01-10', 'A-02-1', 'B-01-1', 'C-01-1'])
    expect(rows[0]).toMatchObject({ id: 'b1', shelfCode: 'S1', aisleCode: 'A', warehouseCode: 'WH1', shelfId: 's1', aisleId: 'a1', warehouseId: 'w1' })
  })

  it('filtert nach Regal, Gang oder Lager (das genauere gewinnt) und nach Suchtext', () => {
    const codes = (filter: Partial<typeof EMPTY_BIN_FILTER>) => filterBins(rows, { ...EMPTY_BIN_FILTER, ...filter }).map((r) => r.code)

    expect(codes({})).toHaveLength(6)
    expect(codes({ shelfId: 's1' })).toEqual(['A-01-1', 'A-01-2', 'A-01-10'])
    expect(codes({ aisleId: 'a1' })).toEqual(['A-01-1', 'A-01-2', 'A-01-10', 'A-02-1'])
    expect(codes({ warehouseId: 'w1' })).toEqual(['A-01-1', 'A-01-2', 'A-01-10', 'A-02-1', 'B-01-1'])
    expect(codes({ warehouseId: 'w2' })).toEqual(['C-01-1'])
    expect(codes({ aisleId: 'a1', shelfId: 's2' })).toEqual(['A-02-1'])
    expect(codes({ aisleId: 'a2', shelfId: 's1' })).toEqual([])                  // Regal gehört nicht zum Gang
    expect(codes({ query: '01-1' })).toEqual(['A-01-1', 'A-01-10', 'B-01-1', 'C-01-1'])
  })

  it('die Auswahl liefert die gewählten Etiketten sortiert nach Bin-Code, unabhängig von der Reihenfolge des Anklickens', () => {
    const all = rows.map(binLabel)
    const labels = selectedLabels(all, new Set(['b10', 'b21', 'b1', 'unbekannt']))

    expect(labels.map((l) => l.code)).toEqual(['A-01-1', 'A-01-10', 'A-02-1'])
    expect(labels[0]).toMatchObject({ kind: 'bin', id: 'b1', key: 'bin:b1', detail: 'Regal S1' })
  })
})

describe('Etiketten aus Artikel und Bestellung', () => {
  it('Artikel: SKU im Barcode, Name darunter; Bestellung: Nummer im Barcode, Kundenreferenz als Zusatz', () => {
    expect(articleLabel(makeArticle({ id: 'a9', sku: 'ART-9', name: 'Mutter M8' })))
      .toEqual({ key: 'article:a9', kind: 'article', id: 'a9', code: 'ART-9', title: 'Mutter M8' })
    const order = { id: 'o1', orderNumber: 'ORD-1', customerReference: 'K-1', status: 'New', source: 'Manual', createdAt: '', lines: [], hasStockNow: true, hasStockAfterFifo: true }
    expect(orderLabel(order)).toEqual({ key: 'order:o1', kind: 'order', id: 'o1', code: 'ORD-1', detail: 'Kunde: K-1' })
    expect(orderLabel({ ...order, customerReference: null }).detail).toBeUndefined()
  })
})

describe('Auswahl per Adresse (Verweis von anderen Seiten)', () => {
  it('liest Art und Ids aus den Suchparametern und ignoriert Unbekanntes', () => {
    expect(parseKind('article')).toBe('article')
    expect(parseKind('order')).toBe('order')
    expect(parseKind('bin')).toBe('bin')
    expect(parseKind('quatsch')).toBe('bin')
    expect(parseKind(null)).toBe('bin')
    expect([...parseIds(' a, b ,,a,c ')]).toEqual(['a', 'b', 'c'])
    expect(parseIds(null).size).toBe(0)
  })
})
