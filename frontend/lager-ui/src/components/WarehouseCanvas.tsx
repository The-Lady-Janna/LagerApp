import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Layer, Line, Stage, Text } from 'react-konva'
import type Konva from 'konva'
import { useCanvasColors } from '../lib/canvasColors'
import {
  TAP_SLOP_PX,
  isDoubleTap,
  pinchViewport,
  screenToWorld,
  zoomAround,
  type PinchStart,
  type Point,
  type ViewportState,
} from '../lib/viewport'

export type { ViewportState } from '../lib/viewport'

interface Props {
  /** Feste Breite in Pixeln. Ohne Angabe füllt die Zeichenfläche ihren Container und folgt dessen Breite (ResizeObserver). */
  width?: number
  height?: number
  gridStepMm?: number
  initial?: Partial<ViewportState>
  /** Optional CSS cursor style — useful in draw modes. */
  cursorStyle?: string
  children: (vp: ViewportState) => ReactNode
  onStageClick?: (worldX: number, worldY: number) => void
  onStageDoubleClick?: (worldX: number, worldY: number) => void
  /** Fires on every mousemove with world-mm coordinates. Only attach when you actually need it. */
  onWorldMouseMove?: (worldX: number, worldY: number) => void
}

const DEFAULT_SCALE = 0.05
const DEFAULT_HEIGHT = 500
/** Breite, solange der Container noch nicht gemessen ist (und in Umgebungen ohne ResizeObserver). */
const FALLBACK_WIDTH = 900

/** Aktuelle Breite des Containers; folgt Größenänderungen (Fenster drehen, Sidebar auf-/zuklappen). */
function useContainerWidth(fixedWidth: number | undefined) {
  const ref = useRef<HTMLDivElement>(null)
  const [measured, setMeasured] = useState(0)

  useEffect(() => {
    const el = ref.current
    if (fixedWidth !== undefined || !el || typeof ResizeObserver === 'undefined') return
    // Der Observer meldet die Startgröße direkt nach observe() — dort wird gemessen, nicht im Effekt selbst.
    const observer = new ResizeObserver(() => setMeasured(Math.floor(el.clientWidth)))
    observer.observe(el)
    return () => observer.disconnect()
  }, [fixedWidth])

  return { ref, width: fixedWidth ?? (measured > 0 ? measured : FALLBACK_WIDTH) }
}

/** Berührungspunkte relativ zur Zeichenfläche (Pixel), für Pan und Pinch. */
function touchPoints(evt: TouchEvent, container: HTMLElement): Point[] {
  const rect = container.getBoundingClientRect()
  return Array.from(evt.touches, (t) => ({ x: t.clientX - rect.left, y: t.clientY - rect.top }))
}

type TouchGesture =
  | { kind: 'pan'; start: Point; offsetX: number; offsetY: number; moved: boolean }
  | { kind: 'pinch'; start: PinchStart }

