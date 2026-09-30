import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  useAssignTracking, useCarriers, useCreateShipment, useMarkDelivered, useMarkShipped, useOrders, usePackingPlan, useShipments,
} from '../api/hooks'
import type { CarrierDto, CreateShipmentRequest, OrderDto, ShipmentDto } from '../api/types'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { StatusPill } from '../components/StatusPill'

/// <summary>
/// Versand-Übersicht. Sendungen gibt es nur für gepackte Bestellungen; nur konfigurierte Carrier sind wählbar (heute der
/// manuelle: die Tracking-Nr wird von Hand eingegeben, nachdem das Label im Carrier-Portal erzeugt wurde). Nicht
/// angebundene Carrier erscheinen als "nicht verfügbar". Maße und Gewicht sind Pflichtfelder, ohne Vorbelegung - sie lassen
/// sich aus dem Packvorschlag der Bestellung übernehmen. Der letzte Versand einer Bestellung setzt sie auf "Versendet".
/// </summary>

/** Nur absolute http(s)-Adressen dürfen als Link erscheinen; javascript:, data: und andere Schemata bleiben Text. */
function safeHref(url: string | null | undefined): string | null {
  if (!url) return null
  try {
    const parsed = new URL(url.trim())
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.href : null
  } catch {
    return null
  }
}

export function ShipmentsPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useShipments()
  const carriers = useCarriers()
  const orders = useOrders()
  const create = useCreateShipment()
  const ship = useMarkShipped()
  const delivered = useMarkDelivered()
  const [showForm, setShowForm] = useState(false)

  return (
    <>
      <h2>{t('shipping:title')}</h2>
      <div className="toolbar">
        <button className="primary" onClick={() => setShowForm(!showForm)}>
          {showForm ? t('common:cancel') : t('shipping:new')}
        </button>
      </div>

      {showForm && (
        <CreateForm
          carriers={carriers.data ?? []}
          orders={orders.data ?? []}
          onCreate={async (req) => {
            try {
              await create.mutateAsync(req)
              setShowForm(false)
            } catch {
              /* Fehler steht im Formular, die Eingaben bleiben erhalten */
            }
          }}
          pending={create.isPending}
          error={create.error}
        />
      )}

      <ErrorBanner error={ship.error} title={t('shipping:shipFailed')} fallback={t('shipping:shipFailedFallback')} onDismiss={ship.reset} />
      <ErrorBanner error={delivered.error} title={t('shipping:deliverFailed')} fallback={t('shipping:deliverFailedFallback')} onDismiss={delivered.reset} />

      <LoadState isLoading={isLoading} error={error} hasData={data !== undefined} what={t('shipping:what')} onRetry={() => void refetch()}>
        <div className="table-wrap">
          <table>
            <caption className="visually-hidden">{t('shipping:caption')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('shipping:col.number')}</th><th scope="col">{t('shipping:col.order')}</th><th scope="col">{t('shipping:col.recipient')}</th><th scope="col">{t('shipping:col.carrier')}</th><th scope="col">{t('shipping:col.tracking')}</th>
                <th scope="col">{t('shipping:col.weight')}</th><th scope="col">{t('shipping:col.status')}</th><th scope="col"><span className="visually-hidden">{t('shipping:col.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {data?.map((s) => (
                <ShipmentRow
                  key={s.id} shipment={s}
                  carrier={carriers.data?.find((c) => c.code === s.carrierCode)}
                  onShip={() => ship.mutate(s.id)}
                  onDeliver={() => delivered.mutate(s.id)}
                  busy={ship.isPending || delivered.isPending}
                />
              ))}
              {data?.length === 0 && <tr><td colSpan={8} className="muted">{t('shipping:empty')}</td></tr>}
            </tbody>
          </table>
        </div>
      </LoadState>
    </>
  )
}

