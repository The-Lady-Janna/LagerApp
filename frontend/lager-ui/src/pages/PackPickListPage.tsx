import { Fragment, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { usePackPickList, usePickList } from '../api/hooks'
import type { PickItemDto } from '../api/types'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { describeDownloadError, downloadFile } from '../lib/download'
import { findDeviations, readPickedQuantities } from '../lib/pickCounts'

/// <summary>
/// Packen: pro Position die Ist-Menge erfassen (0..Soll — der Server lehnt Mengen außerhalb ab, deshalb
/// begrenzt schon das Eingabefeld) und "Packen abschließen" bucht den Bestand. Bei Abweichungen vom Soll fragt
/// window.confirm nach; der Button ist gesperrt, solange die Buchung läuft. Fehler zeigt der ErrorBanner.
/// </summary>
export function PackPickListPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const navigate = useNavigate()
  const location = useLocation()
  const { data: pl, isLoading, error, refetch } = usePickList(id)
  const packMut = usePackPickList()

  // Nur die vom User geänderten Ist-Mengen (pro Position). Alles andere wird
  // abgeleitet: bestätigte Menge, sonst die im Mobile-Picker gezählte Menge
  // (Router-State), sonst Plan-Menge — kein Effect zum Vorbelegen nötig.
  const [overrides, setOverrides] = useState<Record<string, number>>({})
  const [pdfBusy, setPdfBusy] = useState(false)
  const [pdfError, setPdfError] = useState<string | null>(null)

  if (!pl) return <LoadState isLoading={isLoading} error={error} what={t('picking:detail.what')} onRetry={() => void refetch()} />

  // Vom Mobile-Picker übergebene Zählstände (Router-State): sie ersetzen die Soll-Menge als Vorbelegung,
  // damit ein Kurz-Pick nicht unbemerkt als volle Menge gebucht wird.
  const pickedQuantities = readPickedQuantities(location.state, pl.items)
  const actualOf = (item: PickItemDto) =>
    overrides[item.id] ?? item.confirmedQuantity ?? pickedQuantities[item.id] ?? item.quantity

  // Ist-Menge: ganze Zahl zwischen 0 und Soll (der Server lehnt Mengen außerhalb von 0..Soll beim Packen ab).
  const setActual = (item: PickItemDto, value: number) =>
    setOverrides((s) => ({ ...s, [item.id]: Math.min(item.quantity, Math.max(0, Math.trunc(value) || 0)) }))

  const setAllToPlanned = () => {
    const fresh: Record<string, number> = {}
    for (const item of pl.items) fresh[item.id] = item.quantity
    setOverrides(fresh)
  }

  // Lieferschein mit Authorization-Header laden (ein <a href> schickt keinen Token).
  const downloadPdf = async () => {
    setPdfBusy(true)
    setPdfError(null)
    try {
      await downloadFile(`/picklists/${id}/shipping-label.pdf`, `lieferschein-${pl.pickListNumber}.pdf`)
    } catch (err) {
      setPdfError(describeDownloadError(err))
    } finally {
      setPdfBusy(false)
    }
  }

  const totalPlanned = pl.items.reduce((sum, i) => sum + i.quantity, 0)
  const totalActual = pl.items.reduce((sum, i) => sum + actualOf(i), 0)
  const allMatch = pl.items.every((i) => actualOf(i) === i.quantity)
  const isPacked = pl.status === 'Completed'
  const pickerCounted = pl.items.filter((i) => pickedQuantities[i.id] !== undefined)
  const pickerDeviations = findDeviations(pl.items, pickedQuantities)

  // mutate statt mutateAsync: ein Fehler bleibt in packMut.error (ErrorBanner unten), keine unbehandelte Rejection.
  const confirm = () => {
    if (!id || packMut.isPending) return
    if (!allMatch) {
      if (!confirm_dialog(t('picking:pack.deviationConfirm'))) return
    }
    packMut.mutate({
      id,
      req: {
        items: pl.items.map((i) => ({
          pickItemId: i.id,
          actualQuantity: actualOf(i),
        })),
      },
    }, {
      onSuccess: (result) => { if (result) navigate(`/picklists/${result.id}`) },
    })
  }

  return (
    <>
      <h2>{t('picking:pack.title', { number: pl.pickListNumber })}</h2>

      <div className="card">
        <div className="grid-3">
          <div><strong>{t('picking:detail.status')}</strong> {t(`status:pickList.${pl.status}`, { defaultValue: pl.status })}{isPacked ? t('picking:pack.alreadyPacked') : ''}</div>
          <div><strong>{t('picking:detail.lines')}</strong> {pl.items.length}</div>
          <div><strong>{t('picking:detail.cart')}</strong> {pl.pickCartConfigName ?? '—'}</div>
        </div>
      </div>

      <div className="card">
        <h3>{t('picking:pack.parcels')}</h3>
        <p className="muted">
          <Trans i18nKey="picking:pack.intro" components={{ strong: <strong /> }} />
        </p>
        {!isPacked && pickerCounted.length > 0 && (
          <div className="success" role="status" style={{ marginBottom: 8 }}>
            {t('picking:pack.fromPicker.counted', { count: pickerCounted.length })}
            {pickerDeviations.length > 0
              ? t('picking:pack.fromPicker.deviations', {
                  count: pickerDeviations.length,
                  list: pickerDeviations.map((d) => `${d.item.articleSku} ${d.actual}/${d.item.quantity}`).join(', '),
                })
              : t('picking:pack.fromPicker.none')}
            {t('picking:pack.fromPicker.check')}
          </div>
        )}
        <div className="toolbar">
          <button onClick={setAllToPlanned} disabled={isPacked}>{t('picking:pack.allPlanned')}</button>
          <span style={{ marginLeft: 'auto' }} className="muted">
            <Trans
              i18nKey={allMatch ? 'picking:pack.totalsMatch' : 'picking:pack.totalsDiff'}
              values={{ planned: totalPlanned, actual: totalActual }}
              components={{ strong: <strong /> }}
            />
          </span>
        </div>

        {/* One table for the whole pick list. Each parcel = a wide header row,
            followed by the items belonging to that order. Single <colgroup>
            keeps every column aligned across parcels. */}
        {(() => {
          const grouped = new Map<string, typeof pl.items>()
          for (const item of pl.items) {
            const k = item.orderId
            if (!grouped.has(k)) grouped.set(k, [])
            grouped.get(k)!.push(item)
          }
          const parcels = Array.from(grouped.entries())
          return (
            <table style={{ tableLayout: 'fixed', width: '100%', minWidth: 640, marginTop: 12 }}>
              <caption className="visually-hidden">{t('picking:pack.caption')}</caption>
              <colgroup>
                <col style={{ width: '4%' }} />
                <col style={{ width: '20%' }} />
                <col style={{ width: '28%' }} />
                <col style={{ width: '14%' }} />
                <col style={{ width: '8%' }} />
                <col style={{ width: '12%' }} />
                <col style={{ width: '14%' }} />
              </colgroup>
              <thead>
                <tr>
                  <th scope="col">#</th>
                  <th scope="col">{t('picking:col.sku')}</th>
                  <th scope="col">{t('picking:pack.colArticle')}</th>
                  <th scope="col">{t('picking:col.location')}</th>
                  <th scope="col" style={{ textAlign: 'right' }}>{t('picking:pack.colPlanned')}</th>
                  <th scope="col" style={{ textAlign: 'right' }}>{t('picking:pack.colActual')}</th>
                  <th scope="col">{t('picking:col.status')}</th>
                </tr>
              </thead>
              <tbody>
                {parcels.map(([orderId, items], parcelIdx) => {
                  const orderNumber = items[0]?.orderNumber ?? '?'
                  const parcelPlanned = items.reduce((s, i) => s + i.quantity, 0)
                  const parcelActual = items.reduce((s, i) => s + actualOf(i), 0)
                  const parcelMatch = parcelPlanned === parcelActual
                  return (
                    <Fragment key={orderId}>
                      <tr>
                        <td
                          colSpan={7}
                          style={{
                            background: 'var(--c-info-bg)',
                            borderLeft: '4px solid var(--c-primary)',
                            fontWeight: 600,
                            padding: '8px 12px',
                          }}
                        >
                          <Trans i18nKey="picking:pack.parcel" values={{ n: parcelIdx + 1, order: orderNumber }} components={{ code: <code /> }} />
                          <span className="muted" style={{ fontSize: 12, marginLeft: 8, fontWeight: 'normal' }}>
                            {t('picking:pack.parcelInfo', { count: items.length, planned: parcelPlanned, actual: parcelActual, mark: parcelMatch ? ' ✓' : ' ⚠' })}
                          </span>
                        </td>
                      </tr>
                      {items.map((i) => {
                        const actual = actualOf(i)
                        const match = actual === i.quantity
                        return (
                          <tr key={i.id}>
                            <td>{i.sequenceNumber}</td>
                            <td><code style={{ wordBreak: 'break-all' }}>{i.articleSku}</code></td>
                            <td style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={i.articleName}>{i.articleName}</td>
                            <td><code>{i.storageLocationCode}</code></td>
                            <td style={{ textAlign: 'right' }}>{i.quantity}</td>
                            <td style={{ textAlign: 'right' }}>
                              <input
                                type="number"
                                min={0}
                                max={i.quantity}
                                step={1}
                                aria-label={t('picking:pack.actualAria', { sku: i.articleSku })}
                                value={actual}
                                onChange={(e) => setActual(i, +e.target.value)}
                                disabled={isPacked}
                                style={{ width: '100%', textAlign: 'right' }}
                              />
                            </td>
                            <td>
                              {isPacked
                                ? <span className="muted">{t('picking:pack.confirmed', { quantity: i.confirmedQuantity ?? 0 })}</span>
                                : (match ? <span className="text-success">{t('picking:pack.ok')}</span> : <span className="text-danger">Δ {actual - i.quantity}</span>)}
                            </td>
                          </tr>
                        )
                      })}
                    </Fragment>
                  )
                })}
              </tbody>
            </table>
          )
        })()}

        <div className="toolbar" style={{ marginTop: 16 }}>
          <button
            className="primary"
            onClick={confirm}
            disabled={isPacked || packMut.isPending}
          >
            {isPacked ? t('picking:pack.alreadyPackedButton') : packMut.isPending ? t('picking:pack.confirming') : t('picking:pack.finish')}
          </button>
          {/* Lieferschein-PDF — separat von "Packen abschließen", weil man ihn
              auch nach dem Packen nochmal drucken können soll. Wird per Axios
              (mit Authorization-Header) als Blob geladen und als Datei
              gespeichert. */}
          <button type="button" onClick={downloadPdf} disabled={pdfBusy}>
            {pdfBusy ? t('picking:pack.pdfBusy') : t('picking:pack.pdf')}
          </button>
          <button onClick={() => navigate(`/picklists/${id}`)}>{t('picking:pack.backToList')}</button>
        </div>
        {pdfError && <div className="error" role="alert" style={{ marginTop: 8 }}>{pdfError}</div>}
        <ErrorBanner error={packMut.error} title={t('picking:pack.failed')} onDismiss={packMut.reset} style={{ marginTop: 8 }} />
      </div>
    </>
  )
}

// Window confirm wrapper so the function called `confirm` doesn't shadow the page-local handler.
function confirm_dialog(message: string): boolean {
  return window.confirm(message)
}
