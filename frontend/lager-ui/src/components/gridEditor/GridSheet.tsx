import { memo, useCallback, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { useTranslation } from 'react-i18next'
import type { GridCellDto, GridCellType } from '../../api/gridLayoutHooks'
import {
  cellKey, cellRef, columnName, isStorage, normalizeRect, type CellRect, type DraftCell, type GridDraft,
} from './gridModel'

/** Objekte aus dem erweiterten Editor (nicht vom Raster erzeugt), in mm: zur Orientierung halbtransparent über dem Raster. */
export interface GridOverlay {
  shelves: { id: string; x: number; y: number; w: number; d: number }[]
  walls: { id: string; points: { x: number; y: number }[]; thickness: number }[]
  pickPoints: { id: string; x: number; y: number }[]
}

export interface CellPos {
  col: number
  row: number
}

interface Props {
  draft: GridDraft
  /** Gespeicherter Stand je Feld (Regal-Code, belegte Behälter). */
  saved: ReadonlyMap<string, GridCellDto>
  selection: CellRect | null
  cursor: CellPos | null
  /** null = Auswählen; sonst wird mit dieser Feldart gemalt. */
  tool: GridCellType | null
  readOnly: boolean
  cellPx: number
  blocked: ReadonlySet<string>
  overlay: GridOverlay | null
  onSelect: (rect: CellRect, cursor: CellPos) => void
  onPaint: (rect: CellRect) => void
  /** Feldart-Auswahl am Mauszeiger öffnen (Rechtsklick, Doppelklick, Enter). */
  onOpenMenu: (x: number, y: number) => void
  /** Entf/Rücktaste: Auswahl zu Laufweg. */
  onClearSelection: () => void
}

const HEADER_PX = 32

/// <summary>
/// Das Raster wie eine Tabelle: Spaltenköpfe A, B, C …, Zeilenköpfe 1, 2, 3 …, jedes Feld eingefärbt nach seiner Art.
/// Bedienung: Klicken oder Ziehen wählt Felder (Umschalt erweitert), mit gewählter Feldart malt Ziehen ein Rechteck,
/// Rechtsklick/Doppelklick/Enter öffnet die Feldart-Auswahl, Pfeiltasten bewegen die Auswahl, Entf macht Laufweg daraus.
/// Auswahl und Mal-Vorschau liegen als eigene Ebene über den Feldern, damit beim Ziehen keine 10.000 Felder neu zeichnen.
/// </summary>
export function GridSheet(props: Props) {
  const { draft, saved, selection, cursor, tool, readOnly, cellPx, blocked, overlay } = props
  const { t } = useTranslation()
  const containerRef = useRef<HTMLDivElement>(null)
  const [drag, setDrag] = useState<{ anchor: CellPos; current: CellPos; button: number } | null>(null)

  const cellAt = useCallback((x: number, y: number): CellPos | null => {
    const el = document.elementFromPoint(x, y)?.closest<HTMLElement>('[data-col]')
    if (!el || !containerRef.current?.contains(el)) return null
    return { col: Number(el.dataset.col), row: Number(el.dataset.row) }
  }, [])

  const handlePointerDown = (e: PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return
    const pos = cellAt(e.clientX, e.clientY)
    if (!pos) return
    e.preventDefault()
    containerRef.current?.focus({ preventScroll: true })
    // Umschalt+Klick im Auswahlmodus: Bereich vom bisherigen Cursor bis hier.
    const anchor = e.shiftKey && cursor && tool === null ? cursor : pos
    containerRef.current?.setPointerCapture?.(e.pointerId)
    setDrag({ anchor, current: pos, button: e.button })
  }

  const handlePointerMove = (e: PointerEvent<HTMLDivElement>) => {
    if (!drag) return
    const pos = cellAt(e.clientX, e.clientY)
    if (pos && (pos.col !== drag.current.col || pos.row !== drag.current.row)) setDrag({ ...drag, current: pos })
  }

  const finishDrag = () => {
    if (!drag) return
    const rect: CellRect = { c1: drag.anchor.col, r1: drag.anchor.row, c2: drag.current.col, r2: drag.current.row }
    setDrag(null)
    if (tool !== null && !readOnly) props.onPaint(rect)
    else props.onSelect(rect, drag.current)
  }

  const handleContextMenu = (e: React.MouseEvent<HTMLDivElement>) => {
    const pos = cellAt(e.clientX, e.clientY)
    if (!pos) return
    e.preventDefault()
    if (readOnly) return
    const inSelection = selection && isInside(selection, pos)
    if (!inSelection) props.onSelect({ c1: pos.col, r1: pos.row, c2: pos.col, r2: pos.row }, pos)
    props.onOpenMenu(e.clientX, e.clientY)
  }

  const handleDoubleClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (readOnly || tool !== null) return
    const pos = cellAt(e.clientX, e.clientY)
    if (pos) props.onOpenMenu(e.clientX, e.clientY)
  }

  const handleKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const moves: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }
    const move = moves[e.key]
    if (move) {
      e.preventDefault()
      const from = cursor ?? { col: 0, row: 0 }
      const next = {
        col: Math.min(draft.columns - 1, Math.max(0, from.col + move[0])),
        row: Math.min(draft.rows - 1, Math.max(0, from.row + move[1])),
      }
      const anchor = e.shiftKey && selection ? anchorOf(selection, from) : next
      props.onSelect({ c1: anchor.col, r1: anchor.row, c2: next.col, r2: next.row }, next)
      scrollIntoView(next)
      return
    }
    if (readOnly || !selection) return
    if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault()
      props.onClearSelection()
    } else if (e.key === 'Enter' || e.key === 'F2' || (e.key === 'F10' && e.shiftKey) || e.key === 'ContextMenu') {
      e.preventDefault()
      const target = cursor ?? { col: normalizeRect(selection).c1, row: normalizeRect(selection).r1 }
      const el = containerRef.current?.querySelector<HTMLElement>(`[data-col="${target.col}"][data-row="${target.row}"]`)
      const box = el?.getBoundingClientRect()
      props.onOpenMenu(box ? box.right : 0, box ? box.top : 0)
    }
  }

  const scrollIntoView = (pos: CellPos) => {
    containerRef.current
      ?.querySelector<HTMLElement>(`[data-col="${pos.col}"][data-row="${pos.row}"]`)
      ?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' })
  }

  const preview = drag ? { c1: drag.anchor.col, r1: drag.anchor.row, c2: drag.current.col, r2: drag.current.row } : null
  const highlight = preview ?? selection
  const painting = drag !== null && tool !== null && !readOnly

  const columnHeads = useMemo(() => Array.from({ length: draft.columns }, (_, c) => columnName(c)), [draft.columns])
  const width = draft.columns * cellPx
  const height = draft.rows * cellPx
  const sizeMm = { w: draft.columns * draft.cellSizeMm, h: draft.rows * draft.cellSizeMm }

  return (
    <div
      ref={containerRef}
      className={`grid-sheet${tool !== null && !readOnly ? ' grid-sheet--paint' : ''}`}
      style={{ ['--cell' as string]: `${cellPx}px`, ['--hdr' as string]: `${HEADER_PX}px` }}
      tabIndex={0}
      role="grid"
      aria-label={t('gridLayout:sheet.label', { columns: draft.columns, rows: draft.rows })}
      aria-rowcount={draft.rows + 1}
      aria-colcount={draft.columns + 1}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={finishDrag}
      onPointerCancel={() => setDrag(null)}
      onContextMenu={handleContextMenu}
      onDoubleClick={handleDoubleClick}
      onKeyDown={handleKeyDown}
    >
      <div className="grid-sheet__table" style={{ gridTemplateColumns: `var(--hdr) repeat(${draft.columns}, var(--cell))` }}>
        <div role="row" className="grid-sheet__row">
          <div className="grid-sheet__corner" role="columnheader" aria-hidden="true" />
          {columnHeads.map((name, c) => (
            <div key={name} role="columnheader" className={`grid-sheet__colhead${highlight && inCols(highlight, c) ? ' is-active' : ''}`}>{name}</div>
          ))}
        </div>
        {Array.from({ length: draft.rows }, (_, row) => (
          <div role="row" className="grid-sheet__row" key={row}>
            <div role="rowheader" className={`grid-sheet__rowhead${highlight && inRows(highlight, row) ? ' is-active' : ''}`}>{row + 1}</div>
            {Array.from({ length: draft.columns }, (_, col) => {
              const key = cellKey(col, row)
              return (
                <GridCellView
                  key={key}
                  col={col}
                  row={row}
                  cell={draft.cells.get(key)}
                  saved={saved.get(key)}
                  blocked={blocked.has(key)}
                />
              )
            })}
          </div>
        ))}

        <div className="grid-sheet__layer" style={{ width, height }} aria-hidden="true">
          {overlay && (
            <svg className="grid-sheet__overlay" width={width} height={height} viewBox={`0 0 ${sizeMm.w} ${sizeMm.h}`} preserveAspectRatio="none">
              {overlay.walls.map((w) => (
                <polyline key={w.id} points={w.points.map((p) => `${p.x},${p.y}`).join(' ')} strokeWidth={Math.max(w.thickness, draft.cellSizeMm / 8)} />
              ))}
              {overlay.shelves.map((s) => (
                <rect key={s.id} x={s.x} y={s.y} width={s.w} height={s.d} vectorEffect="non-scaling-stroke" />
              ))}
              {overlay.pickPoints.map((p) => (
                <circle key={p.id} cx={p.x} cy={p.y} r={draft.cellSizeMm / 3} vectorEffect="non-scaling-stroke" />
              ))}
            </svg>
          )}
          {highlight && (
            <div
              className={`grid-sheet__selection${painting ? ` grid-sheet__selection--paint grid-cell--${tool!.toLowerCase()}` : ''}`}
              style={rectStyle(highlight, cellPx)}
            />
          )}
          {cursor && !drag && (
            <div className="grid-sheet__cursor" style={rectStyle({ c1: cursor.col, r1: cursor.row, c2: cursor.col, r2: cursor.row }, cellPx)} />
          )}
        </div>
      </div>
    </div>
  )
}

