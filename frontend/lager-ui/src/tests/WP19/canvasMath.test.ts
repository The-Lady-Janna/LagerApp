import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import type { WallDto } from '../../api/types'
import { useFlashMessage } from '../../components/layoutEditor/useFlashMessage'
import {
  ENDPOINT_SNAP_MM, finalizeWallPoints, sameCursor, snap, snapToVertex, toPositions, totalLength,
} from '../../lib/layoutGeometry'
import {
  MAX_SCALE, MIN_SCALE, isDoubleTap, pinchViewport, screenToWorld, zoomAround, type ViewportState,
} from '../../lib/viewport'

const wall = (id: string, points: [number, number][]): WallDto => ({
  id, warehouseId: 'w1', label: null, thicknessMm: 100,
  points: points.map(([xMm, yMm]) => ({ xMm, yMm, zMm: 0 })),
} as WallDto)

describe('Layout-Geometrie', () => {
  it('rastet auf das 100-mm-Raster ein', () => {
    expect(snap(149)).toBe(100)
    expect(snap(150)).toBe(200)
    expect(snap(-260)).toBe(-300)
  })

  it('rastet auf den nächsten vorhandenen Wandpunkt im Radius ein, sonst aufs Raster', () => {
    const walls = [wall('a', [[1000, 1000], [3000, 1000]])]

    expect(snapToVertex(1210, 940, walls)).toEqual({ x: 1000, y: 1000, snapped: true })
    expect(snapToVertex(1000 + ENDPOINT_SNAP_MM + 50, 1000, walls)).toEqual({ x: 1400, y: 1000, snapped: false })   // außerhalb: Raster
  })

  it('beim Ziehen eines Punktes rastet er nicht auf sich selbst ein (excludeKey)', () => {
    const walls = [wall('a', [[1000, 1000], [3000, 1000]])]

    expect(snapToVertex(1010, 1010, walls, [], 'a#0')).toEqual({ x: 1000, y: 1000, snapped: false })
  })

  it('rastet auch auf Punkte des Entwurfs ein (Wand schließen)', () => {
    expect(snapToVertex(2050, 2020, [], [{ x: 2000, y: 2000 }])).toEqual({ x: 2000, y: 2000, snapped: true })
  })

  it('entfernt beim Beenden den doppelten Endpunkt, den der zweite Klick eines Doppelklicks gesetzt hat', () => {
    const pts = [{ x: 0, y: 0 }, { x: 1000, y: 0 }, { x: 1000, y: 0 }]

    expect(finalizeWallPoints(pts)).toEqual([{ x: 0, y: 0 }, { x: 1000, y: 0 }])
    expect(finalizeWallPoints([{ x: 0, y: 0 }, { x: 100, y: 0 }])).toHaveLength(2)   // kein Duplikat → unverändert
    expect(finalizeWallPoints(pts)).not.toBe(pts)                                    // die Eingabe wird nie verändert
    expect(pts).toHaveLength(3)
  })

  it('wandelt Punkte in Server-Positionen (z = 0) und misst die Länge des Linienzugs', () => {
    const positions = toPositions([{ x: 0, y: 0 }, { x: 3000, y: 4000 }])

    expect(positions).toEqual([{ xMm: 0, yMm: 0, zMm: 0 }, { xMm: 3000, yMm: 4000, zMm: 0 }])
    expect(totalLength(positions)).toBe(5000)
    expect(totalLength([])).toBe(0)
  })

  it('erkennt einen unveränderten Cursor (dann muss die Seite nicht neu rendern)', () => {
    expect(sameCursor({ x: 1, y: 2, snapped: false }, { x: 1, y: 2, snapped: false })).toBe(true)
    expect(sameCursor({ x: 1, y: 2, snapped: false }, { x: 1, y: 2, snapped: true })).toBe(false)
    expect(sameCursor(null, null)).toBe(true)
    expect(sameCursor(null, { x: 1, y: 2, snapped: false })).toBe(false)
  })
})

