import { useMemo, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useArticles, useCreateInbound, useInboundList, usePutawaySuggestions, useReceiveInbound, useStock, useStorageLocations } from '../api/hooks'
import type { InboundShipmentDto } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { StatusPill } from '../components/StatusPill'
import { todayIso } from '../features/traceability/expiry'
import { ExpiryPill } from '../features/traceability/ExpiryPill'
import { autoShipmentNumber, checkInboundLines, emptyLine, isUntouched, type InboundLineDraft } from '../features/traceability/inboundLines'
import { formatDateOnly, formatDateTime } from '../lib/format'

/// <summary>
/// Wareneingang: Liste aller Shipments + Form für neuen Eingang mit Bulk-Lines (Artikel, Ziel-Bin, Menge, Charge, MHD).
/// Die Zeilen werden vor dem Senden geprüft (features/traceability/inboundLines.ts): kein MHD in der Vergangenheit, Charge
/// und MHD als Pflicht bei Artikeln, die schon mit MHD geführt werden, keine halb befüllte Zeile (sie würde sonst still
/// wegfallen). Der Server legt Kopf und Zeilen atomar an; kommen weniger Zeilen zurück als gesendet, meldet die Seite es.
/// "Empfangen"-Button bucht den Bestand atomar in die Ziel-Bins — nach einer Rückfrage
/// (ConfirmDialog); während die Buchung läuft, sind die Buttons gesperrt.
/// </summary>
export function InboundPage() {
  const { t } = useTranslation()
  const { data: shipments, isLoading, error, refetch } = useInboundList()
  const articles = useArticles()
  const bins = useStorageLocations()
  const stock = useStock()
  const createMut = useCreateInbound()
  const receiveMut = useReceiveInbound()

  const [showForm, setShowForm] = useState(false)
  const [confirmReceive, setConfirmReceive] = useState<InboundShipmentDto | null>(null)
  const [shipmentNumber, setShipmentNumber] = useState('')
  const [supplierRef, setSupplierRef] = useState('')
  const [lines, setLines] = useState<InboundLineDraft[]>([emptyLine()])
  // Prüfergebnis der letzten Absendung (Meldungen mit Zeilennummer) und Hinweis nach erfolgreichem Anlegen.
  const [problems, setProblems] = useState<string[]>([])
  const [notice, setNotice] = useState<string | null>(null)

  // Artikel, die schon mit MHD geführt werden (Bestandszeile mit MHD): dort sind Charge und MHD Pflicht ("falls erkennbar").
  const trackedArticles = useMemo(() => {
    const ids = new Set<string>()
    for (const row of stock.data ?? []) if (row.expiryDate) ids.add(row.articleId)
    return ids
  }, [stock.data])

  const reset = () => {
    setShowForm(false); setShipmentNumber(''); setSupplierRef(''); setProblems([])
    setLines([emptyLine()])
  }

  const changeLines = (update: (current: InboundLineDraft[]) => InboundLineDraft[]) => {
    setLines(update)
    setProblems([])
  }

  // mutate statt mutateAsync: ein Fehler bleibt in createMut.error (wird unten angezeigt) und erzeugt keine unbehandelte Promise-Rejection.
  const submit = () => {
    const check = checkInboundLines(lines, { today: todayIso(), tracksExpiry: (id) => trackedArticles.has(id) })
    setProblems(check.problems)
    setNotice(null)
    if (check.problems.length > 0) return

    createMut.mutate({
      // "Auto wenn leer": der Server verlangt eine Nummer
      shipmentNumber: shipmentNumber.trim() || autoShipmentNumber(),
      supplierReference: supplierRef.trim() || null,
      lines: check.lines,
    }, {
      onSuccess: (created) => {
        // Ein älterer Server ignorierte die Zeilen still und legte eine leere Lieferung an: nie so tun, als wäre alles gut.
        const saved = created.lines?.length ?? 0
        if (saved < check.lines.length) {
          setProblems([t('inbound:partial', { number: created.shipmentNumber, saved, total: check.lines.length })])
          return
        }
        reset()
        setNotice(t('inbound:created', { number: created.shipmentNumber, count: saved }))
      },
    })
  }

  const receive = () => {
    if (confirmReceive) receiveMut.mutate(confirmReceive.id)
    setConfirmReceive(null)
  }

  if (!shipments) return <LoadState isLoading={isLoading} error={error} what={t('inbound:what')} onRetry={() => void refetch()} />

  return (
    <>
      <h2>{t('inbound:title')}</h2>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <button className="primary" onClick={() => { setShowForm(!showForm); setNotice(null) }}>
          {showForm ? t('common:cancel') : t('inbound:new')}
        </button>
      </div>

      {notice && <div className="success" role="status" style={{ marginBottom: 12 }}>{notice}</div>}

      {showForm && (
        <div className="card" style={{ marginBottom: 16 }}>
          <h3>{t('inbound:form.title')}</h3>
          <div className="grid-2">
            <label>{t('inbound:form.number')}
              <input value={shipmentNumber} onChange={(e) => setShipmentNumber(e.target.value)} />
            </label>
            <label>{t('inbound:form.supplierRef')}
              <input value={supplierRef} onChange={(e) => setSupplierRef(e.target.value)} />
            </label>
          </div>
          <h4 style={{ marginBottom: 4 }}>{t('inbound:form.lines')}</h4>
          <div className="table-wrap">
          <table style={{ width: '100%' }}>
            <caption className="visually-hidden">{t('inbound:form.linesCaption')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('inbound:col.article')}</th><th scope="col">{t('inbound:col.targetBin')}</th><th scope="col">{t('inbound:col.quantity')}</th><th scope="col">{t('inbound:col.lot')}</th><th scope="col">{t('inbound:col.expiry')}</th>
                <th scope="col"><span className="visually-hidden">{t('inbound:col.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l, idx) => (
                <LineRow
                  key={idx} line={l} index={idx}
                  articles={articles.data ?? []} bins={bins.data ?? []}
                  tracked={l.articleId !== '' && trackedArticles.has(l.articleId)}
                  today={todayIso()}
                  onChange={(patch) => changeLines((s) => s.map((x, i) => i === idx ? { ...x, ...patch } : x))}
                  onRemove={() => changeLines((s) => s.filter((_, i) => i !== idx))}
                  canRemove={lines.length > 1}
                />
              ))}
            </tbody>
          </table>
          </div>
          <div className="toolbar" style={{ marginTop: 8 }}>
            <button onClick={() => changeLines((s) => [...s, emptyLine()])}>{t('inbound:form.addLine')}</button>
            <button className="primary" onClick={submit} disabled={createMut.isPending || lines.every(isUntouched)}>
              {createMut.isPending ? t('common:saving') : t('inbound:form.create')}
            </button>
          </div>
          {problems.length > 0 && (
            <div className="error error-banner" role="alert" style={{ marginTop: 8 }}>
              <div className="error-banner-text">
                <strong>{t('inbound:form.fix')}</strong>
                <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
                  {problems.map((p) => <li key={p}>{p}</li>)}
                </ul>
              </div>
            </div>
          )}
          <ErrorBanner error={createMut.error} title={t('inbound:form.createFailed')} onDismiss={createMut.reset} style={{ marginTop: 8 }} />
        </div>
      )}

      <ErrorBanner error={receiveMut.error} title={t('inbound:receiveFailed')} onDismiss={receiveMut.reset} />

      <div className="table-wrap">
        <table style={{ width: '100%' }}>
          <caption className="visually-hidden">{t('inbound:list.caption')}</caption>
          <thead>
            <tr><th scope="col">{t('inbound:col.number')}</th><th scope="col">{t('inbound:col.supplier')}</th><th scope="col">{t('inbound:col.status')}</th><th scope="col">{t('inbound:col.lines')}</th><th scope="col">{t('inbound:col.created')}</th><th scope="col">{t('inbound:col.received')}</th><th scope="col"><span className="visually-hidden">{t('inbound:col.actions')}</span></th></tr>
          </thead>
          <tbody>
            {shipments.map((s) => (
              <ShipmentRow key={s.id} shipment={s} onReceive={() => setConfirmReceive(s)} pending={receiveMut.isPending} />
            ))}
          </tbody>
        </table>
      </div>

      <ConfirmDialog
        open={confirmReceive !== null}
        title={t('inbound:confirm.title')}
        confirmLabel={t('inbound:confirm.label')}
        onConfirm={receive}
        onCancel={() => setConfirmReceive(null)}
      >
        <p>
          <Trans
            i18nKey="inbound:confirm.body"
            values={{ number: confirmReceive?.shipmentNumber, count: confirmReceive?.lines.length ?? 0 }}
            components={{ code: <code /> }}
          />
        </p>
      </ConfirmDialog>
    </>
  )
}