interface CellProps {
  col: number
  row: number
  cell: DraftCell | undefined
  saved: GridCellDto | undefined
  blocked: boolean
}

/** Ein Feld. memo: beim Malen ändern sich nur die betroffenen Felder, Auswahl und Vorschau liegen auf eigener Ebene. */
const GridCellView = memo(function GridCellView({ col, row, cell, saved, blocked }: CellProps) {
  const { t } = useTranslation()
  const type: GridCellType = cell?.type ?? 'Walkway'
  const storage = isStorage(type)
  // Gespeicherte Angaben gelten nur, solange das Feld dieselbe Art hat.
  const info = saved && saved.type === type ? saved : undefined
  const occupied = storage && info ? info.occupiedContainers : 0
  const typeName = t(`gridLayout:type.${type}`)
  const label = [
    cellRef(col, row),
    typeName,
    storage ? t('gridLayout:sheet.containers', { count: cell?.containers ?? 0 }) : null,
    info?.shelfCode ?? null,
    occupied > 0 ? t('gridLayout:sheet.occupied', { count: occupied }) : null,
    blocked ? t('gridLayout:sheet.blocked') : null,
  ].filter(Boolean).join(' · ')

  return (
    <div
      role="gridcell"
      data-col={col}
      data-row={row}
      className={`grid-cell grid-cell--${type.toLowerCase()}${blocked ? ' is-blocked' : ''}${occupied > 0 ? ' is-occupied' : ''}`}
      title={label}
      aria-label={label}
    >
      {storage ? cell?.containers : type === 'Walkway' ? null : t(`gridLayout:glyph.${type}`)}
    </div>
  )
})

const isInside = (rect: CellRect, pos: CellPos) => {
  const n = normalizeRect(rect)
  return pos.col >= n.c1 && pos.col <= n.c2 && pos.row >= n.r1 && pos.row <= n.r2
}
const inCols = (rect: CellRect, c: number) => { const n = normalizeRect(rect); return c >= n.c1 && c <= n.c2 }
const inRows = (rect: CellRect, r: number) => { const n = normalizeRect(rect); return r >= n.r1 && r <= n.r2 }

/** Die Ecke gegenüber dem Cursor: von dort aus erweitert Umschalt+Pfeil die Auswahl. */
function anchorOf(selection: CellRect, cursor: CellPos): CellPos {
  const n = normalizeRect(selection)
  return { col: cursor.col === n.c1 ? n.c2 : n.c1, row: cursor.row === n.r1 ? n.r2 : n.r1 }
}

function rectStyle(rect: CellRect, cellPx: number) {
  const n = normalizeRect(rect)
  return {
    left: n.c1 * cellPx,
    top: n.r1 * cellPx,
    width: (n.c2 - n.c1 + 1) * cellPx,
    height: (n.r2 - n.r1 + 1) * cellPx,
  }
}
