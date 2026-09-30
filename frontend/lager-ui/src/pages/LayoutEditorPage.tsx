import { useCallback, useMemo, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { Circle, Group, Line, Rect, RegularPolygon, Text } from 'react-konva'
import type Konva from 'konva'
import {
  useAddBinToShelf,
  useBinHeatmap,
  useCreatePickPoint,
  useCreateWall,
  useDeleteBin,
  useDeletePickPoint,
  useDeleteWall,
  useMovePickPoint,
  useMoveShelf,
  usePickPoints,
  useStorageLocations,
  useUpdatePickPoint,
  useUpdateWallPoints,
  useWalls,
  useWarehouseLayout,
} from '../api/hooks'
import { WarehouseCanvas } from '../components/WarehouseCanvas'
import { NewShelfDialog } from '../components/NewShelfDialog'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { LayoutToolbar, type EditorMode } from '../components/layoutEditor/LayoutToolbar'
import { LayoutLegend, PickPointPanel, ShelfPanel, WallPanel } from '../components/layoutEditor/LayoutPanels'
import { useFlashMessage } from '../components/layoutEditor/useFlashMessage'
import { getErrorMessage } from '../api/errors'
import { dismissToastsForError } from '../state/toasts'
import { pickPointColor, useCanvasColors } from '../lib/canvasColors'
import { finalizeWallPoints, sameCursor, snap, snapToVertex, toPositions, type SnapResult } from '../lib/layoutGeometry'
import type { PickPointDto, PickPointType, PositionDto, ShelfDto, StorageLocationDto, WallDto } from '../api/types'
import { useActiveWarehouse } from '../state/activeWarehouse'
import { useAuth } from '../state/auth'
import { allBinCodes, nextFreeBinCode } from '../features/warehouses/treeModel'
import { t as translate } from '../i18n'

/** Statuszeile zu einem fehlgeschlagenen Speichern: verständliche Meldung statt "Fehler: AxiosError …" (der globale Toast entfällt, die Zeile zeigt den Fehler bereits). */
function errorLine(e: unknown): string {
  dismissToastsForError(e)
  return `${translate('warehouse:editor.errorPrefix')} ${getErrorMessage(e)}`
}

/** Was der Bestätigungsdialog gerade löschen soll. */
type PendingDelete =
  | { kind: 'bin'; bin: StorageLocationDto }
  | { kind: 'wall'; wall: WallDto }
  | { kind: 'pickpoint'; point: PickPointDto }

export function LayoutEditorPage() {
  const { t } = useTranslation()
  const layout = useWarehouseLayout()
  const locations = useStorageLocations()
  const walls = useWalls()
  const pickPoints = usePickPoints()
  const moveShelf = useMoveShelf()
  const addBin = useAddBinToShelf()
  const deleteBin = useDeleteBin()
  const createWall = useCreateWall()
  const updateWallPoints = useUpdateWallPoints()
  const deleteWall = useDeleteWall()
  const createPP = useCreatePickPoint()
  const updatePP = useUpdatePickPoint()
  const movePP = useMovePickPoint()
  const deletePP = useDeletePickPoint()
  const colors = useCanvasColors()
  const canManageStructure = useAuth((s) => s.hasRole('Manager'))

  const [selectedShelf, setSelectedShelf] = useState<string | null>(null)
  const [selectedWall, setSelectedWall] = useState<string | null>(null)
  const [selectedPickPoint, setSelectedPickPoint] = useState<string | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [pendingDelete, setPendingDelete] = useState<PendingDelete | null>(null)
  const [mode, setMode] = useState<EditorMode>('select')
  const [pendingPoints, setPendingPoints] = useState<{ x: number; y: number }[]>([])
  const [nextPickPointType, setNextPickPointType] = useState<PickPointType>('Start')
  const [cursor, setCursor] = useState<SnapResult | null>(null)
  const { message: statusMsg, flash } = useFlashMessage()
  // Heatmap-Overlay: zeigt direkt im Canvas wo gepickt wird. Per-Bin-Daten
  // werden auf Regal-Ebene aggregiert (Bins haben keine eigene xy-Position auf
  // dem Canvas — sie hängen am Shelf). Lazy: nur fetchen wenn an.
  const [heatOn, setHeatOn] = useState(false)
  const [heatRange, setHeatRange] = useState(7)
  const heatmap = useBinHeatmap(heatRange, heatOn)

  // ---- Abgeleitete Daten (memoisiert: stabile Referenzen, damit die Callbacks unten wirklich stabil sind
  // und pro Render nicht O(Regale × Bins) gefiltert wird)
  const warehouses = useMemo(() => layout.data ?? [], [layout.data])
  // Multi-warehouse: prefer the active selection, fall back to first warehouse.
  // Walls and PickPoints get filtered to the same warehouse so the canvas only
  // shows what belongs to the active site.
  const activeWarehouseId = useActiveWarehouse((s) => s.activeId)
  const primaryWarehouse = useMemo(
    () => warehouses.find((w) => w.id === activeWarehouseId) ?? warehouses[0] ?? null,
    [warehouses, activeWarehouseId],
  )
  const scopeId = primaryWarehouse?.id
  const ppList: PickPointDto[] = useMemo(
    () => (pickPoints.data ?? []).filter((p) => !scopeId || p.warehouseId === scopeId),
    [pickPoints.data, scopeId],
  )
  const rawWalls: WallDto[] = useMemo(
    () => (walls.data ?? []).filter((w) => !scopeId || w.warehouseId === scopeId),
    [walls.data, scopeId],
  )
  // Defensive: filter out walls without a usable polyline. Catches the case
  // where the backend hasn't been restarted yet and still returns the old
  // start/end shape (then w.points is undefined).
  const wallsList: WallDto[] = useMemo(
    () => rawWalls.filter((w) => Array.isArray(w.points) && w.points.length >= 2),
    [rawWalls],
  )
  const wallDtoDrift = rawWalls.length > 0 && wallsList.length === 0
  const bins: StorageLocationDto[] = useMemo(() => locations.data ?? [], [locations.data])
  const shelves: ShelfDto[] = useMemo(
    () => warehouses.flatMap((w) => w.zones).flatMap((z) => z.aisles).flatMap((a) => a.shelves),
    [warehouses],
  )
  // Bins je Regal einmal zählen statt pro Regal und Render über alle Bins zu filtern.
  const binCountByShelf = useMemo(() => {
    const counts = new Map<string, number>()
    for (const b of bins) counts.set(b.shelfId, (counts.get(b.shelfId) ?? 0) + 1)
    return counts
  }, [bins])

  // ---- Heatmap-Aggregation auf Shelf-Ebene
  // BinHeatPoints kommen per-bin. Bins kennen ihren shelfId — daher pro
  // Shelf die Pickzahlen aufsummieren. Max-Wert dient als Skalierungs-Basis
  // für die HSL-Farbverteilung (grün → gelb → rot).
  const { shelfHeat, shelfHeatMax } = useMemo(() => {
    const perShelf = new Map<string, number>()
    if (heatOn) {
      const binPickById = new Map<string, number>()
      for (const h of heatmap.data ?? []) binPickById.set(h.binId, h.pickCount)
      for (const b of bins) {
        const v = binPickById.get(b.id) ?? 0
        if (v <= 0) continue
        perShelf.set(b.shelfId, (perShelf.get(b.shelfId) ?? 0) + v)
      }
    }
    return { shelfHeat: perShelf, shelfHeatMax: perShelf.size > 0 ? Math.max(...perShelf.values()) : 0 }
  }, [heatOn, heatmap.data, bins])

  // Mutationsfunktionen sind stabil (das Ergebnisobjekt von useMutation ist es nicht).
  const createPickPoint = createPP.mutate
  const createWallMutate = createWall.mutate

  const updateCursor = useCallback((next: SnapResult | null) => {
    // Gleiche Position → gleicher State → kein Neuaufbau der Seite (der Cursor rastet ins 100-mm-Raster ein).
    setCursor((cur) => (sameCursor(cur, next) ? cur : next))
  }, [])

  // ---- Stable callbacks BEFORE early returns (Rules of Hooks)

  const handleStageClick = useCallback((worldX: number, worldY: number) => {
    if (!primaryWarehouse) return

    if (mode === 'drawWall') {
      const snapped = snapToVertex(worldX, worldY, wallsList, pendingPoints)
      setPendingPoints((cur) => [...cur, { x: snapped.x, y: snapped.y }])
      return
    }

    if (mode === 'placePickPoint') {
      const x = snap(worldX)
      const y = snap(worldY)
      const sameTypeCount = ppList.filter(p => p.type === nextPickPointType).length + 1
      const label = `${nextPickPointType} ${sameTypeCount}` // Bezeichnung als Daten (Start 1, End 2 …), nicht übersetzt
      updateCursor(null)
      createPickPoint({
        warehouseId: primaryWarehouse.id,
        label,
        type: nextPickPointType,
        position: { xMm: x, yMm: y, zMm: 0 },
      }, {
        onSuccess: (p) => { flash(t('warehouse:editor.pickPointCreated', { label: p.label })); setMode('select') },
        onError: (e) => flash(errorLine(e)),
      })
    }
  }, [mode, primaryWarehouse, wallsList, pendingPoints, ppList, nextPickPointType, createPickPoint, flash, updateCursor, t])

  // Beendet die Wand (Doppelklick, Doppeltipp oder "Wand fertig"). Der Entwurf wird aus dem State gelesen und die
  // Wand SEQUENZIELL angelegt — nicht innerhalb eines setState-Updaters: Updater müssen rein sein, React ruft sie im
  // StrictMode zweimal auf (das hatte im Dev-Modus doppelte Wände erzeugt).
  const finishWall = useCallback(() => {
    if (mode !== 'drawWall' || !primaryWarehouse) return

    const pts = finalizeWallPoints(pendingPoints)
    setPendingPoints([])
    updateCursor(null)
    if (pts.length < 2) {
      flash(t('warehouse:editor.wallDiscarded'))
      return
    }
    createWallMutate(
      { warehouseId: primaryWarehouse.id, label: null, points: toPositions(pts), thicknessMm: 100 },
      {
        onSuccess: (w) => {
          const segs = w.points.length - 1
          flash(t('warehouse:editor.wallCreated', { points: w.points.length, count: segs }))
          setMode('select')
        },
        onError: (e) => flash(errorLine(e)),
      }
    )
  }, [mode, primaryWarehouse, pendingPoints, createWallMutate, flash, updateCursor, t])

  const handleWorldMouseMove = useCallback((wx: number, wy: number) => {
    if (mode === 'drawWall') {
      updateCursor(snapToVertex(wx, wy, wallsList, pendingPoints))
    } else if (mode === 'placePickPoint') {
      updateCursor({ x: snap(wx), y: snap(wy), snapped: false })
    } else {
      updateCursor(null)
    }
  }, [mode, wallsList, pendingPoints, updateCursor])

  // ---- Early returns AFTER all hooks

  if (layout.isLoading || locations.isLoading || walls.isLoading || pickPoints.isLoading) {
    return <LoadState isLoading error={null} what={t('warehouse:editor.what')} />
  }
  if (layout.error && !layout.data) {
    return <LoadState isLoading={false} error={layout.error} what={t('warehouse:editor.what')} onRetry={() => void layout.refetch()} />
  }

  // Fehler der übrigen Abfragen (Wände, Pickpunkte, Lagerplätze) nicht still als "leer" darstellen.
  const queryError = walls.error ?? pickPoints.error ?? locations.error ?? layout.error
  const retryQueries = () => {
    void layout.refetch(); void walls.refetch(); void pickPoints.refetch(); void locations.refetch()
  }

  // Noch kein Lager (leere Datenbank ohne Demodaten): statt einer leeren Zeichenfläche der Weg zur Lagerstruktur.
  if (layout.data && warehouses.length === 0 && !queryError) {
    return (
      <>
        <h2>{t('warehouse:editor.title')}</h2>
        <div className="card">
          <h3 style={{ marginTop: 0 }}>{t('warehouse:selector.none')}</h3>
          <p className="muted">
            {t('warehouse:editor.emptyBody')}
          </p>
          {canManageStructure
            ? <Link className="button primary" to="/warehouses">{t('warehouse:editor.toStructure')}</Link>
            : <p>{t('warehouse:newShelf.noWarehouseOther')}</p>}
        </div>
      </>
    )
  }

  const selShelf = shelves.find((s) => s.id === selectedShelf) ?? null
  const selWall = wallsList.find((w) => w.id === selectedWall) ?? null
  const selPP = ppList.find((p) => p.id === selectedPickPoint) ?? null
  const selShelfBins = selShelf ? bins.filter((b) => b.shelfId === selShelf.id).sort((a, b) => a.code.localeCompare(b.code)) : []

  const heatColor = (count: number): string => {
    if (shelfHeatMax <= 0 || count <= 0) return colors.shelfFill
    // Skala: 0 → grün (120°), 1.0 → rot (0°). Helligkeit 50%, Sättigung 70%.
    const ratio = Math.min(1, count / shelfHeatMax)
    const hue = 120 - ratio * 120
    return `hsl(${hue}, 70%, 55%)`
  }

  const clearSelection = () => { setSelectedShelf(null); setSelectedWall(null); setSelectedPickPoint(null) }

  const handleShelfDragEnd = (s: ShelfDto, newX: number, newY: number) => {
    const position = { xMm: snap(newX), yMm: snap(newY), zMm: s.position.zMm }
    if (position.xMm === s.position.xMm && position.yMm === s.position.yMm) return
    moveShelf.mutate({ id: s.id, position }, {
      onSuccess: () => flash(t('warehouse:editor.shelfSaved', { code: s.code })),
      onError: (e) => flash(errorLine(e)),
    })
  }

  const handleAddBin = () => {
    if (!selShelf) return
    // Lagerplatz-Codes sind im ganzen System eindeutig: der Vorschlag überspringt Codes, die ein anderes Regal schon trägt.
    const code = nextFreeBinCode(selShelf.code, selShelfBins.map((b) => b.code), allBinCodes(warehouses))
    addBin.mutate({ shelfId: selShelf.id, req: { code, widthMm: 600, depthMm: 600, heightMm: 500, maxWeightGrams: 50_000 } }, {
      onSuccess: (b) => flash(t('warehouse:editor.binCreated', { code: b.code })),
      onError: (e) => flash(errorLine(e)),
    })
  }

  // Löschen fragt per ConfirmDialog nach (statt window.confirm); ausgeführt wird in runDelete.
  const runDelete = () => {
    const target = pendingDelete
    setPendingDelete(null)
    if (!target) return
    if (target.kind === 'bin') {
      const b = target.bin
      deleteBin.mutate(b.id, { onSuccess: () => flash(t('warehouse:editor.binDeleted', { code: b.code })), onError: (e) => flash(errorLine(e)) })
    } else if (target.kind === 'wall') {
      deleteWall.mutate(target.wall.id, {
        onSuccess: () => { flash(t('warehouse:editor.wallDeleted')); setSelectedWall(null) },
        onError: (e) => flash(errorLine(e)),
      })
    } else {
      const p = target.point
      deletePP.mutate(p.id, {
        onSuccess: () => { flash(t('warehouse:editor.pointDeleted', { label: p.label })); setSelectedPickPoint(null) },
        onError: (e) => flash(errorLine(e)),
      })
    }
  }

  const handleWallPointDragEnd = (w: WallDto, pointIdx: number, nx: number, ny: number) => {
    const snapped = snapToVertex(nx, ny, wallsList, [], `${w.id}#${pointIdx}`)
    const newPts: PositionDto[] = w.points.map((p, i) =>
      i === pointIdx
        ? { xMm: snapped.x, yMm: snapped.y, zMm: p.zMm }
        : p
    )
    updateWallPoints.mutate({ id: w.id, req: { points: newPts } }, {
      onSuccess: () => flash(snapped.snapped ? t('warehouse:editor.pointSnapped') : t('warehouse:editor.wallUpdated')),
      onError: (e) => flash(errorLine(e)),
    })
  }

  const handlePickPointDragEnd = (p: PickPointDto, nx: number, ny: number) => {
    const position = { xMm: snap(nx), yMm: snap(ny), zMm: p.position.zMm }
    if (position.xMm === p.position.xMm && position.yMm === p.position.yMm) return
    movePP.mutate({ id: p.id, position }, {
      onSuccess: () => flash(t('warehouse:editor.pickPointMoved', { label: p.label })),
      onError: (e) => flash(errorLine(e)),
    })
  }

  const startPlacePickPoint = (type: PickPointType) => {
    setNextPickPointType(type)
    setMode('placePickPoint')
    setPendingPoints([])
    updateCursor(null)
    clearSelection()
  }

  const startDrawWall = () => {
    setMode('drawWall')
    setPendingPoints([])
    updateCursor(null)
    clearSelection()
  }

  const exitMode = () => { setMode('select'); setPendingPoints([]); updateCursor(null) }

  const trackMouse = mode === 'drawWall' || mode === 'placePickPoint'
    ? handleWorldMouseMove
    : undefined

  const canvasCursor = mode === 'select' ? 'default' : 'crosshair'

  const deleteTitle = pendingDelete?.kind === 'bin' ? t('warehouse:editor.deleteBinTitle', { code: pendingDelete.bin.code })
    : pendingDelete?.kind === 'wall' ? t('warehouse:editor.deleteWallTitle')
      : pendingDelete?.kind === 'pickpoint' ? t('warehouse:editor.deletePickPointTitle', { label: pendingDelete.point.label }) : ''
  const errorPrefix = t('warehouse:editor.errorPrefix')

  return (
    <>
      <h2>{t('warehouse:editor.title')}</h2>
      {canManageStructure && (
        <p className="muted" style={{ marginTop: -8 }}>
          <Trans i18nKey="warehouse:editor.structureHint" components={{ a: <Link to="/warehouses" /> }} />
        </p>
      )}
      {wallDtoDrift && (
        <div className="error" role="alert" style={{ marginBottom: 12 }}>
          <Trans i18nKey="warehouse:editor.drift" components={{ code: <code /> }} />
        </div>
      )}
      <ErrorBanner error={queryError} title={t('warehouse:editor.loadFailed')} onRetry={retryQueries} />

      <LayoutToolbar
        mode={mode}
        pendingCount={pendingPoints.length}
        nextPickPointType={nextPickPointType}
        hasWarehouse={primaryWarehouse !== null}
        heatOn={heatOn}
        heatRange={heatRange}
        heatFetching={heatmap.isFetching}
        cursor={cursor}
        onNewShelf={() => setDialogOpen(true)}
        onToggleDrawWall={() => (mode === 'drawWall' ? exitMode() : startDrawWall())}
        onFinishWall={finishWall}
        onPlacePickPoint={startPlacePickPoint}
        onExitMode={exitMode}
        onToggleHeat={() => setHeatOn(!heatOn)}
        onHeatRange={setHeatRange}
      />

      <div className="card">
        <p className="muted">
          <Trans i18nKey="warehouse:editor.canvasHint" components={{ strong: <strong /> }} />
        </p>
        <WarehouseCanvas
          height={500}
          gridStepMm={500}
          cursorStyle={canvasCursor}
          onStageClick={handleStageClick}
          onStageDoubleClick={finishWall}
          onWorldMouseMove={trackMouse}
        >
          {(vp) => {
            const px = (v: number) => v / vp.scale
            return (
              <>
                {wallsList.map((w) => {
                  const isSel = w.id === selectedWall
                  const flat: number[] = []
                  for (const p of w.points) { flat.push(p.xMm, p.yMm) }
                  // Klick (Maus) und Tap (Touch) wählen die Wand aus — Konva feuert für Berührungen kein click.
                  const selectWall = (e: Konva.KonvaEventObject<Event>) => {
                    if (mode !== 'select') return
                    e.cancelBubble = true
                    clearSelection(); setSelectedWall(w.id)
                  }
                  return (
                    <Group key={w.id}>
                      <Line
                        points={flat}
                        stroke={isSel ? colors.selection : colors.wall}
                        strokeWidth={Math.max(w.thicknessMm, px(3))}
                        hitStrokeWidth={Math.max(w.thicknessMm * 2, px(20))}
                        lineCap="square"
                        lineJoin="round"
                        perfectDrawEnabled={false}
                        shadowForStrokeEnabled={false}
                        onClick={selectWall}
                        onTap={selectWall}
                      />
                      {isSel && w.points.map((p, idx) => (
                        <Circle key={idx} x={p.xMm} y={p.yMm} radius={px(6)}
                          fill={colors.selection} stroke={colors.selectionHandleStroke} strokeWidth={px(1)} draggable
                          perfectDrawEnabled={false}
                          onDragEnd={(e) => handleWallPointDragEnd(w, idx, e.target.x(), e.target.y())}
                        />
                      ))}
                    </Group>
                  )
                })}

                {/* Live-Vorschau: bestätigte pending Segmente */}
                {mode === 'drawWall' && pendingPoints.length >= 2 && (
                  <Line
                    points={pendingPoints.flatMap((p) => [p.x, p.y])}
                    stroke={colors.draft}
                    strokeWidth={Math.max(100, px(2))}
                    lineCap="square"
                    lineJoin="round"
                    listening={false}
                    perfectDrawEnabled={false}
                  />
                )}
                {/* Live-Vorschau: aktives Segment vom letzten Punkt zum Cursor */}
                {mode === 'drawWall' && pendingPoints.length > 0 && cursor && (
                  <Line
                    points={[
                      pendingPoints[pendingPoints.length - 1].x,
                      pendingPoints[pendingPoints.length - 1].y,
                      cursor.x, cursor.y,
                    ]}
                    stroke={colors.draft}
                    strokeWidth={Math.max(100, px(2))}
                    dash={[px(6), px(4)]}
                    lineCap="square"
                    listening={false}
                    perfectDrawEnabled={false}
                  />
                )}
                {/* Pending-Punkt-Marker */}
                {mode === 'drawWall' && pendingPoints.map((p, i) => (
                  <Circle key={i} x={p.x} y={p.y} radius={px(4)}
                    fill={i === 0 ? colors.draftStart : colors.draft}
                    listening={false} perfectDrawEnabled={false}
                  />
                ))}
                {/* Cursor-Marker + Snap-Ring */}
                {mode === 'drawWall' && cursor && (
                  <>
                    <Circle x={cursor.x} y={cursor.y} radius={px(4)}
                      fill={cursor.snapped ? colors.cursorDotSnapped : colors.cursorDot}
                      opacity={0.85}
                      listening={false} perfectDrawEnabled={false}
                    />
                    {cursor.snapped && (
                      <Circle x={cursor.x} y={cursor.y} radius={px(10)}
                        stroke={colors.snapRing} strokeWidth={px(2)}
                        listening={false} perfectDrawEnabled={false}
                      />
                    )}
                  </>
                )}

                {/* Live-Vorschau: Pickpunkt-Ghost */}
                {mode === 'placePickPoint' && cursor && (
                  <RegularPolygon
                    x={cursor.x} y={cursor.y}
                    sides={nextPickPointType === 'Both' ? 6 : nextPickPointType === 'Start' ? 3 : 4}
                    radius={px(10)}
                    fill={pickPointColor(colors, nextPickPointType)}
                    opacity={0.45}
                    rotation={nextPickPointType === 'End' ? 45 : 0}
                    listening={false}
                    perfectDrawEnabled={false}
                  />
                )}

                {shelves.map((s) => {
                  const isSel = s.id === selectedShelf
                  const binCount = binCountByShelf.get(s.id) ?? 0
                  const picks = shelfHeat.get(s.id) ?? 0
                  const fill = heatOn
                    ? (isSel ? colors.shelfFillSelected : heatColor(picks))
                    : (isSel ? colors.shelfFillSelected : colors.shelfFill)
                  const label = heatOn
                    ? t('warehouse:editor.shelfPicks', { code: s.code, count: picks })
                    : t('warehouse:editor.shelfBins', { code: s.code, count: binCount })
                  const selectShelf = (e: Konva.KonvaEventObject<Event>) => {
                    if (mode !== 'select') return
                    e.cancelBubble = true
                    clearSelection(); setSelectedShelf(s.id)
                  }
                  return (
                    <Group
                      key={s.id}
                      x={s.position.xMm} y={s.position.yMm}
                      draggable={mode === 'select'}
                      onClick={selectShelf}
                      onTap={selectShelf}
                      onDragEnd={(e) => handleShelfDragEnd(s, e.target.x(), e.target.y())}
                    >
                      <Rect
                        width={s.widthMm} height={s.depthMm}
                        fill={fill}
                        stroke={isSel ? colors.selection : colors.shelfStroke}
                        strokeWidth={px(2)}
                        perfectDrawEnabled={false}
                        shadowForStrokeEnabled={false}
                      />
                      <Text x={px(4)} y={px(4)}
                        text={label}
                        fontSize={px(11)} fill={heatOn ? colors.shelfTextOnHeat : colors.shelfText} listening={false}
                        perfectDrawEnabled={false}
                      />
                    </Group>
                  )
                })}

                {ppList.map((p) => {
                  const isSel = p.id === selectedPickPoint
                  const color = pickPointColor(colors, p.type)
                  const selectPickPoint = (e: Konva.KonvaEventObject<Event>) => {
                    if (mode !== 'select') return
                    e.cancelBubble = true
                    clearSelection(); setSelectedPickPoint(p.id)
                  }
                  return (
                    <Group
                      key={p.id}
                      x={p.position.xMm} y={p.position.yMm}
                      draggable={mode === 'select'}
                      onClick={selectPickPoint}
                      onTap={selectPickPoint}
                      onDragEnd={(e) => handlePickPointDragEnd(p, e.target.x(), e.target.y())}
                    >
                      <RegularPolygon
                        sides={p.type === 'Both' ? 6 : p.type === 'Start' ? 3 : 4}
                        radius={px(10)}
                        fill={color}
                        stroke={isSel ? colors.selection : colors.pickOutline}
                        strokeWidth={px(isSel ? 3 : 1.5)}
                        rotation={p.type === 'End' ? 45 : 0}
                        perfectDrawEnabled={false}
                        shadowForStrokeEnabled={false}
                      />
                      <Text
                        x={px(12)} y={px(-6)}
                        text={p.label}
                        fontSize={px(11)}
                        fill={color}
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
      </div>

      {selPP && (
        <PickPointPanel
          key={selPP.id}
          pickPoint={selPP}
          onClose={() => setSelectedPickPoint(null)}
          onUpdate={(req) => updatePP.mutate({ id: selPP.id, req })}
          onDelete={() => setPendingDelete({ kind: 'pickpoint', point: selPP })}
        />
      )}

      {selWall && (
        <WallPanel
          wall={selWall}
          onClose={() => setSelectedWall(null)}
          onDelete={() => setPendingDelete({ kind: 'wall', wall: selWall })}
        />
      )}

      {selShelf && (
        <ShelfPanel
          shelf={selShelf}
          bins={selShelfBins}
          addPending={addBin.isPending}
          deletePending={deleteBin.isPending}
          onClose={() => setSelectedShelf(null)}
          onAddBin={handleAddBin}
          onDeleteBin={(b) => setPendingDelete({ kind: 'bin', bin: b })}
        />
      )}

      {/* Status message lives just above the legend so it doesn't push the canvas around on save */}
      <div style={{ minHeight: 36, marginBottom: 8 }}>
        {statusMsg && (
          <div
            className={statusMsg.startsWith(errorPrefix) ? 'error' : 'success'}
            role={statusMsg.startsWith(errorPrefix) ? 'alert' : 'status'}
            style={{ margin: 0 }}
          >
            {statusMsg}
          </div>
        )}
      </div>

      <LayoutLegend colors={colors} heatOn={heatOn} heatRange={heatRange} heatMax={shelfHeatMax} />

      <NewShelfDialog open={dialogOpen} onClose={() => setDialogOpen(false)} onCreated={flash} />

      <ConfirmDialog
        open={pendingDelete !== null}
        title={deleteTitle}
        confirmLabel={t('common:delete')}
        danger
        onConfirm={runDelete}
        onCancel={() => setPendingDelete(null)}
      >
        {pendingDelete?.kind === 'bin'
          ? <p>{t('warehouse:editor.deleteBinHint')}</p>
          : <p>{t('warehouse:structure.deleteIrreversible')}</p>}
      </ConfirmDialog>
    </>
  )
}
