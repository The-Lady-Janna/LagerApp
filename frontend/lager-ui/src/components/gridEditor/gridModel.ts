import type { GridCellType, GridLayoutDto, SaveGridLayoutRequest } from '../../api/gridLayoutHooks'

/// <summary>
/// Reine Logik des Raster-Editors (ohne React): Feldarten, Tabellen-Bezeichnungen ("C5"), Entwurf aus Server-Daten, Malen und
/// Behälterzahl auf einem Rechteck, Größe ändern, Unterschiede zum gespeicherten Stand und die Anfrage zum Speichern.
/// Ein leeres Feld ist Laufweg; der Entwurf hält nur belegte Felder (Schlüssel "Spalte,Zeile", beide ab 0).
/// </summary>

/** Reihenfolge in der Palette: Grundfelder, Lagerfelder, Punkte und Flächen. */
export const CELL_TYPES: readonly GridCellType[] = [
  'Walkway', 'Wall', 'Door', 'Obstacle',
  'Shelf', 'PackingStorage', 'OutgoingStorage', 'Returns',
  'Start', 'End', 'Packing', 'GoodsReceipt',
]

const STORAGE: ReadonlySet<GridCellType> = new Set(['Shelf', 'PackingStorage', 'OutgoingStorage', 'Returns'])

/** Lagerfeld: daraus wird ein Regal mit X Behältern. */
export const isStorage = (type: GridCellType) => STORAGE.has(type)

/** Grenzen wie im Backend (GridLayout). */
export const GRID_LIMITS = {
  minCellSizeMm: 250,
  maxCellSizeMm: 5_000,
  maxColumns: 100,
  maxRows: 100,
  maxContainers: 50,
} as const

export interface DraftCell {
  type: GridCellType
  containers: number
}

export interface GridSettings {
  cellSizeMm: number
  columns: number
  rows: number
  containerWidthMm: number
  containerDepthMm: number
  containerHeightMm: number
  containerMaxWeightGrams: number
}

export interface GridDraft extends GridSettings {
  cells: ReadonlyMap<string, DraftCell>
}

/** Ein Rechteck aus Feldern, Ecken eingeschlossen (nicht zwingend normalisiert). */
export interface CellRect {
  c1: number
  r1: number
  c2: number
  r2: number
}

export const cellKey = (col: number, row: number) => `${col},${row}`

export function parseKey(key: string): [number, number] {
  const [c, r] = key.split(',')
  return [Number(c), Number(r)]
}

/** Spaltenname wie in einer Tabelle: 0 = A, 25 = Z, 26 = AA. */
export function columnName(col: number): string {
  let name = ''
  let n = col + 1
  while (n > 0) {
    const rem = (n - 1) % 26
    name = String.fromCharCode(65 + rem) + name
    n = Math.floor((n - 1) / 26)
  }
  return name
}

/** Feldbezeichnung wie "C5" (Zeile ab 1). */
export const cellRef = (col: number, row: number) => `${columnName(col)}${row + 1}`

export function normalizeRect(rect: CellRect): CellRect {
  return {
    c1: Math.min(rect.c1, rect.c2),
    r1: Math.min(rect.r1, rect.r2),
    c2: Math.max(rect.c1, rect.c2),
    r2: Math.max(rect.r1, rect.r2),
  }
}

export const rectContains = (rect: CellRect | null, col: number, row: number) => {
  if (!rect) return false
  const n = normalizeRect(rect)
  return col >= n.c1 && col <= n.c2 && row >= n.r1 && row <= n.r2
}

export const rectSize = (rect: CellRect) => {
  const n = normalizeRect(rect)
  return (n.c2 - n.c1 + 1) * (n.r2 - n.r1 + 1)
}

/** "B2" für ein Feld, "B2:E3" für einen Bereich. */
export function rectRef(rect: CellRect): string {
  const n = normalizeRect(rect)
  const from = cellRef(n.c1, n.r1)
  return n.c1 === n.c2 && n.r1 === n.r2 ? from : `${from}:${cellRef(n.c2, n.r2)}`
}

/** Rechteck auf das Raster beschneiden (null, wenn es ganz außerhalb liegt). */
export function clampRect(rect: CellRect, columns: number, rows: number): CellRect | null {
  const n = normalizeRect(rect)
  const c1 = Math.max(0, n.c1)
  const r1 = Math.max(0, n.r1)
  const c2 = Math.min(columns - 1, n.c2)
  const r2 = Math.min(rows - 1, n.r2)
  return c1 > c2 || r1 > r2 ? null : { c1, r1, c2, r2 }
}

export function settingsOf(draft: GridSettings): GridSettings {
  const { cellSizeMm, columns, rows, containerWidthMm, containerDepthMm, containerHeightMm, containerMaxWeightGrams } = draft
  return { cellSizeMm, columns, rows, containerWidthMm, containerDepthMm, containerHeightMm, containerMaxWeightGrams }
}

