import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import {
  blockedCellsOf, useGridLayout, useSaveGridLayout, type GridCellDto, type GridCellType,
} from '../../api/gridLayoutHooks'
import { usePickPoints, useWalls } from '../../api/hooks'
import type { WarehouseDto } from '../../api/types'
import { getErrorCode } from '../../api/errors'
import { dismissToastsForError, showToast } from '../../state/toasts'
import { useAuth } from '../../state/auth'
import { ConfirmDialog } from '../ConfirmDialog'
import { ErrorBanner } from '../ErrorBanner'
import { LoadState } from '../LoadState'
import { GridSheet, type CellPos, type GridOverlay } from './GridSheet'
import { GridSettingsDialog } from './GridSettingsDialog'
import { CellTypeChooser } from './CellTypeChooser'
import {
  CELL_TYPES, cellKey, changedCellCount, clampContainers, fromDto, isStorage, normalizeRect, paint, parseKey, rectRef,
  rectSize, setContainers, settingsChanged, summarize, toRequest, type CellRect, type GridDraft, type GridSettings,
} from './gridModel'
import './gridEditor.css'

interface History {
  past: GridDraft[]
  present: GridDraft
  future: GridDraft[]
}

const MAX_UNDO = 100
const ZOOM_STEPS = [14, 18, 22, 26, 32, 40, 48] as const
const DEFAULT_ZOOM = 3

interface Props {
  warehouse: WarehouseDto
  /** Meldet ungespeicherte Änderungen (die Seite fragt dann vor dem Wechsel in den erweiterten Editor). */
  onDirtyChange?: (dirty: boolean) => void
}

