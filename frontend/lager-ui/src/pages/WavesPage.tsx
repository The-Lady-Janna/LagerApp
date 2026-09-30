import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useCancelWave, useCreateWave, useOrders, useReleaseWave, useWaves } from '../api/hooks'
import type { PickWaveDto } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { StatusPill } from '../components/StatusPill'
import { formatDateTime, localDateTimeToUtcIso } from '../lib/format'

/// <summary>
/// Pick-Waves verwalten. Welle = N Orders gebündelt mit Cutoff-Zeit. Release
/// erzeugt eine konsolidierte Pickliste (Zone-Split ist Folge-Feature). Release und Abbrechen
/// fragen vorher nach (ConfirmDialog) und sind gesperrt, solange eine der beiden Aktionen läuft.
/// </summary>
export function WavesPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useWaves()
  const orders = useOrders()
  const create = useCreateWave()
  const release = useReleaseWave()
  const cancel = useCancelWave()
  const [showForm, setShowForm] = useState(false)
  const [confirm, setConfirm] = useState<{ action: 'release' | 'cancel'; wave: PickWaveDto } | null>(null)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('picking:waves.what')} onRetry={() => void refetch()} />

  const actionPending = release.isPending || cancel.isPending
  const runConfirmed = () => {
    if (confirm?.action === 'release') release.mutate({ id: confirm.wave.id, req: { splitByZone: false } })
    else if (confirm?.action === 'cancel') cancel.mutate(confirm.wave.id)
    setConfirm(null)
  }

  // Nur Orders ohne aktive Pickliste sind kandidat-fähig.
  const candidateOrders = (orders.data ?? []).filter((o) => o.status === 'New')

  return (
    <>
      <h2>{t('picking:waves.title')}</h2>
      <div className="toolbar">
        <button className="primary" onClick={() => setShowForm(!showForm)}>
          {showForm ? t('common:cancel') : t('picking:waves.new')}
        </button>
      </div>

      {showForm && (
        <CreateWaveForm
          candidates={candidateOrders}
          onCreate={(req) => create.mutate(req, { onSuccess: () => setShowForm(false) })}
          pending={create.isPending}
          error={create.error}
          onDismissError={create.reset}
        />
      )}

      <ErrorBanner
        error={release.error ?? cancel.error}
        title={t('picking:error')}
        onDismiss={() => { release.reset(); cancel.reset() }}
      />

      <div className="table-wrap">
      <table>
        <caption className="visually-hidden">{t('picking:waves.caption')}</caption>
        <thead>
          <tr>
            <th scope="col">{t('picking:col.number')}</th><th scope="col">{t('picking:waves.description')}</th><th scope="col">{t('picking:col.status')}</th><th scope="col">{t('picking:waves.cutoff')}</th>
            <th scope="col">{t('picking:waves.orders')}</th><th scope="col">{t('picking:waves.pickLists')}</th><th scope="col"><span className="visually-hidden">{t('picking:col.actions')}</span></th>
          </tr>
        </thead>
        <tbody>
          {data.map((w) => (
            <WaveRow
              key={w.id} wave={w}
              onRelease={() => setConfirm({ action: 'release', wave: w })}
              onCancel={() => setConfirm({ action: 'cancel', wave: w })}
              actionPending={actionPending}
            />
          ))}
        </tbody>
      </table>
      </div>

      <ConfirmDialog
        open={confirm !== null}
        title={confirm?.action === 'release' ? t('picking:waves.release.title') : t('picking:waves.cancel.title')}
        confirmLabel={confirm?.action === 'release' ? t('picking:waves.release.label') : t('picking:waves.cancel.label')}
        danger={confirm?.action === 'cancel'}
        cancelLabel={confirm?.action === 'cancel' ? t('picking:waves.cancel.back') : t('common:cancel')}
        onConfirm={runConfirmed}
        onCancel={() => setConfirm(null)}
      >
        {confirm?.action === 'release' ? (
          <p>
            <Trans i18nKey="picking:waves.release.body" values={{ number: confirm.wave.waveNumber, count: confirm.wave.orderIds.length }} components={{ code: <code /> }} />
          </p>
        ) : (
          <p>
            <Trans i18nKey="picking:waves.cancel.body" values={{ number: confirm?.wave.waveNumber }} components={{ code: <code /> }} />
          </p>
        )}
      </ConfirmDialog>
    </>
  )
}

