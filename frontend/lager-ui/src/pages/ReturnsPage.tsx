import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import {
  useArticles, useCreateReturn, useProcessReturn, useReturns, useSetReturnQc, useStorageLocations,
} from '../api/hooks'
import type { ReturnLineDto, ReturnShipmentDto } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { StatusPill } from '../components/StatusPill'
import { formatDateTime } from '../lib/format'

const QC_OPTIONS = ['Sellable', 'BGrade', 'Defect', 'Destroy'] as const

/// <summary>
/// Retouren: Liste, Anlegen, QC pro Zeile. "Verarbeiten" bucht die verkaufsfähigen Zeilen in den Bestand —
/// nach einer Rückfrage (ConfirmDialog) und gesperrt, solange die Buchung läuft.
/// </summary>
export function ReturnsPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useReturns()
  const articles = useArticles()
  const bins = useStorageLocations()
  const create = useCreateReturn()
  const setQc = useSetReturnQc()
  const process = useProcessReturn()
  const [showForm, setShowForm] = useState(false)
  const [confirmProcess, setConfirmProcess] = useState<ReturnShipmentDto | null>(null)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('returns:what')} onRetry={() => void refetch()} />

  const sellableQty = confirmProcess?.lines.filter((l) => l.qcResult === 'Sellable').reduce((sum, l) => sum + l.quantity, 0) ?? 0

  return (
    <>
      <h2>{t('returns:title')}</h2>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <button className="primary" onClick={() => setShowForm(!showForm)}>
          {showForm ? t('common:cancel') : t('returns:new')}
        </button>
      </div>

      {showForm && (
        <CreateForm
          articles={articles.data ?? []}
          onCreate={(req) => create.mutate(req, { onSuccess: () => setShowForm(false) })}
          pending={create.isPending}
        />
      )}

      {/* Fehler der Mutationen sichtbar machen (vorher verschwanden sie still). */}
      <ErrorBanner
        error={create.error ?? setQc.error ?? process.error}
        title={t('returns:error')}
        onDismiss={() => { create.reset(); setQc.reset(); process.reset() }}
      />

      <div className="table-wrap">
      <table style={{ width: '100%' }}>
        <caption className="visually-hidden">{t('returns:caption')}</caption>
        <thead><tr><th scope="col">{t('returns:col.rma')}</th><th scope="col">{t('returns:col.customer')}</th><th scope="col">{t('returns:col.status')}</th><th scope="col">{t('returns:col.lines')}</th><th scope="col">{t('returns:col.created')}</th><th scope="col">{t('returns:col.processed')}</th><th scope="col"><span className="visually-hidden">{t('returns:col.actions')}</span></th></tr></thead>
        <tbody>
          {data.map((r) => (
            <ReturnRow key={r.id} ret={r} bins={bins.data ?? []}
              onSetQc={(lineId, req) => setQc.mutate({ id: r.id, lineId, req })}
              onProcess={() => setConfirmProcess(r)}
              processPending={process.isPending} />
          ))}
        </tbody>
      </table>
      </div>

      <ConfirmDialog
        open={confirmProcess !== null}
        title={t('returns:confirm.title')}
        confirmLabel={t('returns:confirm.label')}
        onConfirm={() => { if (confirmProcess) process.mutate(confirmProcess.id); setConfirmProcess(null) }}
        onCancel={() => setConfirmProcess(null)}
      >
        <p>
          <Trans i18nKey="returns:confirm.body" values={{ rma: confirmProcess?.rmaNumber, count: sellableQty }} components={{ code: <code /> }} />
        </p>
      </ConfirmDialog>
    </>
  )
}

function ReturnRow({ ret, bins, onSetQc, onProcess, processPending }: {
  ret: ReturnShipmentDto
  bins: import('../api/types').StorageLocationDto[]
  onSetQc: (lineId: string, req: import('../api/types').SetQcRequest) => void
  onProcess: () => void
  processPending: boolean
}) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const allDone = ret.lines.every((l) => l.qcResult !== 'Pending')
  return (
    <>
      <tr style={{ opacity: ret.status === 'Cancelled' ? 0.5 : 1 }}>
        <td><code>{ret.rmaNumber}</code></td>
        <td>{ret.customerReference ?? <span className="muted">—</span>}</td>
        <td>{t(`status:return.${ret.status}`, { defaultValue: ret.status })}</td>
        <td>{ret.lines.length}</td>
        <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(ret.createdAt)}</td>
        <td className="muted" style={{ fontSize: 12 }}>{formatDateTime(ret.processedAt)}</td>
        <td>
          <button onClick={() => setOpen(!open)} aria-expanded={open}>{open ? t('returns:row.close') : t('returns:row.details')}</button>
          {ret.status === 'Draft' && allDone && (
            <button className="primary" onClick={onProcess} disabled={processPending} style={{ marginLeft: 4 }}>{t('returns:row.process')}</button>
          )}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7} style={{ background: 'var(--c-surface-alt)' }}>
            <table style={{ width: '100%' }}>
              <thead><tr><th scope="col">{t('returns:line.sku')}</th><th scope="col">{t('returns:line.quantity')}</th><th scope="col">{t('returns:line.lot')}</th><th scope="col">{t('returns:line.qc')}</th><th scope="col">{t('returns:line.targetBin')}</th><th scope="col">{t('returns:line.note')}</th></tr></thead>
              <tbody>
                {ret.lines.map((l) => (
                  <ReturnLineRow key={l.id} line={l} bins={bins} editable={ret.status === 'Draft'} onSetQc={(req) => onSetQc(l.id, req)} />
                ))}
              </tbody>
            </table>
          </td>
        </tr>
      )}
    </>
  )
}

