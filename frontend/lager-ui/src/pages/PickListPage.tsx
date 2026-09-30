import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { useMarkPicked, usePickList, usePickPoints, useRecalculatePickList, useStorageLocations, useWalls, useWarehouseLayout } from '../api/hooks'
import { PickRouteCanvas } from '../components/PickRouteCanvas'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import type { ShelfDto } from '../api/types'

export function PickListPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const { data, isLoading, error, refetch } = usePickList(id)
  const locations = useStorageLocations()
  const walls = useWalls()
  const pickPoints = usePickPoints()
  const layout = useWarehouseLayout()
  const recalc = useRecalculatePickList()
  const markPickedMut = useMarkPicked()

  const [startId, setStartId] = useState<string>('')
  const [endId, setEndId] = useState<string>('')
  const [confirmPicked, setConfirmPicked] = useState(false)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('picking:detail.what')} onRetry={() => void refetch()} />

  const allPoints = pickPoints.data ?? []
  const startCandidates = allPoints.filter((p) => p.type === 'Start' || p.type === 'Both')
  const endCandidates = allPoints.filter((p) => p.type === 'End' || p.type === 'Both')

  const recalculate = () => {
    if (!id) return
    recalc.mutate({
      id,
      req: {
        startPickPointId: startId || null,
        endPickPointId: endId || null,
      },
    })
  }

  return (
    <>
      <h2>{t('picking:detail.title', { number: data.pickListNumber })}</h2>
      <div className="card">
        <div className="grid-3">
          <div><strong>{t('picking:detail.status')}</strong> {t(`status:pickList.${data.status}`, { defaultValue: data.status })}</div>
          <div><strong>{t('picking:detail.distance')}</strong> {(data.totalDistanceMm / 1000).toFixed(2)} m</div>
          <div><strong>{t('picking:detail.lines')}</strong> {data.items.length}</div>
        </div>
        {data.pickCartConfigName && (
          <div style={{ marginTop: 8 }}>
            <strong>{t('picking:detail.cart')}</strong> {data.pickCartConfigName}
          </div>
        )}
        <div className="toolbar" style={{ marginTop: 12 }}>
          {data.status !== 'Completed' && data.status !== 'Picked' && (
            <button
              onClick={() => setConfirmPicked(true)}
              disabled={markPickedMut.isPending}
            >
              {markPickedMut.isPending ? t('common:saving') : t('picking:detail.finish')}
            </button>
          )}
          {(data.status === 'Picked' || data.status === 'InProgress') && (
            <Link className="button primary" to={`/picklists/${data.id}/pack`}>{t('picking:detail.toPack')}</Link>
          )}
          {data.status === 'Completed' && (
            <span className="success" style={{ padding: '6px 12px' }}>{t('picking:detail.packed')}</span>
          )}
          {/* Mobile-Picker-Modus: fullscreen, dunkles Theme, Step-by-Step, optional Barcode-Scan.
              Funktioniert auf jedem Touch-Gerät — am besten auf einem Tablet oder Handy. */}
          {data.status !== 'Completed' && (
            <Link className="button" to={`/picker/${data.id}`}>{t('picking:detail.openMobile')}</Link>
          )}
        </div>
        <ErrorBanner error={markPickedMut.error} title={t('picking:error')} onDismiss={markPickedMut.reset} style={{ marginTop: 8 }} />
      </div>

      <ConfirmDialog
        open={confirmPicked}
        title={t('picking:detail.confirm.title')}
        confirmLabel={t('picking:detail.confirm.label')}
        pending={markPickedMut.isPending}
        onConfirm={() => {
          markPickedMut.mutate(data.id, { onSettled: () => setConfirmPicked(false) })
        }}
        onCancel={() => setConfirmPicked(false)}
      >
        <p>{t('picking:detail.confirm.body')}</p>
      </ConfirmDialog>

      <CollapsibleCard title={t('picking:detail.recalc.title')} storageKey="picklist-recalc" defaultOpen={false}>
        <p className="muted">
          {t('picking:detail.recalc.hint')}
        </p>
        <div className="grid-2">
          <label>
            {t('picking:detail.recalc.start')}
            <select value={startId} onChange={(e) => setStartId(e.target.value)}>
              <option value="">{t('picking:detail.recalc.startAuto')}</option>
              {startCandidates.map((p) => (
                <option key={p.id} value={p.id}>{p.label} ({p.position.xMm} × {p.position.yMm} mm)</option>
              ))}
            </select>
          </label>
          <label>
            {t('picking:detail.recalc.end')}
            <select value={endId} onChange={(e) => setEndId(e.target.value)}>
              <option value="">{t('picking:detail.recalc.noEnd')}</option>
              {endCandidates.map((p) => (
                <option key={p.id} value={p.id}>{p.label} ({p.position.xMm} × {p.position.yMm} mm)</option>
              ))}
            </select>
          </label>
        </div>
        <div className="toolbar" style={{ marginTop: 12 }}>
          <button className="primary" onClick={recalculate} disabled={recalc.isPending}>
            {recalc.isPending ? t('picking:detail.recalc.pending') : t('picking:detail.recalc.title')}
          </button>
        </div>
        <ErrorBanner error={recalc.error} title={t('picking:error')} onDismiss={recalc.reset} />
        {recalc.data && <div className="success" style={{ marginTop: 8 }}>{t('picking:detail.recalc.done')}</div>}
      </CollapsibleCard>

      <CollapsibleCard title={t('picking:detail.routeTitle')} storageKey="picklist-canvas" defaultOpen>
        {locations.data && (
          <PickRouteCanvas
            locations={locations.data}
            items={data.items}
            waypoints={data.waypoints ?? []}
            walls={walls.data ?? []}
            shelves={(layout.data ?? []).flatMap((w): ShelfDto[] =>
              w.zones.flatMap((z) => z.aisles.flatMap((a) => a.shelves))
            )}
          />
        )}
      </CollapsibleCard>

      <CollapsibleCard title={t('picking:detail.order.title')} storageKey="picklist-order" defaultOpen>
        <table>
          <caption className="visually-hidden">{t('picking:detail.order.caption')}</caption>
          <thead><tr><th scope="col">#</th><th scope="col">{t('picking:col.location')}</th><th scope="col">{t('picking:col.sku')}</th><th scope="col">{t('picking:col.quantity')}</th><th scope="col">{t('picking:col.order')}</th></tr></thead>
          <tbody>
            {data.items.map((i) => (
              <tr key={i.id}>
                <td>{i.sequenceNumber}</td>
                <td><code>{i.storageLocationCode}</code></td>
                <td><code>{i.articleSku}</code></td>
                <td>{i.quantity}</td>
                <td>{i.orderNumber}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </CollapsibleCard>
    </>
  )
}