export function fromDto(dto: GridLayoutDto): GridDraft {
  const cells = new Map<string, DraftCell>()
  for (const c of dto.cells) cells.set(cellKey(c.col, c.row), { type: c.type, containers: isStorage(c.type) ? c.containers : 0 })
  return { ...settingsOf(dto), cells }
}

/**
 * Setzt alle Felder im Rechteck auf eine Art. Laufweg leert die Felder; Lagerfelder bekommen `containers` Behälter (1 bis 50),
 * alle anderen 0. Liefert denselben Entwurf zurück, wenn sich nichts ändert (kein Undo-Schritt für einen Leerklick).
 */
export function paint(draft: GridDraft, rect: CellRect, type: GridCellType, containers: number): GridDraft {
  const area = clampRect(rect, draft.columns, draft.rows)
  if (!area) return draft
  const count = isStorage(type) ? clampContainers(containers) : 0
  const cells = new Map(draft.cells)
  let changed = false
  for (let row = area.r1; row <= area.r2; row++) {
    for (let col = area.c1; col <= area.c2; col++) {
      const key = cellKey(col, row)
      const current = cells.get(key)
      if (type === 'Walkway') {
        if (current) { cells.delete(key); changed = true }
      } else if (!current || current.type !== type || current.containers !== count) {
        cells.set(key, { type, containers: count })
        changed = true
      }
    }
  }
  return changed ? { ...draft, cells } : draft
}

/** Ändert die Behälterzahl aller Lagerfelder im Rechteck (andere Felder bleiben). */
export function setContainers(draft: GridDraft, rect: CellRect, containers: number): GridDraft {
  const area = clampRect(rect, draft.columns, draft.rows)
  if (!area) return draft
  const count = clampContainers(containers)
  const cells = new Map(draft.cells)
  let changed = false
  for (const [key, cell] of draft.cells) {
    const [col, row] = parseKey(key)
    if (!isStorage(cell.type) || !rectContains(area, col, row) || cell.containers === count) continue
    cells.set(key, { ...cell, containers: count })
    changed = true
  }
  return changed ? { ...draft, cells } : draft
}

export const clampContainers = (n: number) =>
  Math.min(GRID_LIMITS.maxContainers, Math.max(1, Math.round(Number.isFinite(n) ? n : 1)))

/** Neue Einstellungen; Felder außerhalb der neuen Größe fallen weg (`dropped` = wie viele). */
export function applySettings(draft: GridDraft, settings: GridSettings): { draft: GridDraft; dropped: number } {
  const cells = new Map<string, DraftCell>()
  let dropped = 0
  for (const [key, cell] of draft.cells) {
    const [col, row] = parseKey(key)
    if (col < settings.columns && row < settings.rows) cells.set(key, cell)
    else dropped++
  }
  return { draft: { ...settings, cells }, dropped }
}

/** Wie viele belegte Felder außerhalb einer neuen Größe lägen. */
export function cellsOutside(draft: GridDraft, columns: number, rows: number): number {
  let n = 0
  for (const key of draft.cells.keys()) {
    const [col, row] = parseKey(key)
    if (col >= columns || row >= rows) n++
  }
  return n
}

/** Wie viele Felder sich gegenüber dem gespeicherten Stand unterscheiden. */
export function changedCellCount(saved: GridDraft, draft: GridDraft): number {
  let n = 0
  const keys = new Set([...saved.cells.keys(), ...draft.cells.keys()])
  for (const key of keys) {
    const a = saved.cells.get(key)
    const b = draft.cells.get(key)
    if (a?.type !== b?.type || (a?.containers ?? 0) !== (b?.containers ?? 0)) n++
  }
  return n
}

export function settingsChanged(saved: GridSettings, draft: GridSettings): boolean {
  const a = settingsOf(saved)
  const b = settingsOf(draft)
  return (Object.keys(a) as (keyof GridSettings)[]).some((k) => a[k] !== b[k])
}

/** Anzahl belegter Felder je Art im Rechteck (oder im ganzen Raster) und die Behälter der Lagerfelder. */
export function summarize(draft: GridDraft, rect: CellRect | null = null) {
  const byType = new Map<GridCellType, number>()
  let containers = 0
  let total = 0
  for (const [key, cell] of draft.cells) {
    if (rect) {
      const [col, row] = parseKey(key)
      if (!rectContains(rect, col, row)) continue
    }
    byType.set(cell.type, (byType.get(cell.type) ?? 0) + 1)
    containers += cell.containers
    total++
  }
  if (rect) {
    const walkways = rectSize(rect) - total
    if (walkways > 0) byType.set('Walkway', walkways)
  }
  return { byType, containers }
}

export function toRequest(draft: GridDraft, version: string | null): SaveGridLayoutRequest {
  const cells = [...draft.cells.entries()]
    .map(([key, cell]) => {
      const [col, row] = parseKey(key)
      return { col, row, type: cell.type, containers: isStorage(cell.type) ? cell.containers : 0 }
    })
    .sort((a, b) => a.row - b.row || a.col - b.col)
  return { ...settingsOf(draft), cells, expectedVersion: version }
}
