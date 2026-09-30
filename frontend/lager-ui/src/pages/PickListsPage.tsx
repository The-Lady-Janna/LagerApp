import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useBulkSeed, usePickLists, useReseed, useResetPickLists } from '../api/hooks'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { formatDateTime } from '../lib/format'
import { useAuth } from '../state/auth'

/// <summary>
/// Picklisten-Übersicht. Die Wartungs-Werkzeuge (Picklisten zurücksetzen,
/// Demo-Daten, Bulk-Testdaten) sind zerstörerisch bzw. Entwickler-Werkzeuge und
/// nur für Admins sichtbar; der Server erzwingt das ebenfalls (Admin-Policy,
/// Reseed/Bulk zusätzlich nur in Development und sonst 403 "development_only").
/// Die beiden Entwicklungs-Werkzeuge (Reseed, Bulk-Testdaten) gibt es deshalb nur in einem Entwicklungs-Build
/// (<c>import.meta.env.DEV</c>, also `npm run dev`); ein Production-Build zeigt sie nicht, sie liefen dort nur in den 403.
/// </summary>
export function PickListsPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = usePickLists()
  const isAdmin = useAuth((s) => s.hasRole('Admin'))
  // Beim Rendern gelesen (nicht beim Laden des Moduls): so lässt sich der Build-Modus in Tests umschalten. DEV steht vorn, damit
  // der Production-Build die Bedingung zu "false" faltet und die Werkzeuge samt Texten gar nicht erst ins Bundle nimmt.
  const devTools = import.meta.env.DEV && isAdmin
  const resetMut = useResetPickLists()
  const reseedMut = useReseed()
  const bulkMut = useBulkSeed()
  const [bulkCount, setBulkCount] = useState(1000)

  const handleReseed = () => {
    if (!window.confirm(t('picking:lists.reseed.confirm'))) return
    if (!window.confirm(t('picking:lists.reseed.confirmAgain'))) return
    reseedMut.mutate()
  }

  // Nur NICHT gepackte Picklisten werden zurückgesetzt; gepackte (Completed) haben den Bestand bereits
  // gebucht, stornierte (Cancelled) sind endgültig - beide bleiben erhalten (Backend-Semantik von DELETE /api/picklists).
  const resettable = (data ?? []).filter((p) => p.status !== 'Completed' && p.status !== 'Cancelled').length

  const handleReset = () => {
    if (resettable === 0) return
    if (!window.confirm(`${t('picking:lists.reset.confirmHead', { count: resettable })}\n\n${t('picking:lists.reset.confirmBody')}`)) return
    resetMut.mutate()
  }

  const handleBulk = () => {
    if (!window.confirm(t('picking:lists.bulk.confirm', { count: bulkCount }))) return
    bulkMut.mutate(bulkCount)
  }

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('picking:lists.what')} onRetry={() => void refetch()} />

  const lists = data
  // Antwort des Resets: { deleted, skippedCompleted } — gepackte Listen bleiben stehen und werden mitgeteilt.
  const skippedCompleted = (resetMut.data as { skippedCompleted?: number } | undefined)?.skippedCompleted ?? 0

  return (
    <>
      <h2>{t('picking:lists.title')}</h2>

      {isAdmin && (
        <div className="toolbar">
          <button
            className="danger"
            onClick={handleReset}
            disabled={resettable === 0 || resetMut.isPending}
          >
            {resetMut.isPending ? t('picking:lists.reset.pending') : t('picking:lists.reset.button', { count: resettable })}
          </button>
          {devTools && (
            <button
              className="danger"
              onClick={handleReseed}
              disabled={reseedMut.isPending}
              title={t('picking:lists.devOnlyTip')}
              style={{ marginLeft: 'auto' }}
            >
              {reseedMut.isPending ? t('picking:lists.reseed.pending') : t('picking:lists.reseed.button')}
            </button>
          )}
        </div>
      )}

      {devTools && reseedMut.data && (
        <div className="success" style={{ marginBottom: 12 }}>
          {t('picking:lists.reseed.result', { message: reseedMut.data.message, ...reseedMut.data.counts })}
        </div>
      )}
      {devTools && <ErrorBanner error={reseedMut.error} title={t('picking:lists.reseed.failed')} onDismiss={reseedMut.reset} />}

      {devTools && (
        <CollapsibleCard title={t('picking:lists.bulk.title')} storageKey="picklists-bulk" defaultOpen={false}>
          <p className="muted">
            <Trans i18nKey="picking:lists.bulk.hint" components={{ strong: <strong /> }} />
          </p>
          <div className="row">
            <label style={{ flex: 1 }}>
              {t('picking:lists.bulk.count')}
              <input
                type="number"
                min={10}
                max={100000}
                value={bulkCount}
                onChange={(e) => setBulkCount(Math.max(10, Math.min(100000, +e.target.value || 0)))}
              />
            </label>
            <button
              className="danger"
              onClick={handleBulk}
              disabled={bulkMut.isPending}
            >
              {bulkMut.isPending ? t('picking:lists.bulk.pending', { count: bulkCount }) : t('picking:lists.bulk.button', { count: bulkCount })}
            </button>
          </div>
          {bulkMut.data && (
            <div className="success" style={{ marginTop: 8 }}>
              {t('picking:lists.bulk.result', { message: bulkMut.data.message, articles: bulkMut.data.articles, stockItems: bulkMut.data.stockItems, orders: bulkMut.data.orders })}
            </div>
          )}
          <ErrorBanner error={bulkMut.error} title={t('picking:error')} onDismiss={bulkMut.reset} style={{ marginTop: 8 }} />
        </CollapsibleCard>
      )}

      {resetMut.data && (
        <div className="success" style={{ marginBottom: 12 }}>
          {t('picking:lists.reset.done', { count: resetMut.data.deleted })}
          {skippedCompleted > 0 && (
            <span> {t('picking:lists.reset.skipped', { count: skippedCompleted })}</span>
          )}
        </div>
      )}
      <ErrorBanner error={resetMut.error} title={t('picking:lists.reset.failed')} onDismiss={resetMut.reset} />

      <CollapsibleCard title={t('picking:lists.cardTitle', { count: lists.length })} storageKey="picklists-list" defaultOpen>
        <table>
          <caption className="visually-hidden">{t('picking:lists.caption')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('picking:col.number')}</th>
              <th scope="col">{t('picking:col.status')}</th>
              <th scope="col">{t('picking:lists.distance')}</th>
              <th scope="col">{t('picking:col.lines')}</th>
              <th scope="col">{t('picking:lists.waypoints')}</th>
              <th scope="col">{t('picking:col.created')}</th>
              <th scope="col"><span className="visually-hidden">{t('picking:col.actions')}</span></th>
            </tr>
          </thead>
          <tbody>
            {lists.map((p) => (
              <tr key={p.id}>
                <td><code>{p.pickListNumber}</code></td>
                <td>{t(`status:pickList.${p.status}`, { defaultValue: p.status })}</td>
                <td>{(p.totalDistanceMm / 1000).toFixed(2)} m</td>
                <td>{p.items.length}</td>
                <td>{p.waypoints?.length ?? 0}</td>
                <td>{formatDateTime(p.createdAt)}</td>
                <td><Link to={`/picklists/${p.id}`}>{t('picking:open')}</Link></td>
              </tr>
            ))}
            {lists.length === 0 && (
              <tr><td colSpan={7} className="muted">{t('picking:lists.empty')}</td></tr>
            )}
          </tbody>
        </table>
      </CollapsibleCard>
    </>
  )
}