/// <summary>
/// Einfacher Layout-Editor: das Lager als Raster wie in einer Tabelle. Feldart in der Palette wählen und ins Raster ziehen,
/// oder Felder markieren und per Rechtsklick/Doppelklick/Enter die Art wählen. Lagerfelder (Regal, Packpuffer, Warenausgang,
/// Retouren) haben X Behälter. Änderungen sind ein Entwurf mit Rückgängig/Wiederholen; erst "Speichern" schickt das Raster
/// an den Server, der daraus Regale, Behälter, Wände und Pickpunkte erzeugt. Ohne Rolle Manager: nur ansehen.
/// </summary>
export function GridEditor({ warehouse, onDirtyChange }: Props) {
  const { t, i18n } = useTranslation()
  const grid = useGridLayout(warehouse.id)
  const save = useSaveGridLayout(warehouse.id)
  const walls = useWalls()
  const pickPoints = usePickPoints()
  const canEdit = useAuth((s) => s.hasRole('Manager'))

  const [history, setHistory] = useState<History | null>(null)
  const [saved, setSaved] = useState<GridDraft | null>(null)
  const [syncedKey, setSyncedKey] = useState<string | null>(null)
  const [selection, setSelection] = useState<CellRect | null>(null)
  const [cursor, setCursor] = useState<CellPos | null>(null)
  const [tool, setTool] = useState<GridCellType | null>(null)
  const [containers, setContainersInput] = useState(4)
  const [zoom, setZoom] = useState(DEFAULT_ZOOM)
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const [blocked, setBlocked] = useState<ReadonlySet<string>>(new Set())
  const [saveError, setSaveError] = useState<unknown>(null)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [confirmDiscard, setConfirmDiscard] = useState(false)
  const [showOverlay, setShowOverlay] = useState(true)

  const present = history?.present ?? null
  const dirty = !!(present && saved && (changedCellCount(saved, present) > 0 || settingsChanged(saved, present)))
  const readOnly = !canEdit

  // Server-Stand übernehmen: beim ersten Laden und nach jedem Speichern (neue Version) - nie über einen offenen Entwurf hinweg.
  const data = grid.data
  const dataKey = data ? `${data.warehouseId}:${data.version ?? 'neu'}` : null
  if (data && dataKey !== syncedKey && !dirty) {
    const draft = fromDto(data)
    setSyncedKey(dataKey)
    setSaved(draft)
    setHistory({ past: [], present: draft, future: [] })
  }

  useEffect(() => { onDirtyChange?.(dirty) }, [dirty, onDirtyChange])

  // Ungespeicherte Änderungen: der Browser fragt vor dem Schließen/Neuladen.
  useEffect(() => {
    if (!dirty) return
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault() }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const savedCells = useMemo(() => {
    const map = new Map<string, GridCellDto>()
    for (const c of data?.cells ?? []) map.set(cellKey(c.col, c.row), c)
    return map
  }, [data])

  const shelvesById = useMemo(() => {
    const map = new Map<string, WarehouseDto['zones'][number]['aisles'][number]['shelves'][number]>()
    for (const zone of warehouse.zones) for (const aisle of zone.aisles) for (const shelf of aisle.shelves) map.set(shelf.id, shelf)
    return map
  }, [warehouse])

  // Was der erweiterte Editor gezeichnet hat (nicht vom Raster erzeugt): zur Orientierung über dem Raster.
  const overlay: GridOverlay | null = useMemo(() => {
    if (!data) return null
    const managedShelves = new Set(data.managedShelfIds)
    const managedWalls = new Set(data.managedWallIds)
    const managedPoints = new Set(data.managedPickPointIds)
    const result: GridOverlay = {
      shelves: [...shelvesById.values()].filter((s) => !managedShelves.has(s.id))
        .map((s) => ({ id: s.id, x: s.position.xMm, y: s.position.yMm, w: s.widthMm, d: s.depthMm })),
      walls: (walls.data ?? []).filter((w) => w.warehouseId === warehouse.id && !managedWalls.has(w.id) && (w.points?.length ?? 0) >= 2)
        .map((w) => ({ id: w.id, points: w.points.map((p) => ({ x: p.xMm, y: p.yMm })), thickness: w.thicknessMm })),
      pickPoints: (pickPoints.data ?? []).filter((p) => p.warehouseId === warehouse.id && !managedPoints.has(p.id))
        .map((p) => ({ id: p.id, x: p.position.xMm, y: p.position.yMm })),
    }
    return result.shelves.length + result.walls.length + result.pickPoints.length > 0 ? result : null
  }, [data, shelvesById, walls.data, pickPoints.data, warehouse.id])
  const unmanagedCount = overlay ? overlay.shelves.length + overlay.walls.length + overlay.pickPoints.length : 0

  const sheetRef = useRef<HTMLDivElement>(null)

  if (!present || !saved || !data) {
    return <LoadState isLoading={grid.isLoading} error={grid.error} what={t('gridLayout:what')} onRetry={() => void grid.refetch()} />
  }

  // ---- Bearbeiten ---------------------------------------------------------------------------------------------------

  const apply = (next: GridDraft) => {
    if (next === present) return
    setHistory((h) => h && { past: [...h.past, h.present].slice(-MAX_UNDO), present: next, future: [] })
  }
  const undo = () => setHistory((h) => (h && h.past.length > 0
    ? { past: h.past.slice(0, -1), present: h.past[h.past.length - 1], future: [h.present, ...h.future] }
    : h))
  const redo = () => setHistory((h) => (h && h.future.length > 0
    ? { past: [...h.past, h.present], present: h.future[0], future: h.future.slice(1) }
    : h))

  const select = (rect: CellRect, at: CellPos) => { setSelection(rect); setCursor(at) }
  const paintRect = (rect: CellRect) => {
    if (tool === null) return
    apply(paint(present, rect, tool, containers))
    select(rect, { col: rect.c2, row: rect.r2 })
  }
  const chooseType = (type: GridCellType) => {
    if (!selection) return
    apply(paint(present, selection, type, containers))
    setMenu(null)
    sheetRef.current?.querySelector<HTMLElement>('.grid-sheet')?.focus({ preventScroll: true })
  }
  const changeContainers = (n: number) => {
    const count = clampContainers(n)
    setContainersInput(count)
    if (selection) apply(setContainers(present, selection, count))
  }
  const applySettings = (settings: GridSettings, next: GridDraft) => {
    apply(next)
    setSettingsOpen(false)
    if (selection) setSelection(null)
    showToast('info', t('gridLayout:settings.applied', { columns: settings.columns, rows: settings.rows }))
  }

  const doSave = () => {
    setSaveError(null)
    save.mutate(toRequest(present, data.version), {
      onSuccess: (result) => {
        setBlocked(new Set())
        // Neuer Server-Stand: der Entwurf wird beim nächsten Render aus der Antwort übernommen.
        setSaved(null)
        setSyncedKey(null)
        setHistory(null)
        const s = result.summary
        showToast('success', t('gridLayout:save.done', {
          created: s.shelvesCreated, removed: s.shelvesRemoved,
          containersCreated: s.containersCreated, containersRemoved: s.containersRemoved,
          walls: s.walls, pickPoints: s.pickPoints,
        }))
      },
      onError: (e) => {
        dismissToastsForError(e)
        setSaveError(e)
        setBlocked(new Set(blockedCellsOf(e).map((c) => cellKey(c.col, c.row))))
      },
    })
  }
  const discard = () => {
    setHistory({ past: [], present: saved, future: [] })
    setBlocked(new Set())
    setSaveError(null)
    setConfirmDiscard(false)
  }
  const reload = () => {
    setHistory(null)
    setSaved(null)
    setSyncedKey(null)
    setSaveError(null)
    setBlocked(new Set())
    void grid.refetch()
  }

  const handleKeys = (e: KeyboardEvent<HTMLDivElement>) => {
    const mod = e.ctrlKey || e.metaKey
    if (mod && e.key.toLowerCase() === 'z' && !e.shiftKey) { e.preventDefault(); undo() }
    else if (mod && (e.key.toLowerCase() === 'y' || (e.key.toLowerCase() === 'z' && e.shiftKey))) { e.preventDefault(); redo() }
    else if (e.key === 'Escape') {
      if (menu) setMenu(null)
      else if (tool !== null) setTool(null)
      else setSelection(null)
    }
  }

  // ---- Anzeige ------------------------------------------------------------------------------------------------------

  const totals = summarize(present)
  const storageCells = [...totals.byType.entries()].filter(([type]) => isStorage(type)).reduce((n, [, count]) => n + count, 0)
  const changed = changedCellCount(saved, present)
  const locale = i18n.language
  const meters = (mm: number) => (mm / 1000).toLocaleString(locale, { maximumFractionDigits: 2 })
  const selectionSummary = selection ? summarize(present, selection) : null
  const singleCell = selection && rectSize(selection) === 1 ? normalizeRect(selection) : null
  const singleKey = singleCell ? cellKey(singleCell.c1, singleCell.r1) : null
  const singleDraft = singleKey ? present.cells.get(singleKey) : undefined
  const singleSaved = singleKey ? savedCells.get(singleKey) : undefined
  const singleShelf = singleSaved?.shelfId && singleSaved.type === singleDraft?.type ? shelvesById.get(singleSaved.shelfId) : undefined
  const errorCode = saveError ? getErrorCode(saveError) : undefined

  return (
    <div className="grid-editor" ref={sheetRef} onKeyDown={handleKeys}>
      {canEdit && (
        <div className="grid-toolbar" role="toolbar" aria-label={t('gridLayout:toolbar.label')}>
          <div className="grid-palette" role="radiogroup" aria-label={t('gridLayout:toolbar.tools')}>
            <button
              type="button"
              role="radio"
              aria-checked={tool === null}
              className={`grid-tool${tool === null ? ' is-active' : ''}`}
              onClick={() => setTool(null)}
              title={t('gridLayout:toolbar.selectHint')}
            >
              <span className="grid-tool__swatch grid-tool__swatch--select" aria-hidden="true">⬚</span>
              {t('gridLayout:toolbar.select')}
            </button>
            {CELL_TYPES.map((type) => (
              <button
                key={type}
                type="button"
                role="radio"
                aria-checked={tool === type}
                className={`grid-tool${tool === type ? ' is-active' : ''}`}
                onClick={() => setTool(tool === type ? null : type)}
                title={t(`gridLayout:hint.${type}`)}
              >
                <span className={`grid-tool__swatch grid-cell--${type.toLowerCase()}`} aria-hidden="true">
                  {isStorage(type) ? containers : type === 'Walkway' ? '' : t(`gridLayout:glyph.${type}`)}
                </span>
                {t(`gridLayout:type.${type}`)}
              </button>
            ))}
          </div>
          <div className="grid-toolbar__row">
            <ContainerStepper value={containers} onChange={changeContainers} />
            <span className="grid-toolbar__group" role="group" aria-label={t('gridLayout:toolbar.zoom')}>
              <span className="grid-stepper__label" aria-hidden="true">{t('gridLayout:toolbar.zoom')}</span>
              <button type="button" onClick={() => setZoom((z) => Math.max(0, z - 1))} disabled={zoom === 0} aria-label={t('gridLayout:toolbar.zoomOut')}>−</button>
              <button type="button" onClick={() => setZoom((z) => Math.min(ZOOM_STEPS.length - 1, z + 1))} disabled={zoom === ZOOM_STEPS.length - 1} aria-label={t('gridLayout:toolbar.zoomIn')}>+</button>
            </span>
            <span className="grid-toolbar__group">
              <button type="button" onClick={undo} disabled={history!.past.length === 0} title={t('gridLayout:toolbar.undoHint')}>{t('gridLayout:toolbar.undo')}</button>
              <button type="button" onClick={redo} disabled={history!.future.length === 0} title={t('gridLayout:toolbar.redoHint')}>{t('gridLayout:toolbar.redo')}</button>
            </span>
            <button type="button" onClick={() => setSettingsOpen(true)}>{t('gridLayout:toolbar.settings')}</button>
            {overlay && (
              <label className="grid-toolbar__check">
                <input type="checkbox" checked={showOverlay} onChange={(e) => setShowOverlay(e.target.checked)} />
                {t('gridLayout:toolbar.overlay', { count: unmanagedCount })}
              </label>
            )}
          </div>
        </div>
      )}

      <div className={`grid-savebar${dirty ? ' is-dirty' : ''}`} role="status" aria-live="polite">
        <span className="grid-savebar__text">
          {dirty
            ? t('gridLayout:save.pending', { count: changed })
            : data.exists ? t('gridLayout:save.clean') : t('gridLayout:save.new')}
        </span>
        {canEdit && (
          <span className="grid-toolbar__group">
            <button type="button" onClick={() => setConfirmDiscard(true)} disabled={!dirty || save.isPending}>{t('gridLayout:save.discard')}</button>
            <button type="button" className="primary" onClick={doSave} disabled={(!dirty && data.exists) || save.isPending}>
              {save.isPending ? t('common:saving') : t('gridLayout:save.save')}
            </button>
          </span>
        )}
      </div>

      {saveError != null && (
        <ErrorBanner
          error={saveError}
          title={t('gridLayout:save.failed')}
          onDismiss={() => setSaveError(null)}
          onRetry={errorCode === 'grid_conflict' ? reload : undefined}
        />
      )}

      <p className="muted grid-editor__facts">
        {t('gridLayout:facts', {
          columns: present.columns, rows: present.rows, cell: meters(present.cellSizeMm),
          width: meters(present.columns * present.cellSizeMm), depth: meters(present.rows * present.cellSizeMm),
        })}
        {' · '}
        {t('gridLayout:factsStorage', { count: storageCells, containers: totals.containers })}
      </p>

      <div className="grid-editor__body">
        <GridSheet
          draft={present}
          saved={savedCells}
          selection={selection}
          cursor={cursor}
          tool={tool}
          readOnly={readOnly}
          cellPx={ZOOM_STEPS[zoom]}
          blocked={blocked}
          overlay={showOverlay ? overlay : null}
          onSelect={select}
          onPaint={paintRect}
          onOpenMenu={(x, y) => setMenu({ x, y })}
          onClearSelection={() => selection && apply(paint(present, selection, 'Walkway', containers))}
        />

        <aside className="grid-panel card" aria-label={t('gridLayout:panel.label')}>
          {!selection ? (
            <>
              <h3>{t('gridLayout:panel.howTo')}</h3>
              <ul className="grid-panel__help">
                <li><Trans i18nKey="gridLayout:panel.help1" components={{ strong: <strong /> }} /></li>
                <li><Trans i18nKey="gridLayout:panel.help2" components={{ strong: <strong /> }} /></li>
                <li><Trans i18nKey="gridLayout:panel.help3" components={{ strong: <strong /> }} /></li>
                <li><Trans i18nKey="gridLayout:panel.help4" components={{ strong: <strong /> }} /></li>
              </ul>
              {overlay && <p className="muted">{t('gridLayout:panel.overlayHint', { count: unmanagedCount })}</p>}
            </>
          ) : (
            <>
              <h3>
                {rectRef(selection)}
                <span className="muted grid-panel__count"> {t('gridLayout:panel.cells', { count: rectSize(selection) })}</span>
              </h3>
              {singleCell ? (
                <dl className="grid-panel__facts">
                  <dt>{t('gridLayout:panel.type')}</dt>
                  <dd>{t(`gridLayout:type.${singleDraft?.type ?? 'Walkway'}`)}</dd>
                  {singleDraft && isStorage(singleDraft.type) && (
                    <>
                      <dt>{t('gridLayout:panel.containers')}</dt>
                      <dd>{singleDraft.containers}</dd>
                      <dt>{t('gridLayout:panel.shelf')}</dt>
                      <dd>{singleShelf ? <code>{singleShelf.code}</code> : <span className="muted">{t('gridLayout:panel.notSaved')}</span>}</dd>
                    </>
                  )}
                  {singleShelf && singleShelf.locations.length > 0 && (
                    <>
                      <dt>{t('gridLayout:panel.containerCodes')}</dt>
                      <dd className="grid-panel__codes">
                        {[...singleShelf.locations].sort((a, b) => a.position.zMm - b.position.zMm).map((l) => <code key={l.id}>{l.code}</code>)}
                      </dd>
                    </>
                  )}
                  {singleSaved && singleSaved.occupiedContainers > 0 && singleSaved.type === singleDraft?.type && (
                    <>
                      <dt>{t('gridLayout:panel.occupied')}</dt>
                      <dd>{singleSaved.occupiedContainers}</dd>
                    </>
                  )}
                </dl>
              ) : (
                <ul className="grid-panel__mix">
                  {[...(selectionSummary?.byType.entries() ?? [])].map(([type, count]) => (
                    <li key={type}>
                      <span className={`grid-tool__swatch grid-cell--${type.toLowerCase()}`} aria-hidden="true" />
                      {t(`gridLayout:type.${type}`)}: {count}
                    </li>
                  ))}
                </ul>
              )}
              {blockedIn(blocked, selection) && <p className="text-danger">{t('gridLayout:panel.blocked')}</p>}
              {canEdit && (
                <>
                  <h4>{t('gridLayout:panel.setType')}</h4>
                  <CellTypeChooser
                    current={singleDraft?.type ?? (singleCell ? 'Walkway' : null)}
                    containers={containers}
                    onChoose={chooseType}
                    onContainers={changeContainers}
                  />
                </>
              )}
            </>
          )}
        </aside>
      </div>

      {menu && canEdit && selection && (
        <CellTypeChooser
          popup={menu}
          title={rectRef(selection)}
          current={singleDraft?.type ?? (singleCell ? 'Walkway' : null)}
          containers={containers}
          onChoose={chooseType}
          onContainers={changeContainers}
          onClose={() => setMenu(null)}
        />
      )}

      {settingsOpen && (
        <GridSettingsDialog draft={present} onApply={applySettings} onClose={() => setSettingsOpen(false)} />
      )}
      <ConfirmDialog
        open={confirmDiscard}
        title={t('gridLayout:save.discardTitle')}
        confirmLabel={t('gridLayout:save.discard')}
        danger
        onConfirm={discard}
        onCancel={() => setConfirmDiscard(false)}
      >
        {t('gridLayout:save.discardBody', { count: changed })}
      </ConfirmDialog>
    </div>
  )
}

function blockedIn(blocked: ReadonlySet<string>, rect: CellRect): boolean {
  const n = normalizeRect(rect)
  for (const key of blocked) {
    const [col, row] = parseKey(key)
    if (col >= n.c1 && col <= n.c2 && row >= n.r1 && row <= n.r2) return true
  }
  return false
}

/** Behälter je Lagerfeld: gilt beim Malen und für markierte Lagerfelder. */
function ContainerStepper({ value, onChange }: { value: number; onChange: (n: number) => void }) {
  const { t } = useTranslation()
  return (
    <span className="grid-toolbar__group grid-stepper" role="group" aria-label={t('gridLayout:toolbar.containers')}>
      <span className="grid-stepper__label">{t('gridLayout:toolbar.containers')}</span>
      <button type="button" onClick={() => onChange(value - 1)} disabled={value <= 1} aria-label={t('gridLayout:toolbar.fewer')}>−</button>
      <input
        type="number"
        min={1}
        max={50}
        value={value}
        aria-label={t('gridLayout:toolbar.containers')}
        onChange={(e) => { const n = Number(e.target.value); if (Number.isFinite(n) && n >= 1) onChange(n) }}
      />
      <button type="button" onClick={() => onChange(value + 1)} disabled={value >= 50} aria-label={t('gridLayout:toolbar.more')}>+</button>
    </span>
  )
}
