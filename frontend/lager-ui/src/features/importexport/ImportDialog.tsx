import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import {
  IMPORT_MAX_BYTES, IMPORT_MAX_ROWS, useImportCsv,
  type CsvDelimiter, type ImportKind, type ImportResultDto,
} from '../../api/importExportHooks'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Modal } from '../../components/Modal'
import { StatTile } from '../../components/StatTile'
import { formatNumber } from '../../lib/format'
import { applyBlockedReason, canApplyImport, errorKey, formatBytes, writableCount } from './importFormat'

interface Props {
  kind: ImportKind
  /** Vorbelegung des Trennzeichens (die Wahl der Seite). */
  delimiter: CsvDelimiter
  onClose: () => void
}

/// <summary>
/// Import-Dialog in zwei Schritten: Datei und Trennzeichen wählen, PRÜFEN (Trockenlauf: pro Zeile Fehler, dazu "x neu,
/// y aktualisiert, z Fehler"), dann ÜBERNEHMEN. Erst „Übernehmen“ schreibt. Gibt es Zeilenfehler, wird nur übernommen, wenn die
/// fehlerhaften Zeilen ausdrücklich ausgelassen werden; dann gilt der Rest. Wer Datei oder Trennzeichen nach der Prüfung ändert,
/// muss neu prüfen: übernommen wird immer genau das, was geprüft wurde.
/// Der Server prüft bei der Übernahme erneut und schreibt in EINER Transaktion: scheitert etwas, bleibt nichts zurück.
/// </summary>
export function ImportDialog({ kind, delimiter: initialDelimiter, onClose }: Props) {
  const { t } = useTranslation()
  const label = t(`importexport:import.${kind}.label`)
  const importMut = useImportCsv()

  const [file, setFile] = useState<File | null>(null)
  const [delimiter, setDelimiter] = useState<CsvDelimiter>(initialDelimiter)
  const [skipErrors, setSkipErrors] = useState(false)
  const [preview, setPreview] = useState<ImportResultDto | null>(null)
  const [done, setDone] = useState<ImportResultDto | null>(null)

  const tooBig = file !== null && file.size > IMPORT_MAX_BYTES
  const pending = importMut.isPending
  const canApply = canApplyImport(preview, skipErrors)
  const blockedReason = preview ? applyBlockedReason(preview, skipErrors) : null

  /** Jede Änderung an Datei oder Trennzeichen macht die bisherige Prüfung ungültig. */
  const resetCheck = () => {
    setPreview(null)
    setSkipErrors(false)
    importMut.reset()
  }

  const check = () => {
    if (!file || tooBig) return
    importMut.mutate({ kind, file, delimiter, dryRun: true }, { onSuccess: setPreview })
  }

  const apply = () => {
    if (!file || !canApply) return
    importMut.mutate({ kind, file, delimiter, dryRun: false, skipErrors }, { onSuccess: setDone })
  }

  return (
    <Modal title={t('importexport:dialog.title', { label })} width={760} onClose={onClose} closeOnEscape={!pending}>
      {done ? (
        <DoneView result={done} />
      ) : (
        <>
          <p className="muted" style={{ margin: '0 0 12px' }}>{t(`importexport:import.${kind}.description`)}</p>
          <p className="muted" style={{ margin: '0 0 12px' }}>
            <Trans i18nKey="importexport:dialog.requiredColumns" values={{ columns: t(`importexport:import.${kind}.required`) }} components={{ strong: <strong /> }} />
          </p>

          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(220px, 100%), 1fr))', gap: 12 }}>
            <label>
              {t('importexport:dialog.file', { size: formatBytes(IMPORT_MAX_BYTES), rows: formatNumber(IMPORT_MAX_ROWS) })}
              <input
                type="file"
                accept=".csv,.txt,text/csv"
                disabled={pending}
                data-autofocus
                onChange={(e) => {
                  setFile(e.target.files?.[0] ?? null)
                  resetCheck()
                }}
              />
            </label>
            <label>
              {t('importexport:delimiter')}
              <select
                value={delimiter}
                disabled={pending}
                onChange={(e) => {
                  setDelimiter(e.target.value as CsvDelimiter)
                  resetCheck()
                }}
              >
                <option value="semicolon">{t('importexport:dialog.delimiterSemicolon')}</option>
                <option value="comma">{t('importexport:delimiterComma')}</option>
              </select>
            </label>
          </div>

          {tooBig && file && (
            <p className="error" role="alert" style={{ marginTop: 8 }}>
              {t('importexport:dialog.tooBig', { size: formatBytes(file.size), max: formatBytes(IMPORT_MAX_BYTES) })}
            </p>
          )}

          <ErrorBanner error={importMut.error} title={t('importexport:dialog.failed')} style={{ marginTop: 12 }} />

          {preview && (
            <PreviewView
              preview={preview}
              skipErrors={skipErrors}
              onSkipErrorsChange={setSkipErrors}
              pending={pending}
            />
          )}
        </>
      )}

      <div className="toolbar modal-actions">
        {done ? (
          <button type="button" className="primary" onClick={onClose}>{t('importexport:dialog.close')}</button>
        ) : (
          <>
            {preview ? (
              <button type="button" className="primary" onClick={apply} disabled={!canApply || pending} title={blockedReason ?? undefined}>
                {pending ? t('importexport:dialog.applying') : t('importexport:dialog.apply')}
              </button>
            ) : (
              <button type="button" className="primary" onClick={check} disabled={!file || tooBig || pending}>
                {pending ? t('importexport:dialog.checking') : t('importexport:dialog.check')}
              </button>
            )}
            {preview && (
              <button type="button" onClick={check} disabled={pending}>{t('importexport:dialog.recheck')}</button>
            )}
            <button type="button" onClick={onClose} disabled={pending}>{t('common:cancel')}</button>
          </>
        )}
      </div>
    </Modal>
  )
}

