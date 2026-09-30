/// <summary>
/// Reine Rechenhilfen für die Ansicht (Zoom/Pan) der Zeichenflächen — ohne Konva und ohne React,
/// damit Maus-Rad, Ziehen und Zwei-Finger-Geste (Pinch) dieselbe Mathematik nutzen und testbar bleiben.
/// </summary>
export interface ViewportState {
  /** Pixels per millimeter (zoom level). */
  scale: number
  /** Camera offset in screen pixels. */
  offsetX: number
  offsetY: number
}

export interface Point { x: number; y: number }

export const MIN_SCALE = 0.005
export const MAX_SCALE = 2

const clampScale = (scale: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale))

/** Bildschirmpunkt → Welt-Millimeter. */
export function screenToWorld(vp: ViewportState, p: Point): Point {
  return { x: (p.x - vp.offsetX) / vp.scale, y: (p.y - vp.offsetY) / vp.scale }
}

/** Zoomt um den Faktor `factor`, sodass der Weltpunkt unter `pointer` an derselben Bildschirmstelle bleibt. */
export function zoomAround(vp: ViewportState, pointer: Point, factor: number): ViewportState {
  const scale = clampScale(vp.scale * factor)
  const world = screenToWorld(vp, pointer)
  return { scale, offsetX: pointer.x - world.x * scale, offsetY: pointer.y - world.y * scale }
}

/** Ausgangslage einer Zwei-Finger-Geste. */
export interface PinchStart {
  viewport: ViewportState
  a: Point
  b: Point
}

const distance = (a: Point, b: Point) => Math.hypot(b.x - a.x, b.y - a.y)
const midpoint = (a: Point, b: Point): Point => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 })

/**
 * Ansicht während einer Pinch-Geste: der Maßstab folgt dem Abstand der Finger, der Weltpunkt unter der
 * Ausgangsmitte folgt der aktuellen Mitte (Zoomen und Verschieben in einem).
 */
export function pinchViewport(start: PinchStart, a: Point, b: Point): ViewportState {
  const startDistance = distance(start.a, start.b)
  const ratio = startDistance > 0 ? distance(a, b) / startDistance : 1
  const scale = clampScale(start.viewport.scale * ratio)
  const world = screenToWorld(start.viewport, midpoint(start.a, start.b))
  const centre = midpoint(a, b)
  return { scale, offsetX: centre.x - world.x * scale, offsetY: centre.y - world.y * scale }
}

/** Maximale Fingerbewegung (px), bis aus einem Tippen ein Ziehen wird. */
export const TAP_SLOP_PX = 8
/** Zwei Tipper innerhalb dieser Zeit/Distanz gelten als Doppeltipp. */
export const DOUBLE_TAP_MS = 350
export const DOUBLE_TAP_DISTANCE_PX = 30

export function isDoubleTap(previous: { time: number; point: Point } | null, time: number, point: Point): boolean {
  return previous !== null && time - previous.time <= DOUBLE_TAP_MS && distance(previous.point, point) <= DOUBLE_TAP_DISTANCE_PX
}
