import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { RESTORE_CONFIRMATION, type RestoreSource } from '../../api/systemHooks'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { ErrorBanner } from '../../components/ErrorBanner'
import { formatBytes, isRestoreConfirmed } from './backupFormat'

/** Woher der Restore kommt: ein Backup aus der Liste oder eine Datei, die im Dialog gewählt wird. */
export type RestoreMode = { kind: 'backup'; name: string } | { kind: 'upload' }

interface Props {
  mode: RestoreMode
  /** Obergrenze für einen Upload in Byte (vom Server: maxRestoreBytes). */
  maxUploadBytes: number
  /** Der Restore läuft: alles ist gesperrt. */
  pending: boolean
  /** Fehler des letzten Versuchs (der Dialog bleibt dann offen, man kann erneut bestätigen). */
  error: unknown
  onConfirm: (source: RestoreSource) => void
  onCancel: () => void
}

/// <summary>
/// Bestätigungsdialog für den Restore: deutlicher Warnhinweis (die laufende Datenbank wird ersetzt, der Server muss danach
/// neu gestartet werden) und eine Bestätigungs-Eingabe. Erst wenn genau "RESTORE" eingetippt ist (und beim Upload eine Datei
/// gewählt wurde), lässt sich der Restore auslösen. Der Server prüft die Bestätigung ebenfalls.
/// Der Dialog wird nur eingeblendet, solange er offen ist: seine Eingaben beginnen so bei jedem Öffnen leer.
/// </summary>
export function RestoreDialog({ mode, maxUploadBytes, pending, error, onConfirm, onCancel }: Props) {
  const { t } = useTranslation()
  const [phrase, setPhrase] = useState('')
  const [file, setFile] = useState<File | null>(null)

  const tooBig = file !== null && file.size > maxUploadBytes
  const sourceReady = mode.kind === 'backup' || (file !== null && !tooBig)
  const confirmed = isRestoreConfirmed(phrase)

  const confirm = () => {
    if (mode.kind === 'backup') onConfirm({ kind: 'backup', name: mode.name })
    else if (file) onConfirm({ kind: 'upload', file })
  }

  return (
    <ConfirmDialog
      open
      danger
      title={t('system:restore.title')}
      confirmLabel={t('system:restore.label')}
      pending={pending}
      confirmDisabled={!confirmed || !sourceReady}
      onConfirm={confirm}
      onCancel={onCancel}
    >
      <div className="warning" role="note">
        <strong>{t('system:restore.warnTitle')}</strong>
        <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
          <li>{t('system:restore.warn1')}</li>
          <li>{t('system:restore.warn2')}</li>
          <li>{t('system:restore.warn3')}</li>
        </ul>
      </div>

      {mode.kind === 'backup' ? (
        <p style={{ marginTop: 12 }}><Trans i18nKey="system:restore.backup" values={{ name: mode.name }} components={{ strong: <strong /> }} /></p>
      ) : (
        <label style={{ marginTop: 12 }}>
          {t('system:restore.file', { max: formatBytes(maxUploadBytes) })}
          <input
            type="file"
            accept=".db,.sqlite,.sqlite3,application/octet-stream"
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            disabled={pending}
            data-autofocus
          />
        </label>
      )}
      {tooBig && file && (
        <p className="error" role="alert" style={{ marginTop: 8 }}>
          {t('system:restore.tooBig', { size: formatBytes(file.size), max: formatBytes(maxUploadBytes) })}
        </p>
      )}

      <label style={{ marginTop: 12 }}>
        <Trans i18nKey="system:restore.confirmPhrase" values={{ phrase: RESTORE_CONFIRMATION }} components={{ strong: <strong /> }} />
        <input
          value={phrase}
          onChange={(e) => setPhrase(e.target.value)}
          autoComplete="off"
          spellCheck={false}
          disabled={pending}
          data-autofocus={mode.kind === 'backup' ? true : undefined}
        />
      </label>

      <ErrorBanner error={error} title={t('system:restore.failed')} style={{ marginTop: 8 }} />
    </ConfirmDialog>
  )
}
