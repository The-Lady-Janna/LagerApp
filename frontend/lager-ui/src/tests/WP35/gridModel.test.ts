import { describe, expect, it } from 'vitest'
import type { GridLayoutDto } from '../../api/gridLayoutHooks'
import {
  applySettings, cellKey, cellRef, cellsOutside, changedCellCount, columnName, fromDto, paint, rectRef, setContainers,
  settingsChanged, summarize, toRequest, type GridDraft,
} from '../../components/gridEditor/gridModel'

function emptyDraft(columns = 6, rows = 4): GridDraft {
  return {
    cellSizeMm: 1_000, columns, rows,
    containerWidthMm: 600, containerDepthMm: 600, containerHeightMm: 400, containerMaxWeightGrams: 50_000,
    cells: new Map(),
  }
}

describe('Raster-Modell', () => {
  it('benennt Spalten und Felder wie eine Tabelle', () => {
    expect([0, 1, 25, 26, 27, 51, 52, 99].map(columnName)).toEqual(['A', 'B', 'Z', 'AA', 'AB', 'AZ', 'BA', 'CV'])
    expect(cellRef(2, 4)).toBe('C5')
    expect(rectRef({ c1: 4, r1: 2, c2: 1, r2: 1 })).toBe('B2:E3')
    expect(rectRef({ c1: 0, r1: 0, c2: 0, r2: 0 })).toBe('A1')
  })

  it('malt ein Rechteck, Laufweg leert Felder, und ein Klick ohne Änderung erzeugt keinen neuen Stand', () => {
    const shelves = paint(emptyDraft(), { c1: 1, r1: 1, c2: 3, r2: 1 }, 'Shelf', 4)
    expect([...shelves.cells.entries()]).toEqual([
      ['1,1', { type: 'Shelf', containers: 4 }],
      ['2,1', { type: 'Shelf', containers: 4 }],
      ['3,1', { type: 'Shelf', containers: 4 }],
    ])
    // Nicht-Lagerfelder haben keine Behälter; außerhalb des Rasters wird abgeschnitten.
    const wall = paint(shelves, { c1: 4, r1: 3, c2: 9, r2: 9 }, 'Wall', 7)
    expect(wall.cells.get('5,3')).toEqual({ type: 'Wall', containers: 0 })
    expect(wall.cells.size).toBe(5)

    expect(paint(wall, { c1: 4, r1: 3, c2: 5, r2: 3 }, 'Wall', 1)).toBe(wall)
    const cleared = paint(wall, { c1: 2, r1: 1, c2: 2, r2: 1 }, 'Walkway', 1)
    expect(cleared.cells.has('2,1')).toBe(false)
    expect(cleared.cells.size).toBe(4)
  })

  it('setzt die Behälterzahl nur bei Lagerfeldern und begrenzt sie auf 1 bis 50', () => {
    let draft = paint(emptyDraft(), { c1: 0, r1: 0, c2: 1, r2: 0 }, 'Shelf', 2)
    draft = paint(draft, { c1: 2, r1: 0, c2: 2, r2: 0 }, 'Start', 2)
    const more = setContainers(draft, { c1: 0, r1: 0, c2: 5, r2: 0 }, 99)
    expect(more.cells.get('0,0')?.containers).toBe(50)
    expect(more.cells.get('2,0')).toEqual({ type: 'Start', containers: 0 })
    expect(setContainers(draft, { c1: 3, r1: 3, c2: 3, r2: 3 }, 5)).toBe(draft)
  })

  it('zählt Änderungen gegenüber dem gespeicherten Stand und entfernt beim Verkleinern Felder außerhalb', () => {
    const saved = paint(emptyDraft(), { c1: 0, r1: 0, c2: 2, r2: 0 }, 'Shelf', 3)
    const draft = setContainers(paint(saved, { c1: 5, r1: 3, c2: 5, r2: 3 }, 'End', 1), { c1: 0, r1: 0, c2: 0, r2: 0 }, 4)
    expect(changedCellCount(saved, draft)).toBe(2)

    expect(cellsOutside(draft, 4, 4)).toBe(1)
    const { draft: smaller, dropped } = applySettings(draft, { ...draft, columns: 4 })
    expect(dropped).toBe(1)
    expect(smaller.cells.has(cellKey(5, 3))).toBe(false)
    expect(settingsChanged(draft, smaller)).toBe(true)
    expect(settingsChanged(draft, emptyDraft())).toBe(false)
  })

  it('fasst eine Auswahl nach Feldarten zusammen (leere Felder als Laufweg)', () => {
    const draft = paint(paint(emptyDraft(), { c1: 0, r1: 0, c2: 1, r2: 0 }, 'Shelf', 3), { c1: 2, r1: 0, c2: 2, r2: 0 }, 'Wall', 0)
    const all = summarize(draft)
    expect(all.containers).toBe(6)
    const part = summarize(draft, { c1: 1, r1: 0, c2: 3, r2: 1 })
    expect(Object.fromEntries(part.byType)).toEqual({ Shelf: 1, Wall: 1, Walkway: 4 })
  })

  it('übernimmt den Server-Stand und baut die Anfrage zum Speichern sortiert nach Zeile und Spalte', () => {
    const dto: GridLayoutDto = {
      warehouseId: 'w1', exists: true, version: 'v1', cellSizeMm: 1_000, columns: 6, rows: 4,
      containerWidthMm: 600, containerDepthMm: 600, containerHeightMm: 400, containerMaxWeightGrams: 50_000,
      cells: [
        { col: 3, row: 2, ref: 'D3', type: 'Wall', containers: 0, shelfId: null, shelfCode: null, occupiedContainers: 0 },
        { col: 1, row: 0, ref: 'B1', type: 'Shelf', containers: 2, shelfId: 's1', shelfCode: 'A01-01', occupiedContainers: 1 },
      ],
      managedShelfIds: ['s1'], managedWallIds: [], managedPickPointIds: [],
    }
    const draft = fromDto(dto)
    expect(draft.cells.get('1,0')).toEqual({ type: 'Shelf', containers: 2 })

    const request = toRequest(paint(draft, { c1: 0, r1: 3, c2: 0, r2: 3 }, 'Start', 1), dto.version)
    expect(request.expectedVersion).toBe('v1')
    expect(request.cells).toEqual([
      { col: 1, row: 0, type: 'Shelf', containers: 2 },
      { col: 3, row: 2, type: 'Wall', containers: 0 },
      { col: 0, row: 3, type: 'Start', containers: 0 },
    ])
  })
})