export function WarehouseCanvas({
  width: fixedWidth,
  height = DEFAULT_HEIGHT,
  gridStepMm = 1000,
  initial,
  cursorStyle,
  children,
  onStageClick,
  onStageDoubleClick,
  onWorldMouseMove,
}: Props) {
  const { t } = useTranslation()
  const colors = useCanvasColors()
  const { ref: containerRef, width } = useContainerWidth(fixedWidth)
  const stageRef = useRef<Konva.Stage>(null)
  const [vp, setVp] = useState<ViewportState>({
    scale: initial?.scale ?? DEFAULT_SCALE,
    offsetX: initial?.offsetX ?? 40,
    offsetY: initial?.offsetY ?? 40,
  })

  const handleWheel = useCallback((e: Konva.KonvaEventObject<WheelEvent>) => {
    e.evt.preventDefault()
    const stage = stageRef.current
    if (!stage) return
    const pointer = stage.getPointerPosition()
    if (!pointer) return

    setVp((cur) => zoomAround(cur, pointer, e.evt.deltaY < 0 ? 1.1 : 1 / 1.1))
  }, [])

  const dragRef = useRef<{ startX: number; startY: number; ox: number; oy: number } | null>(null)
  const [isPanning, setIsPanning] = useState(false)

  const onMouseDown = useCallback((e: Konva.KonvaEventObject<MouseEvent>) => {
    if (e.evt.button !== 0) return
    if (e.target !== e.target.getStage()) return
    const stage = stageRef.current
    if (!stage) return
    const p = stage.getPointerPosition()
    if (!p) return
    dragRef.current = { startX: p.x, startY: p.y, ox: vp.offsetX, oy: vp.offsetY }
  }, [vp.offsetX, vp.offsetY])

  const onMouseMove = useCallback(() => {
    const stage = stageRef.current
    if (!stage) return
    const p = stage.getPointerPosition()
    if (!p) return

    const drag = dragRef.current
    if (drag) {
      const dx = p.x - drag.startX
      const dy = p.y - drag.startY
      if (!isPanning && Math.hypot(dx, dy) > 4) setIsPanning(true)
      setVp((s) => ({ ...s, offsetX: drag.ox + dx, offsetY: drag.oy + dy }))
      return
    }

    if (onWorldMouseMove) {
      const world = screenToWorld(vp, p)
      onWorldMouseMove(world.x, world.y)
    }
  }, [isPanning, onWorldMouseMove, vp])

  const onDblClick = useCallback((e: Konva.KonvaEventObject<MouseEvent>) => {
    if (!onStageDoubleClick) return
    if (e.target !== e.target.getStage()) return
    const stage = stageRef.current
    if (!stage) return
    const p = stage.getPointerPosition()
    if (!p) return
    const world = screenToWorld(vp, p)
    onStageDoubleClick(world.x, world.y)
  }, [onStageDoubleClick, vp])

  const onMouseUp = useCallback((e: Konva.KonvaEventObject<MouseEvent>) => {
    const drag = dragRef.current
    const stage = stageRef.current
    const p = stage?.getPointerPosition()
    const moved = drag && p
      ? Math.hypot(p.x - drag.startX, p.y - drag.startY) > 4
      : false
    dragRef.current = null
    setIsPanning(false)

    if (drag && !moved && onStageClick && p && e.target === e.target.getStage()) {
      const world = screenToWorld(vp, p)
      onStageClick(world.x, world.y)
    }
  }, [onStageClick, vp])

  // ---- Touch: ein Finger auf leerer Fläche zieht (Pan) bzw. tippt (= Klick, zweimal = Doppelklick),
  // zwei Finger zoomen und verschieben (Pinch). Shapes (Regale, Pickpunkte) verarbeitet Konva selbst
  // (Drag, onTap). touch-action:none am Container (unten) verhindert, dass der Browser stattdessen scrollt/zoomt.
  const gestureRef = useRef<TouchGesture | null>(null)
  const lastTapRef = useRef<{ time: number; point: Point } | null>(null)

  const onTouchStart = useCallback((e: Konva.KonvaEventObject<TouchEvent>) => {
    const stage = stageRef.current
    if (!stage) return
    const pts = touchPoints(e.evt, stage.container())
    if (pts.length >= 2) {
      // Pinch gilt auch, wenn ein Finger auf einem Shape liegt — sonst ließe sich dort nicht zoomen.
      gestureRef.current = { kind: 'pinch', start: { viewport: vp, a: pts[0], b: pts[1] } }
      if (e.evt.cancelable) e.evt.preventDefault()
      return
    }
    if (pts.length === 1 && e.target === stage) {
      gestureRef.current = { kind: 'pan', start: pts[0], offsetX: vp.offsetX, offsetY: vp.offsetY, moved: false }
      if (e.evt.cancelable) e.evt.preventDefault() // unterdrückt die nachgereichten Maus-Ereignisse (sonst doppelter Klick)
    }
  }, [vp])

  const onTouchMove = useCallback((e: Konva.KonvaEventObject<TouchEvent>) => {
    const stage = stageRef.current
    const gesture = gestureRef.current
    if (!stage || !gesture) return
    const pts = touchPoints(e.evt, stage.container())
    if (gesture.kind === 'pinch' && pts.length >= 2) {
      setVp(pinchViewport(gesture.start, pts[0], pts[1]))
    } else if (gesture.kind === 'pan' && pts.length === 1) {
      const dx = pts[0].x - gesture.start.x
      const dy = pts[0].y - gesture.start.y
      if (!gesture.moved && Math.hypot(dx, dy) > TAP_SLOP_PX) gesture.moved = true
      if (gesture.moved) setVp((s) => ({ ...s, offsetX: gesture.offsetX + dx, offsetY: gesture.offsetY + dy }))
    }
    if (e.evt.cancelable) e.evt.preventDefault()
  }, [])

  const onTouchEnd = useCallback((e: Konva.KonvaEventObject<TouchEvent>) => {
    const stage = stageRef.current
    const gesture = gestureRef.current
    if (!stage || !gesture) return
    gestureRef.current = null
    if (gesture.kind !== 'pan' || gesture.moved) return

    // Ein Tipper auf die leere Fläche: wie ein Klick; ein zweiter Tipper kurz danach beendet (Doppelklick).
    const rect = stage.container().getBoundingClientRect()
    const touch = e.evt.changedTouches[0]
    if (!touch) return
    const point = { x: touch.clientX - rect.left, y: touch.clientY - rect.top }
    const world = screenToWorld(vp, point)
    const now = Date.now()
    if (isDoubleTap(lastTapRef.current, now, point)) {
      lastTapRef.current = null
      onStageDoubleClick?.(world.x, world.y)
      return
    }
    lastTapRef.current = { time: now, point }
    onStageClick?.(world.x, world.y)
  }, [onStageClick, onStageDoubleClick, vp])

  // Grid lines, memoized so they only recompute when viewport actually moves
  const gridLines = useMemo(() => {
    const visMinX = -vp.offsetX / vp.scale
    const visMinY = -vp.offsetY / vp.scale
    const visMaxX = (width - vp.offsetX) / vp.scale
    const visMaxY = (height - vp.offsetY) / vp.scale
    const gridStart = (v: number, step: number) => Math.floor(v / step) * step
    const lines: ReactNode[] = []
    const lineWidth = 1 / vp.scale
    for (let x = gridStart(visMinX, gridStepMm); x <= visMaxX; x += gridStepMm) {
      lines.push(<Line key={`vx${x}`} points={[x, visMinY, x, visMaxY]}
        stroke={colors.grid} strokeWidth={lineWidth} listening={false}
        perfectDrawEnabled={false} shadowForStrokeEnabled={false} hitStrokeWidth={0}
      />)
    }
    for (let y = gridStart(visMinY, gridStepMm); y <= visMaxY; y += gridStepMm) {
      lines.push(<Line key={`hy${y}`} points={[visMinX, y, visMaxX, y]}
        stroke={colors.grid} strokeWidth={lineWidth} listening={false}
        perfectDrawEnabled={false} shadowForStrokeEnabled={false} hitStrokeWidth={0}
      />)
    }
    return lines
  }, [vp.offsetX, vp.offsetY, vp.scale, width, height, gridStepMm, colors.grid])

  const cursor = isPanning ? 'grabbing' : (cursorStyle ?? 'default')

  return (
    <div
      ref={containerRef}
      style={{ position: 'relative', width: fixedWidth, maxWidth: '100%', border: `1px solid ${colors.border}`, background: colors.surface, touchAction: 'none' }}
    >
      <Stage
        ref={stageRef}
        width={width}
        height={height}
        onWheel={handleWheel}
        onMouseDown={onMouseDown}
        onMouseMove={onMouseMove}
        onMouseUp={onMouseUp}
        onDblClick={onDblClick}
        onTouchStart={onTouchStart}
        onTouchMove={onTouchMove}
        onTouchEnd={onTouchEnd}
        style={{ cursor, touchAction: 'none' }}
      >
        <Layer x={vp.offsetX} y={vp.offsetY} scaleX={vp.scale} scaleY={vp.scale} listening>
          {gridLines}
          <Line points={[-200, 0, 200, 0]} stroke={colors.axis} strokeWidth={2 / vp.scale}
            listening={false} perfectDrawEnabled={false} />
          <Line points={[0, -200, 0, 200]} stroke={colors.axis} strokeWidth={2 / vp.scale}
            listening={false} perfectDrawEnabled={false} />
          <Text x={6 / vp.scale} y={-16 / vp.scale} text="0,0" fontSize={11 / vp.scale}
            fill={colors.originLabel} listening={false} perfectDrawEnabled={false} />
          {children(vp)}
        </Layer>
      </Stage>
      <div style={{
        position: 'absolute', bottom: 8, right: 12, background: colors.hudBackground,
        padding: '4px 8px', borderRadius: 4, fontSize: 11, color: colors.hudText, pointerEvents: 'none',
      }}>
        {t('warehouse:canvas.hud', { zoom: (vp.scale * 100).toFixed(1), grid: gridStepMm })}
      </div>
    </div>
  )
}
