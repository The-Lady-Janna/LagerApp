import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import {
  useInventory, useInventoryList, useReconcileInventory, useSetInventoryCount,
  useStartInventory, useStorageLocations,
} from '../api/hooks'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { formatDateTime } from '../lib/format'

/// <summary>
/// Inventur. Auf der Index-Route eine Liste aller Counts + Start-Button.
/// /inventory/:id zeigt den Detail-View mit Bin-für-Bin-Eingabe und Diff.
/// </summary>
export function InventoryPage() {
  const { id } = useParams()
  if (id) return <InventoryDetail id={id} />
  return <InventoryList />
}

function InventoryList() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useInventoryList()
  const bins = useStorageLocations()
  const startMut = useStartInventory()
  const [name, setName] = useState('')
  const [binFilter, setBinFilter] = useState<string>('')

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('inventory:what')} onRetry={() => void refetch()} />
  return (
    <>
      <h2>{t('inventory:title')}</h2>
      <div className="card" style={{ marginBottom: 16 }}>
        <h3>{t('inventory:start.title')}</h3>
        <p className="muted" style={{ fontSize: 12 }}>
          {t('inventory:start.hint')}
        </p>
        <div className="grid-2">
          <label>{t('inventory:col.name')} <input value={name} onChange={(e) => setName(e.target.value)} placeholder={t('inventory:start.namePlaceholder')} /></label>
          <label>{t('inventory:start.onlyBin')}
            <select value={binFilter} onChange={(e) => setBinFilter(e.target.value)}>
              <option value="">{t('inventory:start.allBins')}</option>
              {bins.data?.map((b) => <option key={b.id} value={b.id}>{b.code}</option>)}
            </select>
          </label>
        </div>
        <button className="primary" style={{ marginTop: 8 }} disabled={startMut.isPending || !name}
          onClick={() => startMut.mutate({ name, onlyForBinId: binFilter || null })}>
          {startMut.isPending ? t('inventory:start.starting') : t('inventory:start.submit')}
        </button>
        <ErrorBanner error={startMut.error} title={t('inventory:start.failed')} onDismiss={startMut.reset} style={{ marginTop: 8 }} />
      </div>

      <div className="table-wrap">
      <table style={{ width: '100%' }}>
        <caption className="visually-hidden">{t('inventory:list.caption')}</caption>
        <thead><tr><th scope="col">{t('inventory:col.name')}</th><th scope="col">{t('inventory:col.status')}</th><th scope="col">{t('inventory:col.lines')}</th><th scope="col">{t('inventory:col.created')}</th><th scope="col">{t('inventory:col.reconciled')}</th><th scope="col"><span className="visually-hidden">{t('inventory:col.actions')}</span></th></tr></thead>
        <tbody>
          {data.map((c) => (
            <tr key={c.id}>
              <td>{c.name}</td>
              <td>{t(`status:inventory.${c.status}`, { defaultValue: c.status })}</td>
              <td>{c.lines.length}</td>
              <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(c.createdAt)}</td>
              <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(c.reconciledAt)}</td>
              <td><Link className="button" to={`/inventory/${c.id}`}>{t('inventory:details')}</Link></td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>
    </>
  )
}

