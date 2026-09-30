import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router-dom'
import { isTraceableLot, useChargeTrace } from '../../api/lotHooks'
import { LoadState } from '../../components/LoadState'
import { StatTile } from '../../components/StatTile'
import { StatusPill } from '../../components/StatusPill'
import { formatDateOnly, formatDateTime } from '../../lib/format'
import { ExpiryPill } from './ExpiryPill'

// Anzeigenamen der Ledger-Gründe (StockMovementReason des Servers) und der Belegarten: traceability:reason.<Grund> bzw.
// traceability:reference.<Art>; ein unbekannter Wert erscheint unverändert.

const signed = (n: number) => (n > 0 ? `+${n}` : String(n))

/// <summary>
/// Chargen-Rückverfolgung: Die Suche nach einer Chargennummer zeigt die Wareneingänge, den aktuellen Bestand je Lagerplatz,
/// alle Bewegungen aus dem Ledger und die daraus ableitbaren, möglicherweise betroffenen Bestellungen. Die Charge steht in
/// der Adresse (?lot=…): Links aus der Bestandsansicht und der MHD-Karte führen direkt hierher.
/// </summary>
export function TraceabilityPage() {
  const { t } = useTranslation()
  const [params, setParams] = useSearchParams()
  const lot = (params.get('lot') ?? '').trim()

  // Das Eingabefeld folgt der Adresse (Link, Zurück-Navigation): State beim Wechsel im selben Render anpassen.
  const [input, setInput] = useState(lot)
  const [shownLot, setShownLot] = useState(lot)
  if (shownLot !== lot) {
    setShownLot(lot)
    setInput(lot)
  }

  const trace = useChargeTrace(lot)

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const value = input.trim()
    setParams(value ? { lot: value } : {})
  }

  const data = trace.data
  const orders = data?.orders ?? []

  return (
    <>
      <h2>{t('traceability:title')}</h2>
      <p className="muted">
        {t('traceability:intro')}
      </p>

      <form className="toolbar" onSubmit={submit} role="search" aria-label={t('traceability:searchAria')}>
        <label>
          {t('traceability:lotNumber')}
          <input value={input} onChange={(e) => setInput(e.target.value)} placeholder={t('traceability:lotPlaceholder')} style={{ marginLeft: 8 }} />
        </label>
        <button type="submit" className="primary" disabled={input.trim() === ''}>{t('traceability:search')}</button>
      </form>

      {lot !== '' && !isTraceableLot(lot) && (
        <div className="error" role="alert">
          {t('traceability:slashNotAllowed')}
        </div>
      )}

      {lot !== '' && isTraceableLot(lot) && data === undefined && (
        <LoadState isLoading={trace.isLoading} error={trace.error} what={t('traceability:what')} onRetry={() => void trace.refetch()} />
      )}

      {data === null && (
        <p className="muted" role="status">
          {t('traceability:notFound', { lot })}
        </p>
      )}

      {data && (
        <>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 12, marginBottom: 16 }}>
            <StatTile label={t('traceability:tile.lot')} value={<code>{data.lotNumber}</code>} />
            <StatTile label={t('traceability:tile.inbound')} value={data.inboundTotal} tooltip={t('traceability:tile.inboundTip')} />
            <StatTile label={t('traceability:tile.stock')} value={data.currentStockTotal} tone={data.currentStockTotal === 0 ? 'muted' : undefined} />
            <StatTile label={t('traceability:tile.movements')} value={data.movements.length} />
            <StatTile label={t('traceability:tile.orders')} value={orders.length} tooltip={t('traceability:tile.ordersTip')} />
          </div>

          <div className="card" style={{ marginBottom: 12 }}>
            <h3>{t('traceability:inbounds.title')}</h3>
            {data.inbounds.length === 0 ? (
              <p className="muted">{t('traceability:inbounds.none')}</p>
            ) : (
              <div className="table-wrap">
                <table style={{ width: '100%' }}>
                  <caption className="visually-hidden">{t('traceability:inbounds.caption', { lot: data.lotNumber })}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t('traceability:col.deliveryNote')}</th><th scope="col">{t('traceability:col.article')}</th><th scope="col">{t('traceability:col.targetBin')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('traceability:col.quantity')}</th><th scope="col">{t('traceability:col.expiry')}</th><th scope="col">{t('traceability:col.status')}</th><th scope="col">{t('traceability:col.date')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.inbounds.map((i, index) => (
                      <tr key={`${i.shipmentId}-${index}`}>
                        <td><code>{i.shipmentNumber}</code></td>
                        <td><code>{i.articleSku}</code></td>
                        <td><code>{i.targetBinCode ?? i.targetBinId}</code></td>
                        <td style={{ textAlign: 'right' }}>{i.quantity}</td>
                        <td>{formatDateOnly(i.expiryDate)}</td>
                        <td><StatusPill domain="inbound" status={i.status ?? 'Received'} small /></td>
                        <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(i.receivedAt)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          <div className="card" style={{ marginBottom: 12 }}>
            <h3>{t('traceability:stock.title')}</h3>
            {data.currentStock.length === 0 ? (
              <p className="muted">{t('traceability:stock.none')}</p>
            ) : (
              <div className="table-wrap">
                <table style={{ width: '100%' }}>
                  <caption className="visually-hidden">{t('traceability:stock.caption', { lot: data.lotNumber })}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t('traceability:col.location')}</th><th scope="col">{t('traceability:col.article')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('traceability:col.quantity')}</th><th scope="col">{t('traceability:col.expiry')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.currentStock.map((s) => (
                      <tr key={s.stockItemId}>
                        <td><code>{s.binCode}</code></td>
                        <td><code>{s.articleSku}</code></td>
                        <td style={{ textAlign: 'right' }}>{s.quantity}</td>
                        <td>{formatDateOnly(s.expiryDate)} <ExpiryPill expiryDate={s.expiryDate} /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          <div className="card" style={{ marginBottom: 12 }}>
            <h3>{t('traceability:movements.title')}</h3>
            {data.movements.length === 0 ? (
              <p className="muted">{t('traceability:movements.none')}</p>
            ) : (
              <div className="table-wrap">
                <table style={{ width: '100%' }}>
                  <caption className="visually-hidden">{t('traceability:movements.caption', { lot: data.lotNumber })}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t('traceability:col.time')}</th><th scope="col">{t('traceability:col.operation')}</th><th scope="col">{t('traceability:col.article')}</th><th scope="col">{t('traceability:col.location')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('traceability:col.quantity')}</th><th scope="col">{t('traceability:col.document')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.movements.map((m, index) => (
                      <tr key={index}>
                        <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(m.at)}</td>
                        <td>{t(`traceability:reason.${m.reason}`, { defaultValue: m.reason })}</td>
                        <td>{m.articleSku ? <code>{m.articleSku}</code> : <span className="muted">—</span>}</td>
                        <td><code>{m.binCode ?? m.binId}</code></td>
                        <td
                          className={m.quantityDelta < 0 ? 'text-danger' : 'text-success'}
                          style={{ textAlign: 'right', fontWeight: 600 }}
                        >
                          {signed(m.quantityDelta)}
                        </td>
                        <td>
                          {m.referenceType === 'PickList' && m.referenceId ? (
                            <Link to={`/picklists/${m.referenceId}`}>{t('traceability:reference.PickList')}</Link>
                          ) : m.referenceType ? (
                            t(`traceability:reference.${m.referenceType}`, { defaultValue: m.referenceType })
                          ) : (
                            <span className="muted">—</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          <div className="card">
            <h3>{t('traceability:orders.title')}</h3>
            <p className="muted" style={{ fontSize: 12 }}>
              {t('traceability:orders.hint')}
            </p>
            {orders.length === 0 ? (
              <p className="muted">{t('traceability:orders.none')}</p>
            ) : (
              <div className="table-wrap">
                <table style={{ width: '100%' }}>
                  <caption className="visually-hidden">{t('traceability:orders.caption', { lot: data.lotNumber })}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t('traceability:col.order')}</th><th scope="col">{t('traceability:col.customer')}</th><th scope="col">{t('traceability:col.status')}</th>
                      <th scope="col">{t('traceability:col.pickList')}</th><th scope="col">{t('traceability:col.picked')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {orders.map((o) => (
                      <tr key={`${o.orderId}-${o.pickListId}`}>
                        <td><Link to={`/orders/${o.orderId}`}><code>{o.orderNumber}</code></Link></td>
                        <td>{o.customerReference ?? <span className="muted">—</span>}</td>
                        <td><StatusPill domain="order" status={o.status} small /></td>
                        <td><Link to={`/picklists/${o.pickListId}`}>{o.pickListNumber ?? t('traceability:reference.PickList')}</Link></td>
                        <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(o.pickedAt)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
    </>
  )
}