/** Ergebnis des Trockenlaufs: Zahlen, Hinweise, Tabelle der Zeilenfehler und die Wahl „fehlerhafte Zeilen auslassen“. */
function PreviewView({ preview, skipErrors, onSkipErrorsChange, pending }: {
  preview: ImportResultDto
  skipErrors: boolean
  onSkipErrorsChange: (value: boolean) => void
  pending: boolean
}) {
  const { t } = useTranslation()
  const writable = writableCount(preview)
  return (
    <section aria-label={t('importexport:preview.aria')} style={{ marginTop: 16 }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(120px, 100%), 1fr))', gap: 12 }}>
        <StatTile label={t('importexport:preview.created')} value={preview.created} tone={preview.created > 0 ? 'success' : 'muted'} />
        <StatTile label={t('importexport:preview.updated')} value={preview.updated} tone={preview.updated > 0 ? 'success' : 'muted'} />
        <StatTile label={t('importexport:preview.unchanged')} value={preview.unchanged} tone="muted" />
        <StatTile label={t('importexport:preview.errors')} value={preview.errorCount} tone={preview.errorCount > 0 ? 'danger' : 'muted'} />
      </div>
      <p role="status" style={{ margin: '12px 0 0' }}>
        <strong>{preview.summary}</strong> {t('importexport:preview.rowsRead', { rows: formatNumber(preview.rows) })} {preview.message}
      </p>

      {preview.warnings.length > 0 && (
        <div className="warning" role="note" style={{ marginTop: 12 }}>
          <ul style={{ margin: 0, paddingLeft: 18 }}>
            {preview.warnings.map((warning) => <li key={warning}>{warning}</li>)}
          </ul>
        </div>
      )}

      {preview.errors.length > 0 && (
        <>
          <div className="table-wrap" style={{ marginTop: 12, maxHeight: 280, overflowY: 'auto' }}>
            <table style={{ width: '100%' }}>
              <caption className="visually-hidden">{t('importexport:preview.caption')}</caption>
              <thead>
                <tr><th scope="col">{t('importexport:preview.colLine')}</th><th scope="col">{t('importexport:preview.colKey')}</th><th scope="col">{t('importexport:preview.colError')}</th></tr>
              </thead>
              <tbody>
                {preview.errors.map((error, index) => (
                  <tr key={`${error.line}-${index}`}>
                    <td>{error.line}</td>
                    <td><code>{errorKey(error.key)}</code></td>
                    <td>{error.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {preview.errorsTruncated && (
            <p className="muted" style={{ margin: '8px 0 0' }}>
              {t('importexport:preview.truncated', { shown: preview.errors.length, total: formatNumber(preview.errorCount) })}
            </p>
          )}
          <label style={{ flexDirection: 'row', alignItems: 'center', gap: 8, marginTop: 12, color: 'inherit', fontSize: 14 }}>
            <input
              type="checkbox"
              checked={skipErrors}
              disabled={pending || writable === 0}
              onChange={(e) => onSkipErrorsChange(e.target.checked)}
            />
            {t('importexport:preview.skip', { num: formatNumber(writable) })}
          </label>
        </>
      )}
    </section>
  )
}

/** Nach der Übernahme: was geschrieben wurde. */
function DoneView({ result }: { result: ImportResultDto }) {
  const { t } = useTranslation()
  return (
    <section aria-label={t('importexport:done.aria')}>
      <p role="status" className={result.applied ? 'info' : 'warning'}>
        <strong>{result.applied ? t('importexport:done.applied') : t('importexport:done.notApplied')}</strong> {result.message}
      </p>
      {result.applied && (
        <p className="muted" style={{ margin: '8px 0 0' }}>
          {t('importexport:done.auditNote', { suffix: result.importId ? t('importexport:done.idSuffix', { id: result.importId }) : '' })}
        </p>
      )}
    </section>
  )
}
