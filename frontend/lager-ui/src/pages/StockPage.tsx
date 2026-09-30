import { Fragment, useMemo, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useAdjustStock, useArticles, useStock, useStockAlerts, useStockSummary, useStorageLocations } from '../api/hooks'
import type { StockItemDto } from '../api/types'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { compareExpiry, traceabilityPath } from '../features/traceability/expiry'
import { ExpiryPill } from '../features/traceability/ExpiryPill'
import { formatDateOnly } from '../lib/format'

/** Auswahlwert "Neue Charge" im Feld Charge der Buchung. */
const NEW_LOT = '__new__'

/** Bestandszeilen in FEFO-Reihenfolge: frühestes MHD zuerst, ohne MHD zuletzt, dann nach Lagerplatz. */
function fefo(rows: StockItemDto[]): StockItemDto[] {
  return [...rows].sort((a, b) => compareExpiry(a.expiryDate, b.expiryDate) || a.storageLocationCode.localeCompare(b.storageLocationCode))
}

/** Chargennummer als Link zur Rückverfolgung (ohne Charge: Strich). */
function LotLink({ lot }: { lot: string | null }) {
  const { t } = useTranslation()
  if (!lot) return <span className="muted">—</span>
  return <Link to={traceabilityPath(lot)} title={t('traceability:openTrace')}>{lot}</Link>
}

/** MHD als Datum mit Status-Pill (ohne MHD: Strich). */
function ExpiryCell({ expiryDate }: { expiryDate: string | null }) {
  if (!expiryDate) return <span className="muted">—</span>
  return <>{formatDateOnly(expiryDate)} <ExpiryPill expiryDate={expiryDate} /></>
}

