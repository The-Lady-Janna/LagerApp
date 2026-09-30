import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import {
  downloadBackup, useBackupSettings, useBackups, useCreateBackup, useDeleteBackup, useRestore,
  type BackupFileDto, type BackupSettingsDto, type RestoreResultDto,
} from '../../api/systemHooks'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { ErrorBanner } from '../../components/ErrorBanner'
import { LoadState } from '../../components/LoadState'
import { StatTile } from '../../components/StatTile'
import { StatusPill } from '../../components/StatusPill'
import { describeDownloadError } from '../../lib/download'
import { formatDateTime } from '../../lib/format'
import { describeRetention, describeSchedule, formatBytes } from './backupFormat'
import { RestoreDialog, type RestoreMode } from './RestoreDialog'

/// <summary>
/// System-Seite (Admin): Backups auflisten, jetzt erstellen, herunterladen, löschen und die Datenbank daraus (oder aus einer
/// hochgeladenen Datei) wiederherstellen. Zeitplan, Aufbewahrung und die Freigabe des Restores werden nur angezeigt: sie kommen
/// aus der Server-Konfiguration (Backup:Schedule, Backup:RetentionCount, Backup:AllowRestore).
/// Bei MySQL gibt es keine Backups über die Anwendung: die Seite zeigt statt der Liste den mysqldump-Aufruf.
/// Der Restore braucht eine Bestätigungs-Eingabe ("RESTORE") und verlangt danach einen Neustart des Servers.
/// </summary>
export function SystemPage() {
  const { t } = useTranslation()
  const settings = useBackupSettings()
  const supported = settings.data?.supported === true
  const backups = useBackups(supported)
  const createMut = useCreateBackup()
  const deleteMut = useDeleteBackup()
  const restoreMut = useRestore()

  const [deleteTarget, setDeleteTarget] = useState<BackupFileDto | null>(null)
  const [restoreMode, setRestoreMode] = useState<RestoreMode | null>(null)
  const [restoreResult, setRestoreResult] = useState<RestoreResultDto | null>(null)
  const [downloading, setDownloading] = useState<string | null>(null)
  const [downloadError, setDownloadError] = useState<string | null>(null)

  if (!settings.data) {
    return <LoadState isLoading={settings.isLoading} error={settings.error} what={t('system:what')} onRetry={() => void settings.refetch()} />
  }
  const info = settings.data

  if (!info.supported) {
    return (
      <>
        <h2>{t('system:title')}</h2>
        <UnsupportedNotice settings={info} />
      </>
    )
  }

  // Nach einem erfolgreichen Restore läuft der Server noch mit der alten Verbindung: bis zum Neustart kein zweiter Restore.
  const restoreBlocked = !info.restoreAllowed || restoreResult !== null
  const restoreBlockedReason = restoreResult !== null
    ? t('system:blocked.restart')
    : t('system:blocked.notAllowed')

  const download = async (name: string) => {
    setDownloading(name)
    setDownloadError(null)
    try {
      await downloadBackup(name)
    } catch (err) {
      setDownloadError(describeDownloadError(err))
    } finally {
      setDownloading(null)
    }
  }

  const startRestore = (mode: RestoreMode) => {
    restoreMut.reset()
    setRestoreMode(mode)
  }

  return (
    <>
      <h2>{t('system:title')}</h2>

      <div className="warning" role="note" style={{ marginBottom: 16 }}>
        <Trans i18nKey="system:warning" components={{ strong: <strong /> }} />
      </div>

      <SettingsOverview settings={info} />

      {restoreResult && (
        <div className="warning" role="status" style={{ marginBottom: 16 }}>
          <strong>{t('system:restored.title')}</strong>
          <p style={{ margin: '6px 0 0' }}>{restoreResult.message}</p>
          <p style={{ margin: '6px 0 0' }}>
            <Trans i18nKey="system:restored.safety" values={{ name: restoreResult.safetyBackup }} components={{ code: <code /> }} />
          </p>
        </div>
      )}

      <div className="toolbar">
        <button className="primary" onClick={() => createMut.mutate()} disabled={createMut.isPending}>
          {createMut.isPending ? t('system:creating') : t('system:create')}
        </button>
        <button
          onClick={() => startRestore({ kind: 'upload' })}
          disabled={restoreBlocked}
          title={restoreBlocked ? restoreBlockedReason : undefined}
        >
          {t('system:upload')}
        </button>
      </div>

      <ErrorBanner error={createMut.error} title={t('system:createFailed')} onDismiss={createMut.reset} />
      <ErrorBanner error={deleteMut.error} title={t('system:deleteFailed')} onDismiss={deleteMut.reset} />
      {downloadError && (
        <div className="error error-banner" role="alert">
          <div className="error-banner-text">
            <strong>{t('system:downloadFailed')}</strong><span>{downloadError}</span>
          </div>
          <div className="error-banner-actions">
            <button type="button" onClick={() => setDownloadError(null)} aria-label={t('errors:dismissAria')}>×</button>
          </div>
        </div>
      )}

      <LoadState isLoading={backups.isLoading} error={backups.error} hasData={backups.data !== undefined} what={t('system:backupsWhat')} onRetry={() => void backups.refetch()}>
        {backups.data && (
          backups.data.length === 0 ? (
            <p className="muted">{t('system:empty')}</p>
          ) : (
            <div className="table-wrap">
              <table style={{ width: '100%' }}>
                <caption className="visually-hidden">{t('system:caption')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('system:col.name')}</th><th scope="col">{t('system:col.kind')}</th><th scope="col">{t('system:col.size')}</th><th scope="col">{t('system:col.created')}</th><th scope="col">{t('system:col.actions')}</th>
                  </tr>
                </thead>
                <tbody>
                  {backups.data.map((file) => (
                    <tr key={file.name}>
                      <td><code>{file.name}</code></td>
                      <td>
                        <StatusPill
                          status={file.kind}
                          tone={file.kind === 'backup' ? 'info' : 'warning'}
                          label={file.kind === 'backup' ? t('system:kind.backup') : t('system:kind.beforeRestore')}
                          small
                        />
                      </td>
                      <td>{formatBytes(file.sizeBytes)}</td>
                      <td className="muted">{formatDateTime(file.createdUtc)}</td>
                      <td style={{ whiteSpace: 'nowrap' }}>
                        <button
                          onClick={() => void download(file.name)}
                          disabled={downloading !== null}
                          aria-label={t('system:row.downloadAria', { name: file.name })}
                        >
                          {downloading === file.name ? t('common:loading') : t('system:row.download')}
                        </button>
                        <button
                          onClick={() => startRestore({ kind: 'backup', name: file.name })}
                          disabled={restoreBlocked}
                          title={restoreBlocked ? restoreBlockedReason : undefined}
                          aria-label={t('system:row.restoreAria', { name: file.name })}
                          style={{ marginLeft: 4 }}
                        >
                          {t('system:row.restore')}
                        </button>
                        <button
                          onClick={() => { deleteMut.reset(); setDeleteTarget(file) }}
                          aria-label={t('system:row.deleteAria', { name: file.name })}
                          style={{ marginLeft: 4 }}
                        >
                          {t('common:delete')}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )
        )}
      </LoadState>

      <ConfirmDialog
        open={deleteTarget !== null}
        title={t('system:delete.title')}
        confirmLabel={t('common:delete')}
        danger
        pending={deleteMut.isPending}
        onConfirm={() => {
          if (deleteTarget) deleteMut.mutate(deleteTarget.name, { onSettled: () => setDeleteTarget(null) })
        }}
        onCancel={() => setDeleteTarget(null)}
      >
        <p><Trans i18nKey="system:delete.body" values={{ name: deleteTarget?.name }} components={{ strong: <strong /> }} /></p>
        {deleteTarget?.kind === 'before-restore' && (
          <p className="muted">{t('system:delete.beforeRestore')}</p>
        )}
      </ConfirmDialog>

      {restoreMode && (
        <RestoreDialog
          mode={restoreMode}
          maxUploadBytes={info.maxRestoreBytes}
          pending={restoreMut.isPending}
          error={restoreMut.error}
          onConfirm={(source) => restoreMut.mutate(source, {
            onSuccess: (result) => {
              setRestoreResult(result)
              setRestoreMode(null)
            },
          })}
          onCancel={() => { restoreMut.reset(); setRestoreMode(null) }}
        />
      )}
    </>
  )
}

/** Zeitplan, nächster Lauf, Aufbewahrung und Restore-Freigabe (nur lesen) samt Erklärungen. */
function SettingsOverview({ settings }: { settings: BackupSettingsDto }) {
  const { t } = useTranslation()
  return (
    <div className="card card--info">
      <h3 style={{ margin: '0 0 8px' }}>{t('system:settings.title')}</h3>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(160px, 100%), 1fr))', gap: 12 }}>
        <StatTile
          label={t('system:settings.schedule')}
          value={describeSchedule(settings)}
          tone={!settings.schedule ? 'muted' : settings.scheduleValid ? undefined : 'danger'}
          tooltip={t('system:settings.scheduleTip')}
        />
        <StatTile
          label={t('system:settings.nextRun')}
          value={settings.nextRunUtc ? formatDateTime(settings.nextRunUtc) : '—'}
          tone={settings.nextRunUtc ? undefined : 'muted'}
          tooltip={t('system:settings.nextRunTip')}
        />
        <StatTile label={t('system:settings.retention')} value={describeRetention(settings.retentionCount)} tooltip={t('system:settings.retentionTip')} />
        <StatTile
          label={t('system:settings.restore')}
          value={settings.restoreAllowed ? t('system:settings.allowed') : t('system:settings.blocked')}
          tone={settings.restoreAllowed ? 'success' : 'warning'}
          tooltip={t('system:settings.restoreTip')}
        />
      </div>

      {settings.schedule && !settings.scheduleValid && (
        <p className="error" role="alert" style={{ marginTop: 12 }}>
          {t('system:settings.invalidSchedule', { schedule: settings.schedule })}
        </p>
      )}
      {!settings.schedule && (
        <p className="muted" style={{ margin: '12px 0 0' }}>
          <Trans i18nKey="system:settings.noSchedule" components={{ code: <code /> }} />
        </p>
      )}
      {!settings.restoreAllowed && (
        <p className="muted" style={{ margin: '12px 0 0' }}>
          <Trans i18nKey="system:settings.restoreBlockedNote" components={{ code: <code /> }} />
        </p>
      )}
      <p className="muted" style={{ margin: '12px 0 0' }}>
        {settings.customDirectory ? t('system:settings.dirCustom') : t('system:settings.dirDefault')}
        {' '}{t('system:settings.readOnly')}
      </p>
    </div>
  )
}

/** Statt der Backup-Verwaltung: bei MySQL der mysqldump-Aufruf, bei einer In-Memory-Datenbank die Erklärung. */
function UnsupportedNotice({ settings }: { settings: BackupSettingsDto }) {
  const { t } = useTranslation()
  if (settings.unsupportedReason === 'mysql') {
    return (
      <div className="card card--info">
        <h3 style={{ margin: '0 0 8px' }}>{t('system:mysql.title')}</h3>
        <p>
          <Trans i18nKey="system:mysql.intro" components={{ code: <code /> }} />
        </p>
        <p className="muted" style={{ marginBottom: 4 }}>{t('system:mysql.backup')}</p>
        <pre style={{ overflowX: 'auto', margin: 0 }}><code>{settings.mysqlDumpCommand}</code></pre>
        <p className="muted" style={{ margin: '12px 0 4px' }}>{t('system:mysql.restore')}</p>
        <pre style={{ overflowX: 'auto', margin: 0 }}><code>{settings.mysqlRestoreCommand}</code></pre>
        <p className="muted" style={{ marginTop: 12 }}>
          {t('system:mysql.note')}
        </p>
      </div>
    )
  }
  return (
    <div className="warning" role="note">
      {t('system:memory')}
    </div>
  )
}
