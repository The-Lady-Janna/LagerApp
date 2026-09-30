import { Trans, useTranslation } from 'react-i18next'
import type { PickPointDto, PickPointType, ShelfDto, StorageLocationDto, WallDto } from '../../api/types'
import type { CanvasColors } from '../../lib/canvasColors'
import { totalLength } from '../../lib/layoutGeometry'

/// <summary>
/// Detail-Karten des Lager-Layout-Editors (Pickpunkt, Wand, Regal mit Bins) und die Legende. Reine Darstellung:
/// Auswahl und Aktionen kommen als Props aus LayoutEditorPage.
/// </summary>

export function PickPointPanel({ pickPoint, onClose, onUpdate, onDelete }: {
  pickPoint: PickPointDto
  onClose: () => void
  onUpdate: (req: { label: string; type: PickPointType }) => void
  onDelete: () => void
}) {
  const { t } = useTranslation()
  return (
    // Der Aufrufer setzt key={pickPoint.id}: beim Wechsel auf einen anderen Pickpunkt neu einhängen — das Label-Feld
    // (defaultValue) zeigt sonst weiter das Label des vorherigen Pickpunkts, und ein bloßes Verlassen des Feldes würde ihn umbenennen.
    <div className="card">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h3 style={{ margin: 0 }}>{t('warehouse:panel.pickPoint')}</h3>
        <button onClick={onClose}>{t('warehouse:panel.clearSelection')}</button>
      </div>
      <div className="grid-3" style={{ marginTop: 8 }}>
        <label>
          {t('warehouse:panel.label')}
          <input
            defaultValue={pickPoint.label}
            onBlur={(e) => {
              if (e.target.value !== pickPoint.label) onUpdate({ label: e.target.value, type: pickPoint.type })
            }}
          />
        </label>
        <label>
          {t('warehouse:panel.type')}
          <select value={pickPoint.type} onChange={(e) => onUpdate({ label: pickPoint.label, type: e.target.value as PickPointType })}>
            <option value="Start">{t('warehouse:panel.typeStart')}</option>
            <option value="End">{t('warehouse:panel.typeEnd')}</option>
            <option value="Both">{t('warehouse:panel.typeBoth')}</option>
          </select>
        </label>
        <div>{t('warehouse:panel.position', { x: pickPoint.position.xMm, y: pickPoint.position.yMm })}</div>
      </div>
      <div className="toolbar" style={{ marginTop: 8 }}>
        <button className="danger" onClick={onDelete}>{t('warehouse:panel.deletePickPoint')}</button>
      </div>
    </div>
  )
}

export function WallPanel({ wall, onClose, onDelete }: {
  wall: WallDto
  onClose: () => void
  onDelete: () => void
}) {
  const { t } = useTranslation()
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h3 style={{ margin: 0 }}>{t('warehouse:panel.wall')}</h3>
        <button onClick={onClose}>{t('warehouse:panel.clearSelection')}</button>
      </div>
      <div className="grid-3" style={{ marginTop: 8 }}>
        <div>{t('warehouse:panel.points', { count: wall.points.length })}</div>
        <div>{t('warehouse:panel.segments', { count: wall.points.length - 1 })}</div>
        <div>{t('warehouse:panel.length', { length: totalLength(wall.points).toFixed(0) })}</div>
      </div>
      <div className="toolbar" style={{ marginTop: 8 }}>
        <button className="danger" onClick={onDelete}>{t('warehouse:panel.deleteWall')}</button>
      </div>
    </div>
  )
}