export function StockPage() {
  const { t } = useTranslation()
  const summary = useStockSummary()
  const detail = useStock()
  const articles = useArticles()
  const locations = useStorageLocations()
  const adjust = useAdjustStock()
  const alerts = useStockAlerts()

  const [articleId, setArticleId] = useState('')
  const [locationId, setLocationId] = useState('')
  const [delta, setDelta] = useState(0)
  // Charge der Buchung: '' = ohne Charge (beim Abgang: automatisch), die Id einer vorhandenen Bestandszeile oder NEW_LOT.
  const [lotChoice, setLotChoice] = useState('')
  const [newLot, setNewLot] = useState('')
  const [newExpiry, setNewExpiry] = useState('')
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set())

  // Bestandszeilen je Artikel (FEFO): Basis der Unterzeilen je Charge/MHD und der Chargen-Auswahl der Buchung.
  const rowsByArticle = useMemo(() => {
    const map = new Map<string, StockItemDto[]>()
    for (const row of detail.data ?? []) map.set(row.articleId, [...(map.get(row.articleId) ?? []), row])
    for (const [id, rows] of map) map.set(id, fefo(rows))
    return map
  }, [detail.data])

  // Chargen des gewählten Artikels im gewählten Lagerplatz (nur Zeilen mit Charge oder MHD; die chargenlose Zeile ist "ohne Charge").
  const lotChoices = (rowsByArticle.get(articleId) ?? []).filter((r) => r.storageLocationId === locationId && (r.lotNumber || r.expiryDate))
  const chosenRow = lotChoices.find((r) => r.id === lotChoice)
  const creatingLot = lotChoice === NEW_LOT
  const lotMissing = creatingLot && newLot.trim() === ''

  const resetLot = () => { setLotChoice(''); setNewLot(''); setNewExpiry('') }

  // mutate statt mutateAsync: ein Fehler bleibt in adjust.error (ErrorBanner unten), keine unbehandelte Rejection.
  const submit = () => {
    if (!articleId || !locationId || delta === 0 || adjust.isPending || lotMissing) return
    const lotNumber = creatingLot ? newLot.trim() : chosenRow?.lotNumber ?? null
    const expiry = creatingLot ? newExpiry : chosenRow?.expiryDate?.slice(0, 10) ?? ''
    adjust.mutate(
      { articleId, storageLocationId: locationId, delta, lotNumber, expiryDate: expiry || null },
      { onSuccess: () => { setDelta(0); resetLot() } },
    )
  }

  const onDeltaChange = (value: number) => {
    setDelta(value)
    // "Neue Charge" gibt es nur bei einem Zugang; ein Abgang braucht eine vorhandene Charge (oder die Automatik).
    if (value < 0 && creatingLot) resetLot()
  }

  const toggle = (id: string) => setExpanded((prev) => {
    const next = new Set(prev)
    if (!next.delete(id)) next.add(id)
    return next
  })

  const critical = alerts.data?.filter((a) => a.severity === 'critical') ?? []
  const warning = alerts.data?.filter((a) => a.severity === 'warning') ?? []

  return (
    <>
      <h2>{t('stock:title')}</h2>

      {alerts.data && alerts.data.length > 0 && (
        <div className="card" style={{ borderLeft: `4px solid ${critical.length > 0 ? 'var(--c-danger)' : 'var(--c-warning)'}`, marginBottom: 12 }}>
          <h3 style={{ marginTop: 0 }}>{t('stock:alerts.title')}</h3>
          {critical.length > 0 && (
            <details open>
              <summary className="text-danger" style={{ fontWeight: 600, cursor: 'pointer' }}>
                {t('stock:alerts.critical', { count: critical.length })}
              </summary>
              <ul style={{ marginTop: 4 }}>
                {critical.map((a) => (
                  <li key={a.articleId}>
                    <code>{a.articleSku}</code>{' '}
                    <Trans i18nKey="stock:alerts.lineMin" values={{ name: a.articleName, quantity: a.totalQuantity, min: a.minStock }} components={{ strong: <strong /> }} />
                  </li>
                ))}
              </ul>
            </details>
          )}
          {warning.length > 0 && (
            <details>
              <summary className="text-warning" style={{ fontWeight: 600, cursor: 'pointer' }}>
                {t('stock:alerts.warning', { count: warning.length })}
              </summary>
              <ul style={{ marginTop: 4 }}>
                {warning.map((a) => (
                  <li key={a.articleId}>
                    <code>{a.articleSku}</code>{' '}
                    <Trans i18nKey="stock:alerts.lineReorder" values={{ name: a.articleName, quantity: a.totalQuantity, reorder: a.reorderPoint }} components={{ strong: <strong /> }} />
                  </li>
                ))}
              </ul>
            </details>
          )}
        </div>
      )}

      <CollapsibleCard title={t('stock:booking.title')} storageKey="stock-booking" defaultOpen={false}>
        <div className="grid-3">
          <label>
            {t('stock:booking.article')}
            <select value={articleId} onChange={(e) => { setArticleId(e.target.value); resetLot() }}>
              <option value="">{t('stock:booking.choose')}</option>
              {articles.data?.map((a) => <option key={a.id} value={a.id}>{a.sku} – {a.name}</option>)}
            </select>
          </label>
          <label>
            {t('stock:booking.location')}
            <select value={locationId} onChange={(e) => { setLocationId(e.target.value); resetLot() }}>
              <option value="">{t('stock:booking.choose')}</option>
              {locations.data?.map((l) => <option key={l.id} value={l.id}>{l.code}</option>)}
            </select>
          </label>
          <label>
            {t('stock:booking.delta')}
            <input type="number" value={delta} onChange={(e) => onDeltaChange(+e.target.value)} />
          </label>
        </div>
        {articleId && locationId && (
          <div className="grid-3" style={{ marginTop: 12 }}>
            <label>
              {t('stock:booking.lot')}
              <select value={lotChoice} onChange={(e) => setLotChoice(e.target.value)}>
                <option value="">{delta < 0 ? t('stock:booking.lotAuto') : t('stock:booking.lotNone')}</option>
                {lotChoices.map((r) => (
                  <option key={r.id} value={r.id}>
                    {t('stock:booking.lotOption', { lot: r.lotNumber ?? t('stock:booking.noLot'), expiry: formatDateOnly(r.expiryDate), quantity: r.quantity })}
                  </option>
                ))}
                {delta >= 0 && <option value={NEW_LOT}>{t('stock:booking.newLot')}</option>}
              </select>
            </label>
            {creatingLot && (
              <>
                <label>
                  {t('stock:booking.newLotNumber')}
                  <input value={newLot} onChange={(e) => setNewLot(e.target.value)} maxLength={64} aria-invalid={lotMissing} />
                </label>
                <label>
                  {t('stock:booking.expiryOptional')}
                  <input type="date" value={newExpiry} onChange={(e) => setNewExpiry(e.target.value)} />
                </label>
              </>
            )}
          </div>
        )}
        <div className="toolbar" style={{ marginTop: 12 }}>
          <button className="primary" onClick={submit} disabled={adjust.isPending || !articleId || !locationId || delta === 0 || lotMissing}>
            {t('stock:booking.submit')}
          </button>
        </div>
        <ErrorBanner error={adjust.error} title={t('stock:booking.failed')} onDismiss={adjust.reset} style={{ marginTop: 8 }} />
      </CollapsibleCard>

      <CollapsibleCard title={t('stock:summary.title')} storageKey="stock-summary" defaultOpen>
        {!summary.data && <LoadState isLoading={summary.isLoading} error={summary.error} what={t('stock:summary.what')} onRetry={() => void summary.refetch()} />}
        <table>
          <caption className="visually-hidden">{t('stock:summary.caption')}</caption>
          <thead>
            <tr>
              <th scope="col"><span className="visually-hidden">{t('stock:summary.lots')}</span></th>
              <th scope="col">{t('stock:col.sku')}</th><th scope="col">{t('stock:col.name')}</th><th scope="col">{t('stock:summary.total')}</th><th scope="col">{t('stock:summary.locations')}</th><th scope="col">{t('stock:summary.nextExpiry')}</th>
            </tr>
          </thead>
          <tbody>
            {summary.data?.map((s) => {
              const rows = rowsByArticle.get(s.articleId) ?? []
              const hasLots = rows.some((r) => r.lotNumber || r.expiryDate) || rows.length > 1
              const nextExpiry = rows.find((r) => r.expiryDate && r.quantity > 0)?.expiryDate ?? null
              const open = expanded.has(s.articleId)
              return (
                <Fragment key={s.articleId}>
                  <tr>
                    <td>
                      {hasLots && (
                        <button
                          type="button"
                          onClick={() => toggle(s.articleId)}
                          aria-expanded={open}
                          aria-label={open ? t('stock:summary.hideLots', { sku: s.articleSku }) : t('stock:summary.showLots', { sku: s.articleSku })}
                          style={{ padding: '0 6px' }}
                        >
                          {open ? '▾' : '▸'}
                        </button>
                      )}
                    </td>
                    <td><code>{s.articleSku}</code></td>
                    <td>{s.articleName}</td>
                    <td>{s.totalQuantity}</td>
                    <td>{s.locationCount}</td>
                    <td><ExpiryCell expiryDate={nextExpiry} /></td>
                  </tr>
                  {open && (
                    <tr>
                      <td colSpan={6} style={{ background: 'var(--c-surface-alt)' }}>
                        <table style={{ width: '100%' }}>
                          <caption className="visually-hidden">{t('stock:summary.lotCaption', { sku: s.articleSku })}</caption>
                          <thead>
                            <tr>
                              <th scope="col">{t('stock:col.location')}</th><th scope="col">{t('stock:col.lot')}</th><th scope="col">{t('stock:col.expiry')}</th>
                              <th scope="col" style={{ textAlign: 'right' }}>{t('stock:col.quantity')}</th>
                            </tr>
                          </thead>
                          <tbody>
                            {rows.map((r) => (
                              <tr key={r.id}>
                                <td><code>{r.storageLocationCode}</code></td>
                                <td><LotLink lot={r.lotNumber} /></td>
                                <td><ExpiryCell expiryDate={r.expiryDate} /></td>
                                <td style={{ textAlign: 'right' }}>{r.quantity}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </td>
                    </tr>
                  )}
                </Fragment>
              )
            })}
            {summary.data?.length === 0 && (
              <tr><td colSpan={6} className="muted">{t('stock:summary.empty')}</td></tr>
            )}
          </tbody>
        </table>
      </CollapsibleCard>

      <CollapsibleCard title={t('stock:detail.title')} storageKey="stock-detail" defaultOpen={false}>
        {!detail.data && <LoadState isLoading={detail.isLoading} error={detail.error} what={t('stock:detail.what')} onRetry={() => void detail.refetch()} />}
        <table>
          <caption className="visually-hidden">{t('stock:detail.caption')}</caption>
          <thead><tr><th scope="col">{t('stock:col.location')}</th><th scope="col">{t('stock:col.sku')}</th><th scope="col">{t('stock:col.article')}</th><th scope="col">{t('stock:col.quantity')}</th><th scope="col">{t('stock:col.lot')}</th><th scope="col">{t('stock:col.expiry')}</th></tr></thead>
          <tbody>
            {detail.data?.map((s) => (
              <tr key={s.id}>
                <td><code>{s.storageLocationCode}</code></td>
                <td><code>{s.articleSku}</code></td>
                <td>{s.articleName}</td>
                <td>{s.quantity}</td>
                <td><LotLink lot={s.lotNumber} /></td>
                <td><ExpiryCell expiryDate={s.expiryDate} /></td>
              </tr>
            ))}
            {detail.data?.length === 0 && (
              <tr><td colSpan={6} className="muted">{t('stock:detail.empty')}</td></tr>
            )}
          </tbody>
        </table>
      </CollapsibleCard>
    </>
  )
}
