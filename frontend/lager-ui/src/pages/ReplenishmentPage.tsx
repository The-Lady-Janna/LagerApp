import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useCompleteReplenishment, useReplenishmentTasks, useScanReplenishment } from '../api/hooks'
import type { ReplenishmentTaskDto } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'

/// <summary>
/// Replenishment: Hot-Pick-Bins werden unter Schwelle aufgefüllt aus Reserve.
/// Scan-Button generiert neue Tasks idempotent. Der Complete-Dialog (ConfirmDialog) bucht physisch um:
/// er bleibt bei einem Fehler offen (Fehlertext im Dialog) und sperrt "Buchen" während die Buchung läuft.
/// </summary>
export function ReplenishmentPage() {
  const { t } = useTranslation()
  const [openOnly, setOpenOnly] = useState(true)
  const { data, isLoading, error, refetch } = useReplenishmentTasks(openOnly)
  const scan = useScanReplenishment()
  const complete = useCompleteReplenishment()
  const [completing, setCompleting] = useState<ReplenishmentTaskDto | null>(null)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('purchasing:replenishment.what')} onRetry={() => void refetch()} />

  const closeDialog = () => { setCompleting(null); complete.reset() }

  return (
    <>
      <h2>{t('purchasing:replenishment.title')}</h2>
      <div className="toolbar">
        <button className="primary" onClick={() => scan.mutate(null)} disabled={scan.isPending}>
          {scan.isPending ? t('purchasing:replenishment.scanning') : t('purchasing:replenishment.scan')}
        </button>
        <label style={{ flexDirection: 'row', marginLeft: 12 }}>
          <input type="checkbox" checked={openOnly} onChange={(e) => setOpenOnly(e.target.checked)} />
          {t('purchasing:replenishment.openOnly')}
        </label>
        {scan.data && scan.data.length > 0 && (
          <span className="muted" style={{ marginLeft: 'auto' }}>
            {t('purchasing:replenishment.created', { count: scan.data.length })}
          </span>
        )}
      </div>

      <ErrorBanner error={scan.error} title={t('purchasing:replenishment.scanFailed')} onDismiss={scan.reset} />

      {data.length === 0 && (
        <p className="muted">{t('purchasing:replenishment.none')}</p>
      )}

      <div className="table-wrap">
      <table>
        <caption className="visually-hidden">{t('purchasing:replenishment.caption')}</caption>
        <thead>
          <tr>
            <th scope="col">{t('purchasing:replenishment.col.sku')}</th><th scope="col">{t('purchasing:replenishment.col.source')}</th><th scope="col">{t('purchasing:replenishment.col.target')}</th>
            <th scope="col" style={{ textAlign: 'right' }}>{t('purchasing:replenishment.col.suggested')}</th><th scope="col">{t('purchasing:replenishment.col.status')}</th><th scope="col"><span className="visually-hidden">{t('purchasing:replenishment.col.actions')}</span></th>
          </tr>
        </thead>
        <tbody>
          {data.map((task) => (
            <tr key={task.id} style={{ opacity: task.status === 'Cancelled' ? 0.5 : 1 }}>
              <td><code>{task.articleSku}</code></td>
              <td><code>{task.sourceBinCode}</code></td>
              <td><code>{task.targetBinCode}</code></td>
              <td style={{ textAlign: 'right' }}>{task.suggestedQty}</td>
              <td>
                {task.status === 'Completed' ? (
                  <span className="text-success">✓ {task.completedQty}</span>
                ) : t(`status:replenishment.${task.status}`, { defaultValue: task.status })}
              </td>
              <td>
                {task.status === 'Open' && (
                  <button className="primary" onClick={() => setCompleting(task)}>{t('purchasing:replenishment.book')}</button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>

      {completing && (
        <CompleteDialog
          // key: jede Aufgabe beginnt mit ihrer eigenen Vorschlagsmenge.
          key={completing.id}
          task={completing}
          onCancel={closeDialog}
          onConfirm={(qty) => complete.mutate({ id: completing.id, actualQty: qty }, { onSuccess: closeDialog })}
          pending={complete.isPending}
          error={complete.error}
        />
      )}
    </>
  )
}

function CompleteDialog({ task, onCancel, onConfirm, pending, error }: {
  task: ReplenishmentTaskDto
  onCancel: () => void
  onConfirm: (qty: number) => void
  pending: boolean
  error: unknown
}) {
  const { t } = useTranslation()
  const [qty, setQty] = useState(task.suggestedQty)
  return (
    <ConfirmDialog
      open
      title={t('purchasing:replenishment.dialog.title')}
      confirmLabel={t('purchasing:replenishment.dialog.label')}
      pending={pending}
      confirmDisabled={!(qty >= 1)}
      onConfirm={() => onConfirm(qty)}
      onCancel={onCancel}
    >
      <p>
        <code>{task.articleSku}</code><br />
        <strong>{task.sourceBinCode}</strong> → <strong>{task.targetBinCode}</strong>
      </p>
      <label>{t('purchasing:replenishment.dialog.actual')}
        <input type="number" min={1} value={qty} onChange={(e) => setQty(+e.target.value)} data-autofocus />
      </label>
      <p className="muted" style={{ fontSize: 12 }}>{t('purchasing:replenishment.dialog.note')}</p>
      <ErrorBanner error={error} title={t('purchasing:replenishment.dialog.failed')} />
    </ConfirmDialog>
  )
}