function ShipmentRow({ shipment, onReceive, pending }: { shipment: InboundShipmentDto; onReceive: () => void; pending: boolean }) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  return (
    <>
      <tr style={{ opacity: shipment.status === 'Cancelled' ? 0.5 : 1 }}>
        <td><code>{shipment.shipmentNumber}</code></td>
        <td>{shipment.supplierReference ?? <span className="muted">—</span>}</td>
        <td><StatusPill domain="inbound" status={shipment.status} /></td>
        <td>{shipment.lines.length}</td>
        <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(shipment.createdAt)}</td>
        <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(shipment.receivedAt)}</td>
        <td>
          <button onClick={() => setOpen(!open)} aria-expanded={open}>{open ? t('inbound:row.close') : t('inbound:row.details')}</button>
          {shipment.status === 'Draft' && (
            <button className="primary" onClick={onReceive} disabled={pending} style={{ marginLeft: 4 }}>
              {t('inbound:row.receive')}
            </button>
          )}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7} style={{ background: 'var(--c-surface-alt)' }}>
            <table style={{ width: '100%' }}>
              <thead><tr><th scope="col">{t('inbound:col.sku')}</th><th scope="col">{t('inbound:col.bin')}</th><th scope="col" style={{ textAlign: 'right' }}>{t('inbound:col.quantity')}</th><th scope="col">{t('inbound:col.lot')}</th><th scope="col">{t('inbound:col.expiry')}</th></tr></thead>
              <tbody>
                {shipment.lines.map((l) => (
                  <tr key={l.id}>
                    <td><code>{l.articleSku}</code></td>
                    <td><code>{l.targetBinCode}</code></td>
                    <td style={{ textAlign: 'right' }}>{l.quantity}</td>
                    <td>{l.lotNumber ?? <span className="muted">—</span>}</td>
                    <td>
                      {l.expiryDate ? formatDateOnly(l.expiryDate) : <span className="muted">—</span>}
                      {/* Vor dem Buchen: ein abgelaufenes oder kritisches MHD fällt sofort auf */}
                      {shipment.status === 'Draft' && <> <ExpiryPill expiryDate={l.expiryDate} /></>}
                    </td>
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

/// <summary>
/// Eine Wareneingangs-Zeile mit Putaway-Vorschlag. Sobald Artikel + Menge
/// gewählt sind, ruft der Hook die Top-3 Bin-Empfehlungen ab. Klick auf
/// "Übernehmen" füllt das Bin-Feld vor. Lot und MHD sind Pflicht, wenn der Artikel schon mit MHD geführt wird (`tracked`);
/// das MHD-Feld lässt kein Datum vor heute zu (Neuware).
/// </summary>
function LineRow({ line, index, articles, bins, tracked, today, onChange, onRemove, canRemove }: {
  index: number
  line: InboundLineDraft
  articles: import('../api/types').ArticleDto[]
  bins: import('../api/types').StorageLocationDto[]
  tracked: boolean
  today: string
  onChange: (patch: Partial<InboundLineDraft>) => void
  onRemove: () => void
  canRemove: boolean
}) {
  const { t } = useTranslation()
  const suggestions = usePutawaySuggestions(line.articleId || null, line.quantity)
  return (
    <>
      <tr>
        <td>
          <select aria-label={t('inbound:line.articleAria', { n: index + 1 })} value={line.articleId} onChange={(e) => onChange({ articleId: e.target.value })}>
            <option value="">{t('inbound:line.choose')}</option>
            {articles.map((a) => <option key={a.id} value={a.id}>{a.sku} — {a.name}</option>)}
          </select>
        </td>
        <td>
          <select aria-label={t('inbound:line.binAria', { n: index + 1 })} value={line.targetBinId} onChange={(e) => onChange({ targetBinId: e.target.value })}>
            <option value="">{t('inbound:line.choose')}</option>
            {bins.map((b) => <option key={b.id} value={b.id}>{b.code}</option>)}
          </select>
        </td>
        <td><input type="number" min={1} aria-label={t('inbound:line.quantityAria', { n: index + 1 })} value={line.quantity} onChange={(e) => onChange({ quantity: +e.target.value })} style={{ width: 80 }} /></td>
        <td>
          <input
            aria-label={t('inbound:line.lotAria', { n: index + 1 })} value={line.lotNumber} maxLength={64}
            onChange={(e) => onChange({ lotNumber: e.target.value })}
            placeholder={tracked ? t('inbound:line.required') : t('inbound:line.optional')} required={tracked}
          />
        </td>
        <td>
          <input
            type="date" aria-label={t('inbound:line.expiryAria', { n: index + 1 })} value={line.expiryDate} min={today}
            onChange={(e) => onChange({ expiryDate: e.target.value })} required={tracked}
          />
        </td>
        <td><button type="button" onClick={onRemove} disabled={!canRemove} aria-label={t('inbound:line.removeAria', { n: index + 1 })}>×</button></td>
      </tr>
      {tracked && (
        <tr>
          <td colSpan={6} className="muted" style={{ fontSize: 11, padding: '0 8px 4px' }}>
            {t('inbound:line.trackedHint')}
          </td>
        </tr>
      )}
      {line.articleId && suggestions.data && suggestions.data.length > 0 && (
        <tr>
          <td colSpan={6} style={{ background: 'var(--c-surface-alt)', padding: '4px 8px' }}>
            <span className="muted" style={{ fontSize: 11, marginRight: 8 }}>{t('inbound:line.putaway')}</span>
            {suggestions.data.slice(0, 3).map((s) => (
              <button
                key={s.binId}
                type="button"
                onClick={() => onChange({ targetBinId: s.binId })}
                aria-pressed={line.targetBinId === s.binId}
                style={{
                  marginRight: 4, fontSize: 11, padding: '2px 8px',
                  background: line.targetBinId === s.binId ? 'var(--c-primary)' : 'transparent',
                  color: line.targetBinId === s.binId ? 'var(--c-on-primary)' : 'inherit',
                }}
                title={t('inbound:line.suggestionTitle', { reason: s.reason, score: s.capacityScore })}
              >
                <code>{s.binCode}</code> ({t(`status:binType.${s.binType}`, { defaultValue: s.binType })})
              </button>
            ))}
          </td>
        </tr>
      )}
    </>
  )
}
