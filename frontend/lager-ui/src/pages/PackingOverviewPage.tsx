import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { usePickLists } from '../api/hooks'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { LoadState } from '../components/LoadState'
import { StatTile } from '../components/StatTile'
import type { PickListDto } from '../api/types'
import { formatDateTime } from '../lib/format'

export function PackingOverviewPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = usePickLists()

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('picking:lists.what')} onRetry={() => void refetch()} />

  const all: PickListDto[] = data
  const ready = all.filter((p) => p.status === 'Picked').sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime())
  const inProgress = all.filter((p) => p.status === 'Pending' || p.status === 'InProgress').sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime())
  const done = all.filter((p) => p.status === 'Completed').sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())

  return (
    <>
      <h2>{t('picking:packing.title')}</h2>

      <div className="card card--info">
        <h3 style={{ margin: '0 0 8px' }}>{t('picking:packing.overview')}</h3>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(160px, 100%), 1fr))', gap: 12 }}>
          <StatTile label={t('picking:packing.tile.ready')} value={ready.length} tone="success" />
          <StatTile label={t('picking:packing.tile.picking')} value={inProgress.length} tone="warning" />
          <StatTile label={t('picking:packing.tile.done')} value={done.length} tone="muted" />
        </div>
      </div>

      <CollapsibleCard title={t('picking:packing.ready.title', { count: ready.length })} storageKey="pack-ready" defaultOpen>
        <p className="muted">{t('picking:packing.ready.hint')}</p>
        <PickListTable items={ready} packLink />
      </CollapsibleCard>

      <CollapsibleCard title={t('picking:packing.picking.title', { count: inProgress.length })} storageKey="pack-inprogress" defaultOpen={false}>
        <p className="muted">{t('picking:packing.picking.hint')}</p>
        <PickListTable items={inProgress} />
      </CollapsibleCard>

      <CollapsibleCard title={t('picking:packing.done.title', { count: done.length })} storageKey="pack-done" defaultOpen={false}>
        <PickListTable items={done} />
      </CollapsibleCard>
    </>
  )
}

function PickListTable({ items, packLink = false }: { items: PickListDto[]; packLink?: boolean }) {
  const { t } = useTranslation()
  if (items.length === 0) return <p className="muted">{t('picking:packing.empty')}</p>
  return (
    <table>
      <caption className="visually-hidden">{t('picking:lists.caption')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('picking:col.number')}</th>
          <th scope="col">{t('picking:col.status')}</th>
          <th scope="col">{t('picking:packing.cart')}</th>
          <th scope="col">{t('picking:packing.orders')}</th>
          <th scope="col">{t('picking:col.lines')}</th>
          <th scope="col">{t('picking:col.created')}</th>
          <th scope="col"><span className="visually-hidden">{t('picking:col.actions')}</span></th>
        </tr>
      </thead>
      <tbody>
        {items.map((p) => {
          const distinctOrders = new Set(p.items.map((i) => i.orderId)).size
          return (
            <tr key={p.id}>
              <td><code>{p.pickListNumber}</code></td>
              <td>{t(`status:pickList.${p.status}`, { defaultValue: p.status })}</td>
              <td>{p.pickCartConfigName ?? <span className="muted">—</span>}</td>
              <td>{distinctOrders}</td>
              <td>{p.items.length}</td>
              <td>{formatDateTime(p.createdAt)}</td>
              <td>
                {packLink
                  ? <Link className="button primary" to={`/picklists/${p.id}/pack`}>{t('picking:packing.pack')}</Link>
                  : <Link to={`/picklists/${p.id}`}>{t('picking:open')}</Link>}
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}