function InventoryDetail({ id }: { id: string }) {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useInventory(id)
  const setMut = useSetInventoryCount()
  const reconcileMut = useReconcileInventory()
  const [drafts, setDrafts] = useState<Record<string, { counted: string; reason: string }>>({})
  const [confirmReconcile, setConfirmReconcile] = useState(false)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('inventory:whatOne')} onRetry={() => void refetch()} />

  const open = data.status === 'Open'
  const totalDiff = data.lines.reduce((sum, l) => sum + Math.abs(l.diff), 0)
  // Der Server bucht nur, wenn alle Positionen gezählt sind ("Es sind noch nicht alle Positionen gezählt").
  const uncounted = data.lines.filter((l) => l.countedQty == null).length

  const reconcile = () => {
    reconcileMut.mutate(id)
    setConfirmReconcile(false)
  }

  return (
    <>
      <h2>{t('inventory:detail.title', { name: data.name })}</h2>
      <div className="card">
        <div className="grid-3">
          <div><strong>{t('inventory:detail.status')}</strong> {t(`status:inventory.${data.status}`, { defaultValue: data.status })}</div>
          <div><strong>{t('inventory:detail.lines')}</strong> {data.lines.length}</div>
          <div><strong>{t('inventory:detail.diffSum')}</strong> {totalDiff}</div>
        </div>
        <div className="toolbar" style={{ marginTop: 12 }}>
          <Link className="button" to="/inventory">{t('inventory:detail.back')}</Link>
          {open && (
            <button
              className="primary"
              disabled={reconcileMut.isPending || uncounted > 0}
              title={uncounted > 0 ? t('inventory:detail.uncountedTip', { count: uncounted }) : undefined}
              onClick={() => setConfirmReconcile(true)}
            >
              {reconcileMut.isPending ? t('inventory:detail.reconciling') : t('inventory:detail.reconcile')}
            </button>
          )}
          {open && uncounted > 0 && (
            <span className="muted" style={{ fontSize: 12 }}>{t('inventory:detail.uncounted', { count: uncounted })}</span>
          )}
        </div>
        <ErrorBanner error={reconcileMut.error} title={t('inventory:detail.reconcileFailed')} onDismiss={reconcileMut.reset} />
        <ErrorBanner error={setMut.error} title={t('inventory:detail.countFailed')} onDismiss={setMut.reset} />
      </div>

      <ConfirmDialog
        open={confirmReconcile}
        title={t('inventory:confirm.title')}
        confirmLabel={t('inventory:confirm.label')}
        pending={reconcileMut.isPending}
        onConfirm={reconcile}
        onCancel={() => setConfirmReconcile(false)}
      >
        <p>
          {t('inventory:confirm.body', { total: totalDiff })}
        </p>
      </ConfirmDialog>

      <div className="table-wrap">
      <table style={{ width: '100%', marginTop: 12 }}>
        <caption className="visually-hidden">{t('inventory:detail.caption')}</caption>
        <thead>
          <tr>
            <th scope="col">{t('inventory:detail.colBin')}</th><th scope="col">{t('inventory:detail.colSku')}</th><th scope="col" style={{ textAlign: 'right' }}>{t('inventory:detail.colExpected')}</th>
            <th scope="col" style={{ textAlign: 'right' }}>{t('inventory:detail.colCounted')}</th><th scope="col" style={{ textAlign: 'right' }}>{t('inventory:detail.colDiff')}</th><th scope="col">{t('inventory:detail.colReason')}</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => {
            const draft = drafts[l.id] ?? { counted: l.countedQty?.toString() ?? '', reason: l.reason ?? '' }
            return (
              <tr key={l.id} style={open && l.countedQty == null ? { background: 'var(--c-surface-alt)' } : undefined}>
                <td><code>{l.binCode}</code></td>
                <td><code>{l.articleSku}</code></td>
                <td style={{ textAlign: 'right' }}>{l.expectedQty}</td>
                <td style={{ textAlign: 'right' }}>
                  {open ? (
                    <input
                      type="number"
                      aria-label={t('inventory:detail.countedAria', { bin: l.binCode, sku: l.articleSku })}
                      placeholder={t('inventory:detail.open')}
                      value={draft.counted}
                      onChange={(e) => setDrafts((d) => ({ ...d, [l.id]: { ...draft, counted: e.target.value } }))}
                      onBlur={() => {
                        const qty = parseInt(draft.counted, 10)
                        if (!isNaN(qty)) setMut.mutate({ id, lineId: l.id, req: { countedQty: qty, reason: draft.reason || null } })
                      }}
                      style={{ width: 70, textAlign: 'right' }}
                    />
                  ) : l.countedQty ?? '—'}
                </td>
                <td className={l.diff === 0 ? 'text-success' : 'text-danger'} style={{ textAlign: 'right', fontWeight: 600 }}>
                  {l.diff > 0 ? `+${l.diff}` : l.diff}
                </td>
                <td>
                  {open ? (
                    <input value={draft.reason} aria-label={t('inventory:detail.reasonAria', { bin: l.binCode, sku: l.articleSku })}
                      onChange={(e) => setDrafts((d) => ({ ...d, [l.id]: { ...draft, reason: e.target.value } }))} />
                  ) : l.reason ?? <span className="muted">—</span>}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
      </div>
    </>
  )
}
