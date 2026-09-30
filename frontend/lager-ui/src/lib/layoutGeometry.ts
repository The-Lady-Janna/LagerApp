import type { PositionDto, WallDto } from '../api/types'

/// <summary>
/// Reine Geometrie des Layout-Editors (Raster, Einrasten, Wand-Entwurf) — ohne React und Konva,
/// damit sie testbar bleibt und die Seite schlank.
/// </summary>

/** Raster in mm, auf das Regale, Pickpunkte und Wandpunkte einrasten. */
export const SNAP_MM = 100
/** Radius in mm, in dem ein Wandpunkt auf einen vorhandenen Eckpunkt einrastet. */
export const ENDPOINT_SNAP_MM = 300

export const snap = (v: number) => Math.round(v / SNAP_MM) * SNAP_MM

export interface XY { x: number; y: number }
export interface SnapResult extends XY { snapped: boolean }

/**
 * Snap to nearest existing wall vertex (or any extra candidates passed in),
 * falls back to grid snap. The excludeKey allows skipping a specific vertex
 * (used while dragging a vertex so it doesn't snap to itself).
 */
export function snapToVertex(
  x: number,
  y: number,
  walls: readonly WallDto[],
  extra: readonly XY[] = [],
  excludeKey?: string,
): SnapResult {
  const rSq = ENDPOINT_SNAP_MM * ENDPOINT_SNAP_MM
  let best: { x: number; y: number; dSq: number } | null = null

  const consider = (px: number, py: number, key?: string) => {
    if (key && key === excludeKey) return
    const dSq = (px - x) ** 2 + (py - y) ** 2
    if (dSq <= rSq && (!best || dSq < best.dSq)) best = { x: px, y: py, dSq }
  }

  for (const w of walls) {
    w.points.forEach((p, i) => consider(p.xMm, p.yMm, `${w.id}#${i}`))
  }
  extra.forEach((p, i) => consider(p.x, p.y, `extra#${i}`))

  if (best !== null) {
    const b = best as { x: number; y: number; dSq: number }
    return { x: b.x, y: b.y, snapped: true }
  }
  return { x: snap(x), y: snap(y), snapped: false }
}

/**
 * Bereinigt den Wand-Entwurf beim Beenden: Ein Doppelklick/Doppeltipp löst vorher schon zwei einzelne Klicks aus,
 * der zweite setzt denselben Punkt noch einmal — dieser doppelte Endpunkt wird entfernt.
 */
export function finalizeWallPoints(points: readonly XY[]): XY[] {
  if (points.length >= 2) {
    const last = points[points.length - 1]
    const prev = points[points.length - 2]
    if (last.x === prev.x && last.y === prev.y) return points.slice(0, -1)
  }
  return [...points]
}

/** Punkte des Entwurfs als Server-Positionen (Boden, z = 0). */
export function toPositions(points: readonly XY[]): PositionDto[] {
  return points.map((p) => ({ xMm: p.x, yMm: p.y, zMm: 0 }))
}

/** Länge eines Linienzugs in mm. */
export function totalLength(points: readonly PositionDto[]): number {
  let sum = 0
  for (let i = 1; i < points.length; i++) {
    sum += Math.hypot(points[i].xMm - points[i - 1].xMm, points[i].yMm - points[i - 1].yMm)
  }
  return sum
}

/** Gleicher Cursor (Position + Einrast-Zustand)? Dann muss die Seite nicht neu rendern. */
export function sameCursor(a: SnapResult | null, b: SnapResult | null): boolean {
  if (a === b) return true
  return a !== null && b !== null && a.x === b.x && a.y === b.y && a.snapped === b.snapped
}