function WaveRow({ wave, onRelease, onCancel, actionPending }: {
  wave: PickWaveDto
  onRelease: () => void
  onCancel: () => void
  actionPending: boolean
}) {
  const { t } = useTranslation()
  return (
    <tr style={{ opacity: wave.status === 'Cancelled' ? 0.5 : 1 }}>
      <td><code>{wave.waveNumber}</code></td>
      <td>{wave.description ?? <span className="muted">—</span>}</td>
      <td><StatusPill domain="wave" status={wave.status} /></td>
      <td className="muted" style={{ fontSize: 12 }}>
        {formatDateTime(wave.cutoffAt)}
      </td>
      <td>{wave.orderIds.length}</td>
      <td>
        {wave.pickListIds.length === 0 ? <span className="muted">—</span> : (
          wave.pickListIds.map((id) => (
            <Link key={id} to={`/picklists/${id}`} style={{ marginRight: 4, fontSize: 11 }}>{id.slice(0, 8)}…</Link>
          ))
        )}
      </td>
      <td style={{ whiteSpace: 'nowrap' }}>
        {wave.status === 'Open' && (
          <>
            <button className="primary" onClick={onRelease} disabled={actionPending || wave.orderIds.length === 0}>
              {t('picking:waves.row.release')}
            </button>
            <button onClick={onCancel} disabled={actionPending} style={{ marginLeft: 4 }}>{t('common:cancel')}</button>
          </>
        )}
      </td>
    </tr>
  )
}

function CreateWaveForm({ candidates, onCreate, pending, error, onDismissError }: {
  candidates: import('../api/types').OrderDto[]
  onCreate: (req: import('../api/types').CreatePickWaveRequest) => void
  pending: boolean
  error: unknown
  onDismissError: () => void
}) {
  const { t } = useTranslation()
  const [description, setDescription] = useState('')
  const [cutoff, setCutoff] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())

  const toggle = (id: string) => setSelected((s) => {
    const n = new Set(s)
    if (n.has(id)) n.delete(id); else n.add(id)
    return n
  })

  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <h3>{t('picking:waves.form.title')}</h3>
      <div className="grid-2">
        <label>{t('picking:waves.description')} <input value={description} onChange={(e) => setDescription(e.target.value)} placeholder={t('picking:waves.form.descriptionPlaceholder')} /></label>
        <label>{t('picking:waves.form.cutoff')} <input type="datetime-local" value={cutoff} onChange={(e) => setCutoff(e.target.value)} /></label>
      </div>
      <h4>{t('picking:waves.form.select', { selected: selected.size, total: candidates.length })}</h4>
      <div style={{ maxHeight: 240, overflow: 'auto', border: '1px solid var(--c-border-light)', borderRadius: 4, padding: 8 }}>
        {candidates.length === 0 && <p className="muted">{t('picking:waves.form.none')}</p>}
        {candidates.map((o) => (
          <label key={o.id} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: 4 }}>
            <input type="checkbox" checked={selected.has(o.id)} onChange={() => toggle(o.id)} />
            <code>{o.orderNumber}</code>
            <span className="muted" style={{ fontSize: 11 }}>{o.customerReference ? t('picking:waves.form.orderInfoRef', { count: o.lines.length, ref: o.customerReference }) : t('picking:waves.form.orderInfo', { count: o.lines.length })}</span>
          </label>
        ))}
      </div>
      <button className="primary" style={{ marginTop: 8 }} disabled={pending || selected.size === 0}
        onClick={() => onCreate({
          description: description || null,
          // datetime-local liefert Ortszeit ohne Zone; die Anzeige liest Zeitpunkte ohne Zone als UTC.
          cutoffAt: localDateTimeToUtcIso(cutoff),
          orderIds: Array.from(selected),
        })}>
        {pending ? t('common:saving') : t('picking:waves.form.create', { count: selected.size })}
      </button>
      <ErrorBanner error={error} title={t('picking:waves.form.createFailed')} onDismiss={onDismissError} style={{ marginTop: 8 }} />
    </div>
  )
}
