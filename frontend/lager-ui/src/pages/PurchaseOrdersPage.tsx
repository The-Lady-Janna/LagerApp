import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import {
  useArticles, useCreateInboundFromPurchaseOrder, useCreatePurchaseOrder, usePurchaseOrders, usePurchaseSuggestions,
  useSendPurchaseOrder, useStorageLocations, useSuppliers,
} from '../api/hooks'
import type { PurchaseOrderDto, PurchaseSuggestionDto } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { StatusPill } from '../components/StatusPill'
import { formatDate, formatDateOnly, formatMoney } from '../lib/format'
import { useAuth } from '../state/auth'

/** Noch nicht empfangene Menge einer Bestellzeile (nie negativ). */
const openQty = (line: PurchaseOrderDto['lines'][number]) => Math.max(0, line.orderedQty - line.receivedQty)

/** Erwartet die Bestellung noch Ware (versendet oder teilweise geliefert) und hat mindestens eine Zeile offene Menge? */
const hasOpenQuantity = (po: PurchaseOrderDto) =>
  (po.status === 'Sent' || po.status === 'PartiallyReceived') && po.lines.some((l) => openQty(l) > 0)

/// <summary>
/// Bestellungen-Page: Suggestions oben, Liste in der Mitte, Inline-Create unten.
/// Detail per Klick aufklappbar. Versenden direkt aus der Liste (nach einer Rückfrage, mit Sperre, solange die Aktion läuft).
/// Die Bestellung bucht selbst keinen Bestand: "Wareneingang anlegen" (Rolle Receiver) legt aus den offenen Mengen einen
/// Wareneingang (Entwurf) an; gebucht wird dort ("Empfangen"), und erst das schreibt den Empfang in die Bestellung fort.
/// </summary>
export function PurchaseOrdersPage() {
  const { t } = useTranslation()
  const { data: pos, isLoading, error, refetch } = usePurchaseOrders()
  const { data: suggestions } = usePurchaseSuggestions()
  const suppliers = useSuppliers()
  const articles = useArticles()
  const create = useCreatePurchaseOrder()
  const send = useSendPurchaseOrder()
  const bins = useStorageLocations()
  const createInbound = useCreateInboundFromPurchaseOrder()
  // Der Server erlaubt "Wareneingang anlegen" der Rolle Receiver (Admin und Manager eingeschlossen).
  const canReceive = useAuth((s) => s.hasRole('Receiver'))
  const [showForm, setShowForm] = useState(false)
  const [seedFrom, setSeedFrom] = useState<PurchaseSuggestionDto | null>(null)
  const [confirmSend, setConfirmSend] = useState<PurchaseOrderDto | null>(null)
  const [inboundFor, setInboundFor] = useState<PurchaseOrderDto | null>(null)
  // Hinweis nach erfolgreichem Anlegen (mit Verweis auf den Wareneingang, wo gebucht wird).
  const [notice, setNotice] = useState<string | null>(null)

  if (!pos) return <LoadState isLoading={isLoading} error={error} what={t('purchasing:po.what')} onRetry={() => void refetch()} />

  const actionPending = send.isPending || createInbound.isPending

  return (
    <>
      <h2>{t('purchasing:po.title')}</h2>

      {suggestions && suggestions.length > 0 && (
        <div className="card" style={{ borderLeft: '4px solid var(--c-primary)', marginBottom: 16 }}>
          <h3 style={{ marginTop: 0 }}>{t('purchasing:po.suggestions.title')}</h3>
          <p className="muted" style={{ fontSize: 12 }}>
            {t('purchasing:po.suggestions.hint')}
          </p>
          {suggestions.map((s, idx) => (
            <div key={idx} style={{ marginBottom: 8, padding: 8, background: 'var(--c-surface-alt)', borderRadius: 4 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                <strong>{s.supplierName ?? t('purchasing:po.suggestions.noSupplier')}</strong>
                {s.supplierId && (
                  <button onClick={() => { setSeedFrom(s); setShowForm(true) }}>
                    {t('purchasing:po.suggestions.createPo', { count: s.lines.length })}
                  </button>
                )}
              </div>
              <div className="muted" style={{ fontSize: 11, marginBottom: 4 }}>{t('purchasing:po.suggestions.leadTime', { days: s.leadTimeDays, count: s.lines.length })}</div>
              <ul style={{ margin: 0, paddingLeft: 16, fontSize: 12 }}>
                {s.lines.map((l) => (
                  <li key={l.articleId}>
                    <code>{l.sku}</code> {t('purchasing:po.suggestions.line', { name: l.name, stock: l.currentStock, reorder: l.reorderPoint, suggested: l.suggestedOrderQty })}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      )}

      <div className="toolbar" style={{ marginBottom: 12 }}>
        <button className="primary" onClick={() => { setSeedFrom(null); setShowForm(true) }}>{t('purchasing:po.new')}</button>
      </div>

      {showForm && (
        <CreatePoForm
          // key: ein anderer Bestellvorschlag (oder "neue PO") startet mit frischem Zustand — sonst blieben
          // Lieferant und Zeilen des vorherigen Vorschlags unter der Überschrift des neuen stehen.
          key={seedFrom?.supplierId ?? 'new'}
          suggestion={seedFrom}
          suppliers={suppliers.data ?? []}
          articles={articles.data ?? []}
          onCancel={() => { setShowForm(false); setSeedFrom(null) }}
          onCreate={(req) => create.mutate(req, { onSuccess: () => { setShowForm(false); setSeedFrom(null) } })}
          pending={create.isPending}
          error={create.error}
          onDismissError={create.reset}
        />
      )}

      <ErrorBanner error={send.error} title={t('purchasing:po.sendFailed')} onDismiss={send.reset} />
      {notice && (
        <div className="success" role="status" style={{ marginBottom: 12 }}>
          {notice} <Link to="/inbound">{t('purchasing:po.toInbound')}</Link>
        </div>
      )}

      <div className="table-wrap">
      <table style={{ width: '100%' }}>
        <caption className="visually-hidden">{t('purchasing:po.caption')}</caption>
        <thead>
          <tr><th scope="col">{t('purchasing:po.col.number')}</th><th scope="col">{t('purchasing:po.col.supplier')}</th><th scope="col">{t('purchasing:po.col.status')}</th><th scope="col">{t('purchasing:po.col.value')}</th><th scope="col">{t('purchasing:po.col.created')}</th><th scope="col">{t('purchasing:po.col.expected')}</th><th scope="col"><span className="visually-hidden">{t('purchasing:po.col.actions')}</span></th></tr>
        </thead>
        <tbody>
          {pos.map((po) => (
            <PoRow
              key={po.id} po={po} pending={actionPending}
              canCreateInbound={canReceive && hasOpenQuantity(po)}
              onSend={() => setConfirmSend(po)}
              onCreateInbound={() => { createInbound.reset(); setNotice(null); setInboundFor(po) }}
            />
          ))}
        </tbody>
      </table>
      </div>

      <ConfirmDialog
        open={confirmSend !== null}
        title={t('purchasing:po.send.title')}
        confirmLabel={t('purchasing:po.send.label')}
        onConfirm={() => { if (confirmSend) send.mutate(confirmSend.id); setConfirmSend(null) }}
        onCancel={() => setConfirmSend(null)}
      >
        <p>
          <Trans
            i18nKey="purchasing:po.send.body"
            values={{ number: confirmSend?.poNumber, supplier: confirmSend?.supplierName ?? t('purchasing:po.send.theSupplier') }}
            components={{ code: <code /> }}
          />
        </p>
      </ConfirmDialog>

      {inboundFor && (
        <CreateInboundDialog
          po={inboundFor}
          bins={bins.data ?? []}
          pending={createInbound.isPending}
          error={createInbound.error}
          onCancel={() => setInboundFor(null)}
          onConfirm={(targetBinId) => createInbound.mutate({ id: inboundFor.id, req: { targetBinId } }, {
            onSuccess: (created) => {
              setInboundFor(null)
              setNotice(t('purchasing:po.inboundCreated', { shipment: created.shipmentNumber, po: inboundFor.poNumber, count: created.lines.length }))
            },
          })}
        />
      )}
    </>
  )
}

/// <summary>
/// Rückfrage für "Wareneingang anlegen": nennt die offenen Mengen, verlangt den Ziel-Lagerplatz (alle Zeilen gehen dorthin) und
/// sagt ehrlich, dass noch nichts gebucht wird. Ein Fehler des Servers (z. B. es gibt schon einen offenen Wareneingang zu dieser
/// Bestellung) bleibt im Dialog stehen, damit man einen anderen Platz wählen oder abbrechen kann.
/// </summary>
function CreateInboundDialog({ po, bins, pending, error, onConfirm, onCancel }: {
  po: PurchaseOrderDto
  bins: import('../api/types').StorageLocationDto[]
  pending: boolean
  error: unknown
  onConfirm: (targetBinId: string) => void
  onCancel: () => void
}) {
  const { t } = useTranslation()
  const [binId, setBinId] = useState('')
  const open = po.lines.filter((l) => openQty(l) > 0)
  return (
    <ConfirmDialog
      open
      title={t('purchasing:po.inbound.title')}
      confirmLabel={t('purchasing:po.inbound.label')}
      pending={pending}
      confirmDisabled={binId === ''}
      onConfirm={() => onConfirm(binId)}
      onCancel={onCancel}
    >
      <p>
        <Trans i18nKey="purchasing:po.inbound.body" values={{ number: po.poNumber }} components={{ code: <code />, strong: <strong /> }} />
      </p>
      <ul style={{ margin: '0 0 8px', paddingLeft: 18 }}>
        {open.map((l) => <li key={l.id}>{openQty(l)} × <code>{l.articleSku}</code></li>)}
      </ul>
      <label>
        {t('purchasing:po.inbound.target')}
        <select value={binId} onChange={(e) => setBinId(e.target.value)} data-autofocus>
          <option value="">{t('purchasing:choose')}</option>
          {bins.map((b) => <option key={b.id} value={b.id}>{b.code}</option>)}
        </select>
      </label>
      <ErrorBanner error={error} title={t('purchasing:createFailed')} style={{ marginTop: 8 }} />
    </ConfirmDialog>
  )
}

function PoRow({ po, onSend, onCreateInbound, canCreateInbound, pending }: {
  po: PurchaseOrderDto; onSend: () => void; onCreateInbound: () => void; canCreateInbound: boolean; pending: boolean
}) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  return (
    <>
      <tr style={{ opacity: po.status === 'Cancelled' ? 0.5 : 1 }}>
        <td><code>{po.poNumber}</code></td>
        <td>{po.supplierName ?? <span className="muted">?</span>}</td>
        <td><StatusPill domain="purchaseOrder" status={po.status} /></td>
        <td>{formatMoney(po.totalValueCents, po.currency)}</td>
        <td className="muted" style={{ fontSize: 12 }}>{formatDate(po.createdAt)}</td>
        <td className="muted" style={{ fontSize: 12 }}>{formatDateOnly(po.expectedDate)}</td>
        <td>
          <button onClick={() => setOpen(!open)} aria-expanded={open}>{open ? t('purchasing:po.row.close') : t('purchasing:po.row.details')}</button>
          {po.status === 'Draft' && (
            <button className="primary" onClick={onSend} disabled={pending} style={{ marginLeft: 4 }}>{t('purchasing:po.send.label')}</button>
          )}
          {canCreateInbound && (
            <button className="primary" onClick={onCreateInbound} disabled={pending} style={{ marginLeft: 4 }}>
              {t('purchasing:po.row.createInbound')}
            </button>
          )}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7} style={{ background: 'var(--c-surface-alt)' }}>
            <table style={{ width: '100%' }}>
              <thead><tr><th scope="col">{t('purchasing:po.row.sku')}</th><th scope="col" style={{ textAlign: 'right' }}>{t('purchasing:po.row.ordered')}</th><th scope="col" style={{ textAlign: 'right' }}>{t('purchasing:po.row.received')}</th><th scope="col" style={{ textAlign: 'right' }}>{t('purchasing:po.row.price')}</th></tr></thead>
              <tbody>
                {po.lines.map((l) => (
                  <tr key={l.id}>
                    <td><code>{l.articleSku}</code></td>
                    <td style={{ textAlign: 'right' }}>{l.orderedQty}</td>
                    <td style={{ textAlign: 'right' }}>{l.receivedQty}</td>
                    <td style={{ textAlign: 'right' }}>{formatMoney(l.unitPriceCents, po.currency)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </td>
        </tr>
      )}
    </>
  )
}

function CreatePoForm({ suggestion, suppliers, articles, onCancel, onCreate, pending, error, onDismissError }: {
  suggestion: PurchaseSuggestionDto | null
  suppliers: import('../api/types').SupplierDto[]
  articles: import('../api/types').ArticleDto[]
  onCancel: () => void
  onCreate: (req: import('../api/types').CreatePurchaseOrderRequest) => void
  pending: boolean
  error: unknown
  onDismissError: () => void
}) {
  const { t } = useTranslation()
  const [supplierId, setSupplierId] = useState(suggestion?.supplierId ?? '')
  const [expectedDate, setExpectedDate] = useState('')
  const [notes, setNotes] = useState('')
  const [lines, setLines] = useState<{ articleId: string; orderedQty: number; unitPriceCents: number }[]>(
    suggestion?.lines.map((l) => ({ articleId: l.articleId, orderedQty: l.suggestedOrderQty, unitPriceCents: 0 })) ??
    [{ articleId: '', orderedQty: 1, unitPriceCents: 0 }]
  )

  const submit = () => {
    onCreate({
      supplierId,
      expectedDate: expectedDate || null,
      notes: notes || null,
      lines: lines.filter((l) => l.articleId && l.orderedQty > 0),
    })
  }

  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <h3>{suggestion ? t('purchasing:po.form.titleSuggestion', { supplier: suggestion.supplierName }) : t('purchasing:po.form.titleNew')}</h3>
      <div className="grid-3">
        <label>{t('purchasing:po.form.supplier')}
          <select value={supplierId} onChange={(e) => setSupplierId(e.target.value)} disabled={!!suggestion}>
            <option value="">{t('purchasing:choose')}</option>
            {suppliers.map((s) => <option key={s.id} value={s.id}>{s.code} — {s.name}</option>)}
          </select>
        </label>
        <label>{t('purchasing:po.form.expected')} <input type="date" value={expectedDate} onChange={(e) => setExpectedDate(e.target.value)} /></label>
        <label>{t('purchasing:po.form.notes')} <input value={notes} onChange={(e) => setNotes(e.target.value)} /></label>
      </div>
      <div className="table-wrap">
      <table style={{ width: '100%', marginTop: 8 }}>
        <caption className="visually-hidden">{t('purchasing:po.form.caption')}</caption>
        <thead><tr><th scope="col">{t('purchasing:po.form.article')}</th><th scope="col">{t('purchasing:po.form.quantity')}</th><th scope="col">{t('purchasing:po.form.price')}</th><th scope="col"><span className="visually-hidden">{t('purchasing:po.col.actions')}</span></th></tr></thead>
        <tbody>
          {lines.map((l, idx) => (
            <tr key={idx}>
              <td>
                <select aria-label={t('purchasing:po.form.articleAria', { n: idx + 1 })} value={l.articleId} onChange={(e) => setLines((s) => s.map((x, i) => i === idx ? { ...x, articleId: e.target.value, unitPriceCents: articles.find((a) => a.id === e.target.value)?.purchasePriceCents ?? 0 } : x))}>
                  <option value="">{t('purchasing:choose')}</option>
                  {articles.map((a) => <option key={a.id} value={a.id}>{a.sku} — {a.name}</option>)}
                </select>
              </td>
              <td><input type="number" min={1} aria-label={t('purchasing:po.form.quantityAria', { n: idx + 1 })} value={l.orderedQty} onChange={(e) => setLines((s) => s.map((x, i) => i === idx ? { ...x, orderedQty: +e.target.value } : x))} style={{ width: 80 }} /></td>
              <td><input type="number" min={0} aria-label={t('purchasing:po.form.priceAria', { n: idx + 1 })} value={l.unitPriceCents} onChange={(e) => setLines((s) => s.map((x, i) => i === idx ? { ...x, unitPriceCents: +e.target.value } : x))} style={{ width: 100 }} /></td>
              <td><button type="button" onClick={() => setLines((s) => s.filter((_, i) => i !== idx))} disabled={lines.length === 1} aria-label={t('purchasing:po.form.removeAria', { n: idx + 1 })}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>
      <div className="toolbar" style={{ marginTop: 8 }}>
        <button onClick={() => setLines((s) => [...s, { articleId: '', orderedQty: 1, unitPriceCents: 0 }])}>{t('purchasing:po.form.addLine')}</button>
        <button className="primary" onClick={submit} disabled={pending || !supplierId || !lines.some((l) => l.articleId)}>
          {pending ? t('common:saving') : t('purchasing:po.form.create')}
        </button>
        <button onClick={onCancel}>{t('common:cancel')}</button>
      </div>
      <ErrorBanner error={error} title={t('purchasing:createFailed')} onDismiss={onDismissError} />
    </div>
  )
}