describe('Ansicht: Zoom, Pan und Pinch (Touch)', () => {
  const vp: ViewportState = { scale: 0.05, offsetX: 40, offsetY: 40 }

  it('Zoom um einen Punkt lässt den Weltpunkt darunter an derselben Bildschirmstelle', () => {
    const pointer = { x: 300, y: 200 }
    const before = screenToWorld(vp, pointer)

    const zoomed = zoomAround(vp, pointer, 1.5)

    expect(zoomed.scale).toBeCloseTo(0.075)
    const after = screenToWorld(zoomed, pointer)
    expect(after.x).toBeCloseTo(before.x)
    expect(after.y).toBeCloseTo(before.y)
  })

  it('begrenzt den Zoom auf den zulässigen Bereich', () => {
    expect(zoomAround(vp, { x: 0, y: 0 }, 1e6).scale).toBe(MAX_SCALE)
    expect(zoomAround(vp, { x: 0, y: 0 }, 1e-6).scale).toBe(MIN_SCALE)
  })

  it('Pinch: der Maßstab folgt dem Fingerabstand, der Weltpunkt unter der Fingermitte bleibt unter der Mitte', () => {
    const start = { viewport: vp, a: { x: 100, y: 200 }, b: { x: 200, y: 200 } }
    const worldUnderCentre = screenToWorld(vp, { x: 150, y: 200 })

    // Finger spreizen (Abstand ×2) und dabei um 30/10 px verschieben
    const pinched = pinchViewport(start, { x: 80, y: 210 }, { x: 280, y: 210 })

    expect(pinched.scale).toBeCloseTo(vp.scale * 2)
    const centre = { x: 180, y: 210 }
    const worldNow = screenToWorld(pinched, centre)
    expect(worldNow.x).toBeCloseTo(worldUnderCentre.x)
    expect(worldNow.y).toBeCloseTo(worldUnderCentre.y)
  })

  it('Pinch: ein Fingerabstand von 0 ändert den Maßstab nicht (keine Division durch 0)', () => {
    const start = { viewport: vp, a: { x: 100, y: 100 }, b: { x: 100, y: 100 } }

    expect(pinchViewport(start, { x: 120, y: 100 }, { x: 180, y: 100 }).scale).toBe(vp.scale)
  })

  it('erkennt einen Doppeltipp nur kurz nacheinander und nahe beieinander', () => {
    const first = { time: 1000, point: { x: 100, y: 100 } }

    expect(isDoubleTap(first, 1200, { x: 110, y: 105 })).toBe(true)
    expect(isDoubleTap(first, 1600, { x: 100, y: 100 })).toBe(false)   // zu spät
    expect(isDoubleTap(first, 1100, { x: 300, y: 100 })).toBe(false)   // zu weit weg
    expect(isDoubleTap(null, 1100, { x: 100, y: 100 })).toBe(false)
  })
})

describe('useFlashMessage', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  it('blendet die Meldung nach 2,5 s aus', () => {
    const { result } = renderHook(() => useFlashMessage())

    act(() => result.current.flash('Regal gespeichert'))
    expect(result.current.message).toBe('Regal gespeichert')
    act(() => { vi.advanceTimersByTime(2500) })

    expect(result.current.message).toBeNull()
  })

  it('der Timer einer älteren Meldung löscht eine neuere nicht vorzeitig', () => {
    const { result } = renderHook(() => useFlashMessage())

    act(() => result.current.flash('Erste'))
    act(() => { vi.advanceTimersByTime(2000) })
    act(() => result.current.flash('Zweite'))
    act(() => { vi.advanceTimersByTime(1000) })   // 3 s seit "Erste", aber nur 1 s seit "Zweite"

    expect(result.current.message).toBe('Zweite')
    act(() => { vi.advanceTimersByTime(1500) })
    expect(result.current.message).toBeNull()
  })

  it('räumt den Timer beim Unmount auf', () => {
    const { result, unmount } = renderHook(() => useFlashMessage())
    act(() => result.current.flash('Meldung'))

    unmount()

    expect(vi.getTimerCount()).toBe(0)
  })
})