function ShipmentRow({ shipment, carrier, onShip, onDeliver, busy }: {
  shipment: ShipmentDto
  carrier: CarrierDto | undefined
  onShip: () => void
  onDeliver: () => void
  busy: boolean
}) {
  const { t } = useTranslation()
  const assign = useAssignTracking()
  const [trackOpen, setTrackOpen] = useState(false)
  const [trackNr, setTrackNr] = useState('')
  const [trackUrl, setTrackUrl] = useState('')
  const [cost, setCost] = useState(0)

  const href = safeHref(shipment.trackingUrl)
  // Ein konfigurierter Carrier mit API erzeugt Nummer und Label selbst; der manuelle braucht die Eingabe.
  const apiCarrier = carrier?.isConfigured === true && carrier.code !== 'MANUAL'
  const urlInvalid = trackUrl.trim() !== '' && safeHref(trackUrl) === null
  const recipientLine = [shipment.recipientZip, shipment.recipientCity].filter(Boolean).join(' ')

  return (
    <>
      <tr>
        <td><code>{shipment.shipmentNumber}</code></td>
        <td>{shipment.orderNumber ?? '?'}</td>
        <td>
          {shipment.recipientName
            ? <>{shipment.recipientName}{recipientLine && <div className="muted" style={{ fontSize: 12 }}>{recipientLine}</div>}</>
            : <span className="muted">—</span>}
        </td>
        <td>{shipment.carrierCode}</td>
        <td>
          {shipment.trackingNumber ? (
            href
              ? <a href={href} target="_blank" rel="noreferrer noopener">{shipment.trackingNumber}</a>
              : (
                <>
                  <code>{shipment.trackingNumber}</code>
                  {shipment.trackingUrl && <div className="muted" style={{ fontSize: 12 }}>{shipment.trackingUrl}</div>}
                </>
              )
          ) : <span className="muted">—</span>}
        </td>
        <td>{(shipment.weightGrams / 1000).toFixed(2)} kg</td>
        <td><StatusPill domain="shipment" status={shipment.status} /></td>
        <td style={{ whiteSpace: 'nowrap' }}>
          {shipment.status === 'Ready' && (
            <button onClick={() => setTrackOpen(!trackOpen)} aria-expanded={trackOpen}>{trackOpen ? t('shipping:row.close') : t('shipping:row.trackingNr')}</button>
          )}
          {shipment.status === 'Labeled' && (
            <button className="primary" onClick={onShip} disabled={busy}>{t('shipping:row.shipped')}</button>
          )}
          {shipment.status === 'Shipped' && (
            <button onClick={onDeliver} disabled={busy}>{t('shipping:row.delivered')}</button>
          )}
        </td>
      </tr>
      {trackOpen && (
        <tr><td colSpan={8} style={{ background: 'var(--c-surface-alt)' }}>
          <div className="grid-3">
            <label>{t('shipping:row.trackingNr')} {apiCarrier && <span className="muted">{t('shipping:row.trackingOptional')}</span>}
              <input value={trackNr} onChange={(e) => setTrackNr(e.target.value)} />
            </label>
            <label>{t('shipping:row.trackingUrl')} <input value={trackUrl} onChange={(e) => setTrackUrl(e.target.value)} placeholder={t('shipping:row.urlPlaceholder')} /></label>
            <label>{t('shipping:row.cost')} <input type="number" min={0} value={cost} onChange={(e) => setCost(+e.target.value)} /></label>
          </div>
          {urlInvalid && <div className="error" role="alert" style={{ marginTop: 8 }}>{t('shipping:row.urlInvalid')}</div>}
          <button className="primary" style={{ marginTop: 8 }} disabled={(!trackNr.trim() && !apiCarrier) || urlInvalid || assign.isPending}
            onClick={() => assign.mutate({
              id: shipment.id,
              req: { trackingNumber: trackNr.trim() || null, trackingUrl: trackUrl.trim() || null, costCents: cost },
            })}>
            {assign.isPending ? t('common:saving') : t('shipping:row.assign')}
          </button>
          <ErrorBanner error={assign.error} title={t('shipping:row.assignFailed')} fallback={t('shipping:row.assignFailedFallback')} onDismiss={assign.reset} style={{ marginTop: 8 }} />
        </td></tr>
      )}
    </>
  )
}

/** Positive ganze Zahl aus einem Eingabefeld, sonst null (leer, 0, negativ, keine Zahl). */
function positiveInt(text: string): number | null {
  const n = Number(text)
  return text.trim() !== '' && Number.isInteger(n) && n > 0 ? n : null
}

