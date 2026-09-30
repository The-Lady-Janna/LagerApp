import { useTranslation } from 'react-i18next'
import type { PickPointType } from '../../api/types'
import type { SnapResult } from '../../lib/layoutGeometry'

export type EditorMode = 'select' | 'drawWall' | 'placePickPoint'

interface Props {
  mode: EditorMode
  /** Anzahl der bisher gesetzten Punkte der Wand im Entwurf. */
  pendingCount: number
  nextPickPointType: PickPointType
  /** Ohne Lager sind alle Zeichenwerkzeuge gesperrt. */
  hasWarehouse: boolean
  heatOn: boolean
  heatRange: number
  heatFetching: boolean
  /** Cursorposition (nur in den Zeichenmodi). */
  cursor: SnapResult | null
  onNewShelf: () => void
  onToggleDrawWall: () => void
  /** Beendet die Wand (Touch-Ersatz für den Doppelklick). */
  onFinishWall: () => void
  onPlacePickPoint: (type: PickPointType) => void
  onExitMode: () => void
  onToggleHeat: () => void
  onHeatRange: (days: number) => void
}

/// <summary>
/// Werkzeugleiste des Lager-Layout-Editors: Regal anlegen, Wand zeichnen, Pickpunkte setzen, Heatmap.
/// Reine Darstellung — der Zustand (Modus, Entwurf, Auswahl) bleibt in der Seite.
/// </summary>
export function LayoutToolbar({
  mode, pendingCount, nextPickPointType, hasWarehouse, heatOn, heatRange, heatFetching, cursor,
  onNewShelf, onToggleDrawWall, onFinishWall, onPlacePickPoint, onExitMode, onToggleHeat, onHeatRange,
}: Props) {
  const { t } = useTranslation()
  const pickButton = (type: PickPointType, label: string) => (
    <button
      className={mode === 'placePickPoint' && nextPickPointType === type ? 'primary' : ''}
      onClick={() => onPlacePickPoint(type)}
      disabled={!hasWarehouse}
    >{label}</button>
  )

  return (
    <div className="toolbar">
      <button className="primary" onClick={onNewShelf}>{t('warehouse:toolbar.newShelf')}</button>
      <button
        className={mode === 'drawWall' ? 'primary' : ''}
        onClick={onToggleDrawWall}
        disabled={!hasWarehouse}
        aria-pressed={mode === 'drawWall'}
      >
        {mode === 'drawWall'
          ? (pendingCount === 0 ? t('warehouse:toolbar.point1') : t('warehouse:toolbar.pointN', { n: pendingCount + 1 }))
          : t('warehouse:toolbar.drawWall')}
      </button>
      {mode === 'drawWall' && (
        <button onClick={onFinishWall} disabled={pendingCount < 2}>{t('warehouse:toolbar.wallDone')}</button>
      )}
      {pickButton('Start', t('warehouse:toolbar.startPoint'))}
      {pickButton('End', t('warehouse:toolbar.endPoint'))}
      {pickButton('Both', t('warehouse:toolbar.bothPoint'))}
      {mode !== 'select' && <button onClick={onExitMode}>{t('common:cancel')}</button>}
      <span aria-hidden="true" style={{ borderLeft: '1px solid var(--c-border)', height: 24, margin: '0 6px' }} />
      <button
        className={heatOn ? 'primary' : ''}
        onClick={onToggleHeat}
        aria-pressed={heatOn}
        title={t('warehouse:toolbar.heatTitle')}
      >
        {heatOn ? t('warehouse:toolbar.heatOn') : t('warehouse:toolbar.heatOff')}
      </button>
      {heatOn && (
        <select
          aria-label={t('warehouse:toolbar.rangeAria')}
          value={heatRange}
          onChange={(e) => onHeatRange(+e.target.value)}
          title={t('warehouse:toolbar.rangeTitle')}
        >
          <option value={1}>{t('reports:range.d1')}</option>
          <option value={7}>{t('reports:range.d7')}</option>
          <option value={30}>{t('reports:range.d30')}</option>
          <option value={90}>{t('reports:range.d90')}</option>
        </select>
      )}
      {heatOn && heatFetching && <span className="muted" style={{ fontSize: 12 }} role="status">{t('warehouse:toolbar.loading')}</span>}
      {cursor && mode !== 'select' && (
        <span className="muted" style={{ marginLeft: 'auto', fontSize: 12 }}>
          {t('warehouse:toolbar.cursor', { x: cursor.x, y: cursor.y })}{cursor.snapped ? t('warehouse:toolbar.snapped') : ''}
        </span>
      )}
    </div>
  )
}