function ReturnLineRow({ line, bins, editable, onSetQc }: {
  line: ReturnLineDto
  bins: import('../api/types').StorageLocationDto[]
  editable: boolean
  onSetQc: (req: import('../api/types').SetQcRequest) => void
}) {
  const { t } = useTranslation()
  const [bin, setBin] = useState(line.targetBinId ?? '')
  const [notes, setNotes] = useState(line.qcNotes ?? '')

  return (
    <tr>
      <td><code>{line.articleSku}</code></td>
      <td>{line.quantity}</td>
      <td>{line.lotNumber ?? <span className="muted">—</span>}</td>
      <td>
        <StatusPill domain="qc" status={line.qcResult} small />
        {editable && (
          <div style={{ display: 'flex', gap: 2, marginTop: 4 }}>
            {QC_OPTIONS.map((q) => (
              <button key={q} type="button" onClick={() => onSetQc({ result: q, targetBinId: q === 'Sellable' ? bin || null : null, notes: notes || null })}
                aria-pressed={line.qcResult === q}
                style={{
                  fontSize: 10, padding: '2px 4px',
                  background: line.qcResult === q ? 'var(--c-primary)' : 'var(--pill-neutral-bg)',
                  color: line.qcResult === q ? 'var(--c-on-primary)' : 'var(--pill-neutral-fg)',
                }}
                disabled={q === 'Sellable' && !bin}>
                {t(`status:pill.qc.${q}`)}
              </button>
            ))}
          </div>
        )}
      </td>
      <td>
        {editable && (
          <select aria-label={t('returns:line.binAria', { sku: line.articleSku })} value={bin} onChange={(e) => setBin(e.target.value)}>
            <option value="">{t('returns:line.binPlaceholder')}</option>
            {bins.map((b) => <option key={b.id} value={b.id}>{b.code}</option>)}
          </select>
        )}
        {!editable && line.targetBinId && <code style={{ fontSize: 11 }}>{bins.find((b) => b.id === line.targetBinId)?.code ?? line.targetBinId}</code>}
      </td>
      <td>{editable
        ? <input aria-label={t('returns:line.noteAria', { sku: line.articleSku })} value={notes} onChange={(e) => setNotes(e.target.value)} placeholder={t('returns:line.optionalCap')} style={{ width: '100%' }} />
        : line.qcNotes ?? <span className="muted">—</span>}
      </td>
    </tr>
  )
}

function CreateForm({ articles, onCreate, pending }: {
  articles: import('../api/types').ArticleDto[]
  onCreate: (req: import('../api/types').CreateReturnShipmentRequest) => void
  pending: boolean
}) {
  const { t } = useTranslation()
  const [ref, setRef] = useState('')
  const [notes, setNotes] = useState('')
  const [lines, setLines] = useState<{ articleId: string; quantity: number; lotNumber: string }[]>([
    { articleId: '', quantity: 1, lotNumber: '' },
  ])

  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <h3>{t('returns:form.title')}</h3>
      <div className="grid-2">
        <label>{t('returns:form.customerRef')} <input value={ref} onChange={(e) => setRef(e.target.value)} /></label>
        <label>{t('returns:form.notes')} <input value={notes} onChange={(e) => setNotes(e.target.value)} /></label>
      </div>
      <div className="table-wrap">
      <table style={{ width: '100%', marginTop: 8 }}>
        <caption className="visually-hidden">{t('returns:form.caption')}</caption>
        <thead><tr><th scope="col">{t('returns:form.article')}</th><th scope="col">{t('returns:line.quantity')}</th><th scope="col">{t('returns:line.lot')}</th><th scope="col"><span className="visually-hidden">{t('returns:col.actions')}</span></th></tr></thead>
        <tbody>
          {lines.map((l, idx) => (
            <tr key={idx}>
              <td>
                <select aria-label={t('returns:form.articleAria', { n: idx + 1 })} value={l.articleId} onChange={(e) => setLines((s) => s.map((x, i) => i === idx ? { ...x, articleId: e.target.value } : x))}>
                  <option value="">{t('returns:form.choose')}</option>
                  {articles.map((a) => <option key={a.id} value={a.id}>{a.sku} — {a.name}</option>)}
                </select>
              </td>
              <td><input type="number" min={1} aria-label={t('returns:form.quantityAria', { n: idx + 1 })} value={l.quantity} onChange={(e) => setLines((s) => s.map((x, i) => i === idx ? { ...x, quantity: +e.target.value } : x))} style={{ width: 70 }} /></td>
              <td><input aria-label={t('returns:form.lotAria', { n: idx + 1 })} value={l.lotNumber} onChange={(e) => setLines((s) => s.map((x, i) => i === idx ? { ...x, lotNumber: e.target.value } : x))} placeholder={t('returns:form.optional')} /></td>
              <td><button type="button" onClick={() => setLines((s) => s.filter((_, i) => i !== idx))} disabled={lines.length === 1} aria-label={t('returns:form.removeAria', { n: idx + 1 })}>×</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>
      <div className="toolbar" style={{ marginTop: 8 }}>
        <button onClick={() => setLines((s) => [...s, { articleId: '', quantity: 1, lotNumber: '' }])}>{t('returns:form.addLine')}</button>
        <button className="primary" disabled={pending || !lines.some((l) => l.articleId)}
          onClick={() => onCreate({
            customerReference: ref || null, notes: notes || null,
            lines: lines.filter((l) => l.articleId).map((l) => ({ articleId: l.articleId, quantity: l.quantity, lotNumber: l.lotNumber || null })),
          })}>
          {pending ? t('common:saving') : t('returns:form.create')}
        </button>
      </div>
    </div>
  )
}