export function ShelfPanel({ shelf, bins, addPending, deletePending, onClose, onAddBin, onDeleteBin }: {
  shelf: ShelfDto
  bins: StorageLocationDto[]
  addPending: boolean
  deletePending: boolean
  onClose: () => void
  onAddBin: () => void
  onDeleteBin: (bin: StorageLocationDto) => void
}) {
  const { t } = useTranslation()
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h3 style={{ margin: 0 }}>{t('warehouse:panel.shelfTitle', { code: shelf.code })}</h3>
        <button onClick={onClose}>{t('warehouse:panel.clearSelection')}</button>
      </div>
      <div className="grid-3" style={{ marginTop: 8 }}>
        <div>{t('warehouse:panel.position', { x: shelf.position.xMm, y: shelf.position.yMm })}</div>
        <div>{t('warehouse:panel.dimensions', { w: shelf.widthMm, d: shelf.depthMm, h: shelf.heightMm })}</div>
        <div>{t('warehouse:panel.bins', { count: bins.length })}</div>
      </div>

      <h4>{t('warehouse:panel.binsHeading')}</h4>
      <table>
        <caption className="visually-hidden">{t('warehouse:panel.binsCaption', { code: shelf.code })}</caption>
        <thead><tr><th scope="col">{t('warehouse:dialog.code')}</th><th scope="col">{t('warehouse:panel.colDimensions')}</th><th scope="col">{t('warehouse:panel.colMaxLoad')}</th><th scope="col"><span className="visually-hidden">{t('warehouse:panel.colActions')}</span></th></tr></thead>
        <tbody>
          {bins.map((b) => (
            <tr key={b.id}>
              <td><code>{b.code}</code></td>
              <td>{b.widthMm} × {b.depthMm} × {b.heightMm}</td>
              <td>{(b.maxWeightGrams / 1000).toFixed(1)} kg</td>
              <td>
                <button className="danger" onClick={() => onDeleteBin(b)} disabled={deletePending} aria-label={t('warehouse:panel.deleteBinAria', { code: b.code })}>
                  {t('common:delete')}
                </button>
              </td>
            </tr>
          ))}
          {bins.length === 0 && (
            <tr><td colSpan={4} className="muted">{t('warehouse:panel.noBins')}</td></tr>
          )}
        </tbody>
      </table>
      <div className="toolbar" style={{ marginTop: 8 }}>
        <button className="primary" onClick={onAddBin} disabled={addPending}>
          {t('warehouse:panel.addBin')}
        </button>
      </div>
    </div>
  )
}

/** Legende: die Swatches nehmen dieselben Zeichenflächen-Farben wie das Canvas (folgen dem Theme). */
export function LayoutLegend({ colors, heatOn, heatRange, heatMax }: {
  colors: CanvasColors
  heatOn: boolean
  heatRange: number
  heatMax: number
}) {
  const { t } = useTranslation()
  return (
    <div className="card">
      <h3>{t('warehouse:legend.title')}</h3>
      <p>
        <span style={{ display: 'inline-block', width: 16, height: 16, background: colors.shelfFill, border: `1px solid ${colors.shelfStroke}`, verticalAlign: 'middle', marginRight: 6 }}></span>{t('warehouse:legend.shelf')}
        &nbsp;&nbsp;
        <span style={{ display: 'inline-block', width: 16, height: 4, background: colors.wall, verticalAlign: 'middle', marginRight: 6 }}></span>{t('warehouse:legend.wall')}
        &nbsp;&nbsp;
        <span style={{ display: 'inline-block', width: 0, height: 0, borderLeft: '7px solid transparent', borderRight: '7px solid transparent', borderBottom: `12px solid ${colors.pickStart}`, verticalAlign: 'middle', marginRight: 6 }}></span>{t('warehouse:legend.start')}
        &nbsp;&nbsp;
        <span style={{ display: 'inline-block', width: 11, height: 11, background: colors.pickEnd, transform: 'rotate(45deg)', verticalAlign: 'middle', marginRight: 6 }}></span>{t('warehouse:legend.end')}
        &nbsp;&nbsp;
        <span style={{ display: 'inline-block', width: 14, height: 14, background: colors.pickBoth, verticalAlign: 'middle', marginRight: 6, borderRadius: 2 }}></span>{t('warehouse:legend.both')}
      </p>
      {heatOn && (
        <div style={{ marginTop: 8 }}>
          <strong style={{ fontSize: 12 }}>{t('warehouse:legend.heat', { range: heatRange })}</strong>
          <span className="muted" style={{ fontSize: 12, marginLeft: 8 }}>
            {heatMax > 0
              ? <Trans i18nKey="warehouse:legend.cold" components={{ code: <code /> }} />
              : t('warehouse:legend.noPicks')}
          </span>
          {heatMax > 0 && (
            <>
              <span style={{
                display: 'inline-block', width: 200, maxWidth: '40%', height: 12, marginLeft: 6, verticalAlign: 'middle',
                background: 'linear-gradient(to right, hsl(120,70%,55%), hsl(60,70%,55%), hsl(0,70%,55%))',
                borderRadius: 3,
              }} />
              <span className="muted" style={{ fontSize: 12, marginLeft: 6 }}>
                <Trans i18nKey="warehouse:legend.hot" values={{ max: heatMax }} components={{ code: <code /> }} />
              </span>
            </>
          )}
        </div>
      )}
    </div>
  )
}