function CreateForm({ carriers, orders, onCreate, pending, error }: {
  carriers: CarrierDto[]
  orders: OrderDto[]
  onCreate: (req: CreateShipmentRequest) => void
  pending: boolean
  error: unknown
}) {
  const { t } = useTranslation()
  const [orderId, setOrderId] = useState('')
  const [carrierChoice, setCarrierChoice] = useState('')
  // Keine Pseudo-Vorgaben: Maße und Gewicht müssen eingegeben oder aus dem Packvorschlag übernommen werden.
  const [l, setL] = useState(''); const [w, setW] = useState(''); const [h, setH] = useState('')
  const [g, setG] = useState('')
  const [notes, setNotes] = useState('')

  const packing = usePackingPlan(orderId || undefined)
  const packedOrders = orders.filter((o) => o.status === 'Packed')
  const chosenOrder = packedOrders.find((o) => o.id === orderId)
  const selectable = carriers.filter((c) => c.isConfigured)
  const unavailable = carriers.filter((c) => !c.isConfigured)
  const carrierCode = carrierChoice || selectable[0]?.code || ''

  const length = positiveInt(l); const width = positiveInt(w); const height = positiveInt(h); const weight = positiveInt(g)
  const complete = length !== null && width !== null && height !== null && weight !== null
  const address = chosenOrder?.shippingAddress

  return (
    <div className="card">
      <h3>{t('shipping:form.title')}</h3>
      <div className="grid-3">
        <label>{t('shipping:form.order')}
          <select value={orderId} onChange={(e) => setOrderId(e.target.value)}>
            <option value="">{t('shipping:form.choose')}</option>
            {packedOrders.map((o) => <option key={o.id} value={o.id}>{o.orderNumber}{o.customerName ? ` – ${o.customerName}` : ''}</option>)}
          </select>
        </label>
        <label>{t('shipping:col.carrier')}
          <select value={carrierCode} onChange={(e) => setCarrierChoice(e.target.value)}>
            {carriers.map((c) => (
              <option key={c.code} value={c.code} disabled={!c.isConfigured}>
                {c.displayName}{c.isConfigured ? '' : t('shipping:form.notAvailableSuffix')}
              </option>
            ))}
          </select>
        </label>
        <label>{t('shipping:form.weight')} <input type="number" min={1} value={g} onChange={(e) => setG(e.target.value)} required /></label>
        <label>{t('shipping:form.length')} <input type="number" min={1} value={l} onChange={(e) => setL(e.target.value)} required /></label>
        <label>{t('shipping:form.width')} <input type="number" min={1} value={w} onChange={(e) => setW(e.target.value)} required /></label>
        <label>{t('shipping:form.height')} <input type="number" min={1} value={h} onChange={(e) => setH(e.target.value)} required /></label>
      </div>
      {unavailable.length > 0 && (
        <p className="muted" style={{ marginBottom: 0 }}>
          {t('shipping:form.unavailable', { list: unavailable.map((c) => `${c.displayName}${c.note ? ` (${c.note})` : ''}`).join('; ') })}
        </p>
      )}
      {chosenOrder && (
        <p className="muted" style={{ marginBottom: 0 }}>
          {t('shipping:form.recipient')} {chosenOrder.customerName
            ? `${chosenOrder.customerName}${address ? `, ${address.street}, ${address.zip} ${address.city}` : t('shipping:form.noAddress')}`
            : t('shipping:form.noCustomer')}
        </p>
      )}
      {packing.data && packing.data.cartons.length > 0 && (
        <div className="toolbar" style={{ marginTop: 8, marginBottom: 0 }}>
          <span className="muted">{t('shipping:form.adopt')}</span>
          {packing.data.cartons.map((c) => (
            <button
              key={c.index}
              type="button"
              onClick={() => {
                setL(String(c.innerLengthMm)); setW(String(c.innerWidthMm)); setH(String(c.innerHeightMm)); setG(String(c.totalWeightGrams))
              }}
            >
              {t('shipping:form.carton', { n: c.index + 1, l: c.innerLengthMm, w: c.innerWidthMm, h: c.innerHeightMm, weight: (c.totalWeightGrams / 1000).toFixed(2) })}
            </button>
          ))}
        </div>
      )}
      <label style={{ marginTop: 8 }}>{t('shipping:form.notes')} <input value={notes} onChange={(e) => setNotes(e.target.value)} /></label>
      <button className="primary" style={{ marginTop: 8 }} disabled={!orderId || !carrierCode || !complete || pending}
        onClick={() => {
          if (!complete) return
          onCreate({ orderId, carrierCode, lengthMm: length, widthMm: width, heightMm: height, weightGrams: weight, notes: notes || null })
        }}>
        {pending ? t('common:saving') : t('shipping:form.create')}
      </button>
      <ErrorBanner error={error} title={t('shipping:form.createFailed')} fallback={t('shipping:form.createFailedFallback')} style={{ marginTop: 8 }} />
    </div>
  )
}
