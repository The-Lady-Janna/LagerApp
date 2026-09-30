import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router-dom'
import { useCancelOrder, useGeneratePickList, useOrders, usePackingPlan, usePickPoints } from '../api/hooks'
import { CANCELLABLE_ORDER_STATUSES } from '../api/types'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { formatDateOnly, formatDateTime } from '../lib/format'
import { useAuth } from '../state/auth'
import { OrderStatusPill } from './orders/OrderStatusPill'

export function OrderDetailPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const navigate = useNavigate()
  const { data: orders, isLoading, error: ordersError, refetch } = useOrders()
  const order = orders?.find((o) => o.id === id)
  const generate = useGeneratePickList()
  const cancel = useCancelOrder()
  const canCancel = useAuth((s) => s.hasRole('Manager'))
  // Für eine stornierte Bestellung gibt es nichts mehr zu verpacken.
  const packing = usePackingPlan(order?.status === 'Cancelled' ? undefined : id)
  const pickPoints = usePickPoints()

  const [startId, setStartId] = useState<string>('')
  const [endId, setEndId] = useState<string>('')

  if (!order) {
    return (
      <LoadState isLoading={isLoading} error={ordersError} what={t('orders:detail.what')} onRetry={() => void refetch()}>
        <p className="muted">{t('orders:detail.notFound')}</p>
      </LoadState>
    )
  }

  const allPoints = pickPoints.data ?? []
  const startCandidates = allPoints.filter((p) => p.type === 'Start' || p.type === 'Both')
  const endCandidates = allPoints.filter((p) => p.type === 'End' || p.type === 'Both')

  const generatePickList = async () => {
    try {
      const pl = await generate.mutateAsync({
        orderIds: [order.id],
        startPickPointId: startId || null,
        endPickPointId: endId || null,
      })
      navigate(`/picklists/${pl.id}`)
    } catch {
      /* Fehler steht in der Karte */
    }
  }

  const askCancel = () => {
    const consequence = order.status === 'New' ? t('orders:cancel.consequenceNew') : t('orders:cancel.consequenceOther')
    if (!window.confirm(t('orders:cancel.confirm', { number: order.orderNumber, consequence }))) return
    cancel.mutate(order.id)
  }

  const address = order.shippingAddress
  const warnings = packing.data?.warnings ?? []

  return (
    <>
      <h2>{t('orders:detail.title', { number: order.orderNumber })}</h2>
      <div className="card">
        <div className="grid-3">
          <div><strong>{t('orders:detail.status')}</strong> <OrderStatusPill status={order.status} /></div>
          <div><strong>{t('orders:detail.source')}</strong> {order.source}</div>
          <div><strong>{t('orders:detail.customerRef')}</strong> {order.customerReference ?? '—'}</div>
          <div><strong>{t('orders:detail.customer')}</strong> {order.customerName ?? '—'}</div>
          <div><strong>{t('orders:detail.priority')}</strong> {t(`orders:priority.${order.priority ?? 0}`, { defaultValue: String(order.priority) })}</div>
          <div><strong>{t('orders:detail.due')}</strong> {formatDateOnly(order.dueDate)}</div>
          <div><strong>{t('orders:detail.created')}</strong> {formatDateTime(order.createdAt)}</div>
          {order.externalReference && <div><strong>{t('orders:detail.externalRef')}</strong> <code>{order.externalReference}</code></div>}
        </div>
        <div style={{ marginTop: 12 }}>
          <strong>{t('orders:detail.address')}</strong>{' '}
          {address ? (
            <span>
              {address.label}: {address.street}{address.street2 ? `, ${address.street2}` : ''}, {address.zip} {address.city}, {address.country}
            </span>
          ) : <span className="muted">{t('orders:detail.noAddress')}</span>}
        </div>
        {canCancel && CANCELLABLE_ORDER_STATUSES.includes(order.status) && (
          <div className="toolbar" style={{ marginTop: 12, marginBottom: 0 }}>
            <button className="danger" onClick={askCancel} disabled={cancel.isPending}>
              {cancel.isPending ? t('orders:detail.cancelling') : t('orders:detail.cancel')}
            </button>
          </div>
        )}
        <ErrorBanner error={cancel.error} title={t('orders:cancel.failed')} fallback={t('orders:cancel.failedFallback')} onDismiss={cancel.reset} style={{ marginTop: 8 }} />
      </div>

      <div className="card">
        <h3>{t('orders:detail.lines')}</h3>
        <table>
          <thead><tr><th scope="col">{t('orders:detail.colSku')}</th><th scope="col">{t('orders:detail.colQuantity')}</th></tr></thead>
          <tbody>
            {order.lines.map((l) => (
              <tr key={l.id}><td><code>{l.articleSku}</code></td><td>{l.quantity}</td></tr>
            ))}
          </tbody>
        </table>
      </div>

      {order.status === 'New' && (
        <div className="card">
          <h3>{t('orders:detail.pick.title')}</h3>
          <div className="grid-2">
            <label>
              {t('orders:detail.pick.start')}
              <select value={startId} onChange={(e) => setStartId(e.target.value)}>
                <option value="">{t('orders:detail.pick.startAuto')}</option>
                {startCandidates.map((p) => (
                  <option key={p.id} value={p.id}>{p.label} ({p.position.xMm} × {p.position.yMm} mm)</option>
                ))}
              </select>
            </label>
            <label>
              {t('orders:detail.pick.end')}
              <select value={endId} onChange={(e) => setEndId(e.target.value)}>
                <option value="">{t('orders:detail.pick.noEnd')}</option>
                {endCandidates.map((p) => (
                  <option key={p.id} value={p.id}>{p.label} ({p.position.xMm} × {p.position.yMm} mm)</option>
                ))}
              </select>
            </label>
          </div>
          <div className="toolbar" style={{ marginTop: 12 }}>
            <button className="primary" onClick={generatePickList} disabled={generate.isPending}>
              {generate.isPending ? t('orders:cart.generating') : t('orders:detail.pick.title')}
            </button>
          </div>
          <ErrorBanner error={generate.error} title={t('orders:cart.failed')} fallback={t('orders:detail.pick.failedFallback')} onDismiss={generate.reset} />
        </div>
      )}

      <div className="card">
        <h3>{t('orders:detail.pack.title')}</h3>
        <p className="muted" style={{ marginTop: 0 }}>
          {t('orders:detail.pack.hint')}
        </p>
        {packing.isLoading && <p className="muted" role="status">{t('orders:detail.pack.computing')}</p>}
        <ErrorBanner error={packing.error} title={t('orders:detail.pack.failed')} fallback={t('orders:detail.pack.failedFallback')} onRetry={() => void packing.refetch()} />
        {packing.data && (
          <>
            {warnings.length > 0 && (
              <div className="warning" role="status" style={{ marginBottom: 8 }}>
                <strong>{t('orders:detail.pack.warnings')}</strong>
                <ul style={{ margin: '4px 0 0' }}>
                  {warnings.map((w, i) => <li key={i}>{w}</li>)}
                </ul>
              </div>
            )}
            <p>{packing.data.unpacked.length > 0 ? t('orders:detail.pack.cartonsUnpacked', { count: packing.data.cartons.length, unpacked: packing.data.unpacked.length }) : t('orders:detail.pack.cartons', { count: packing.data.cartons.length })}</p>
            {packing.data.cartons.map((c) => (
              <div key={c.index} style={{ marginBottom: 8, paddingLeft: 12, borderLeft: '3px solid var(--c-primary)' }}>
                <Trans
                  i18nKey="orders:detail.pack.carton"
                  values={{ n: c.index + 1, type: c.cartonType, l: c.innerLengthMm, w: c.innerWidthMm, h: c.innerHeightMm, weight: (c.totalWeightGrams / 1000).toFixed(2), fill: (c.fillRatio * 100).toFixed(0) }}
                  components={{ strong: <strong /> }}
                />
                <ul>
                  {c.allocations.map((a) => (
                    <li key={a.articleId}><code>{a.articleSku}</code>: {a.quantity}×</li>
                  ))}
                </ul>
              </div>
            ))}
          </>
        )}
      </div>
    </>
  )
}
