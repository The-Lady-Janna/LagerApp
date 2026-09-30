import { Circle, Group, Line, Rect, Text } from 'react-konva'
import type { PickItemDto, PositionDto, ShelfDto, StorageLocationDto, WallDto } from '../api/types'
import { useCanvasColors } from '../lib/canvasColors'
import { WarehouseCanvas } from './WarehouseCanvas'

interface Props {
  locations: StorageLocationDto[]
  items: PickItemDto[]
  /** Full path including any waypoints around walls. Falls back to bin-center-only if empty. */
  waypoints: PositionDto[]
  walls: WallDto[]
  /** Shelves drawn as light backdrop so the route view matches the layout editor. */
  shelves?: ShelfDto[]
}

const FALLBACK_START: PositionDto = { xMm: 0, yMm: 0, zMm: 0 }

export function PickRouteCanvas({ locations, items, waypoints, walls, shelves = [] }: Props) {
  const colors = useCanvasColors()
  const locMap = new Map(locations.map((l) => [l.id, l]))
  const ordered = [...items].sort((a, b) => a.sequenceNumber - b.sequenceNumber)

  // The route always starts at waypoints[0] (set by the backend to the chosen
  // start pick-point, or Origin as fallback). If no waypoints came back (old
  // pre-recalculation pick lists), fall back to drawing through bin centers
  // and place the start marker at the origin so the view at least renders.
  const startPos: PositionDto = waypoints.length > 0 ? waypoints[0] : FALLBACK_START

  const routePoints: number[] = waypoints.length > 0
    ? waypoints.flatMap((p) => [p.xMm, p.yMm])
    : (() => {
        const pts: number[] = [startPos.xMm, startPos.yMm]
        for (const it of ordered) {
          const loc = locMap.get(it.storageLocationId)
          if (loc) pts.push(loc.position.xMm + loc.widthMm / 2, loc.position.yMm + loc.depthMm / 2)
        }
        return pts
      })()

  const usableWalls = walls.filter((w) => Array.isArray(w.points) && w.points.length >= 2)

  return (
    <WarehouseCanvas height={500} gridStepMm={500}>
      {(vp) => {
        const px = (v: number) => v / vp.scale
        return (
          <>
            {/* Shelves as soft backdrop so the route view matches the layout editor */}
            {shelves.map((s) => (
              <Group key={s.id} x={s.position.xMm} y={s.position.yMm} listening={false}>
                <Rect
                  width={s.widthMm}
                  height={s.depthMm}
                  fill={colors.shelfFill}
                  stroke={colors.shelfStroke}
                  strokeWidth={px(1)}
                  perfectDrawEnabled={false}
                />
                <Text x={px(4)} y={px(4)} text={s.code} fontSize={px(10)} fill={colors.shelfText} perfectDrawEnabled={false} />
              </Group>
            ))}

            {/* Walls on top of shelves so the route view actually shows obstacles */}
            {usableWalls.map((w) => (
              <Line
                key={w.id}
                points={w.points.flatMap((p) => [p.xMm, p.yMm])}
                stroke={colors.wall}
                strokeWidth={Math.max(w.thicknessMm, px(3))}
                lineCap="square"
                lineJoin="round"
                listening={false}
                perfectDrawEnabled={false}
                shadowForStrokeEnabled={false}
              />
            ))}

            {/* Bins as small yellow rectangles, like in the layout editor */}
            {locations.map((b) => (
              <Group key={b.id} x={b.position.xMm} y={b.position.yMm} listening={false}>
                <Rect
                  width={b.widthMm}
                  height={b.depthMm}
                  fill={colors.binFill}
                  stroke={colors.binStroke}
                  strokeWidth={px(1)}
                  perfectDrawEnabled={false}
                />
              </Group>
            ))}

            {/* The route polyline */}
            <Line
              points={routePoints}
              stroke={colors.draft}
              strokeWidth={px(3)}
              lineCap="round"
              lineJoin="round"
              listening={false}
              perfectDrawEnabled={false}
            />

            {/* Waypoints that aren't a pick (wall corners) — small dots */}
            {waypoints.length > 0 && waypoints.slice(1).map((p, i) => {
              const isPickPos = ordered.some((it) => {
                const loc = locMap.get(it.storageLocationId)
                if (!loc) return false
                const cx = loc.position.xMm + loc.widthMm / 2
                const cy = loc.position.yMm + loc.depthMm / 2
                return cx === p.xMm && cy === p.yMm
              })
              if (isPickPos) return null
              return (
                <Circle key={`wp-${i}`} x={p.xMm} y={p.yMm} radius={px(4)}
                  fill={colors.waypointFill} stroke={colors.draft} strokeWidth={px(1.5)}
                  listening={false} perfectDrawEnabled={false}
                />
              )
            })}

            {/* Start marker at the actual route start (waypoints[0]) */}
            <Circle x={startPos.xMm} y={startPos.yMm} radius={px(8)} fill={colors.pickStart} listening={false} perfectDrawEnabled={false} />
            <Text
              x={startPos.xMm + px(12)}
              y={startPos.yMm - px(6)}
              text="Start"
              fontSize={px(11)}
              fill={colors.routeStartText}
              listening={false}
              perfectDrawEnabled={false}
            />

            {/* Numbered pick markers at the bin centers */}
            {ordered.map((it) => {
              const loc = locMap.get(it.storageLocationId)
              if (!loc) return null
              const cx = loc.position.xMm + loc.widthMm / 2
              const cy = loc.position.yMm + loc.depthMm / 2
              return (
                <Group key={it.id} x={cx} y={cy}>
                  <Circle radius={px(11)} fill={colors.draft} listening={false} perfectDrawEnabled={false} />
                  <Text
                    x={px(-4)}
                    y={px(-6)}
                    text={String(it.sequenceNumber)}
                    fontSize={px(13)}
                    fill={colors.routeMarkerText}
                    listening={false}
                    perfectDrawEnabled={false}
                  />
                </Group>
              )
            })}
          </>
        )
      }}
    </WarehouseCanvas>
  )
}
