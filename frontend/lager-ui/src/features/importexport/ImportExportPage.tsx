import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { downloadExport, type CsvDelimiter, type ExportKind, type ImportKind } from '../../api/importExportHooks'
import { ErrorBanner } from '../../components/ErrorBanner'
import { describeDownloadError } from '../../lib/download'
import { ImportDialog } from './ImportDialog'
import { EXPORT_INFO, EXPORT_KINDS, IMPORT_KINDS } from './importFormat'

const GRID = { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(min(280px, 100%), 1fr))', gap: 16 } as const

/// <summary>
/// Import und Export als CSV (Manager): Artikel, Bestand und Bestellungen lassen sich ausgeben und einlesen, Bewegungen (Ledger)
/// und Audit nur ausgeben. Die Dateien sind für Excel mit deutscher Einstellung gemacht (UTF-8 mit BOM, Semikolon, Dezimalkomma);
/// Texte, die eine Tabellenkalkulation als Formel auswerten würde, sind im Export entschärft.
/// Der Import prüft zuerst (Trockenlauf) und schreibt erst nach „Übernehmen“ (Dialog, siehe ImportDialog).
/// </summary>
export function ImportExportPage() {
  const { t } = useTranslation()
  const [delimiter, setDelimiter] = useState<CsvDelimiter>('semicolon')
  const [range, setRange] = useState({ from: '', to: '' })
  const [user, setUser] = useState('')
  const [downloading, setDownloading] = useState<ExportKind | null>(null)
  const [downloaded, setDownloaded] = useState<string | null>(null)
  const [downloadError, setDownloadError] = useState<string | null>(null)
  const [importKind, setImportKind] = useState<ImportKind | null>(null)

  const download = async (kind: ExportKind) => {
    setDownloading(kind)
    setDownloadError(null)
    setDownloaded(null)
    try {
      setDownloaded(await downloadExport(kind, { delimiter, from: range.from, to: range.to, user }))
    } catch (err) {
      setDownloadError(describeDownloadError(err))
    } finally {
      setDownloading(null)
    }
  }

  const rangeInvalid = range.from !== '' && range.to !== '' && range.from > range.to

  return (
    <>
      <h2>{t('importexport:title')}</h2>

      <div className="card card--info">
        <p style={{ margin: 0 }}>
          <Trans i18nKey="importexport:intro" components={{ strong: <strong /> }} />
        </p>
        <label style={{ marginTop: 12, maxWidth: 360 }}>
          {t('importexport:delimiter')}
          <select value={delimiter} onChange={(e) => setDelimiter(e.target.value as CsvDelimiter)}>
            <option value="semicolon">{t('importexport:delimiterSemicolon')}</option>
            <option value="comma">{t('importexport:delimiterComma')}</option>
          </select>
        </label>
      </div>

      <h3>{t('importexport:heading.export')}</h3>
      <ErrorBanner error={downloadError} title={t('importexport:exportFailed')} onDismiss={() => setDownloadError(null)} />
      {downloaded && <p role="status" className="muted"><Trans i18nKey="importexport:saved" values={{ name: downloaded }} components={{ code: <code /> }} /></p>}
      <div style={GRID}>
        {EXPORT_KINDS.map((kind) => {
          const info = EXPORT_INFO[kind]
          const label = t(`importexport:export.${kind}.label`)
          return (
            <section key={kind} className="card" aria-label={t('importexport:exportAria', { label })}>
              <h4 style={{ margin: '0 0 4px' }}>{label}</h4>
              <p className="muted" style={{ margin: '0 0 12px' }}>{t(`importexport:export.${kind}.description`)}</p>
              {info.hasRange && (
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8, marginBottom: 8 }}>
                  <label>
                    {t('importexport:from')}
                    <input type="date" value={range.from} max={range.to || undefined} onChange={(e) => setRange({ ...range, from: e.target.value })} />
                  </label>
                  <label>
                    {t('importexport:to')}
                    <input type="date" value={range.to} min={range.from || undefined} onChange={(e) => setRange({ ...range, to: e.target.value })} />
                  </label>
                </div>
              )}
              {info.hasUser && (
                <label style={{ marginBottom: 8 }}>
                  {t('importexport:user')}
                  <input value={user} maxLength={128} autoComplete="off" onChange={(e) => setUser(e.target.value)} />
                </label>
              )}
              {info.hasRange && rangeInvalid && (
                <p className="error" role="alert" style={{ margin: '0 0 8px' }}>{t('importexport:rangeInvalid')}</p>
              )}
              <button
                type="button"
                className="primary"
                onClick={() => void download(kind)}
                disabled={downloading !== null || (info.hasRange === true && rangeInvalid)}
                aria-label={t('importexport:downloadAria', { label })}
              >
                {downloading === kind ? t('common:loading') : t('importexport:download')}
              </button>
            </section>
          )
        })}
      </div>

      <h3>{t('importexport:heading.import')}</h3>
      <p className="muted" style={{ marginTop: 0 }}>
        <Trans i18nKey="importexport:importHint" components={{ code: <code /> }} />
      </p>
      <div style={GRID}>
        {IMPORT_KINDS.map((kind) => {
          const label = t(`importexport:import.${kind}.label`)
          return (
            <section key={kind} className="card" aria-label={t('importexport:importAria', { label })}>
              <h4 style={{ margin: '0 0 4px' }}>{label}</h4>
              <p className="muted" style={{ margin: '0 0 8px' }}>{t(`importexport:import.${kind}.description`)}</p>
              <p style={{ margin: '0 0 12px', fontSize: 13 }}>{t('importexport:requiredColumns', { columns: t(`importexport:import.${kind}.required`) })}</p>
              <button type="button" onClick={() => setImportKind(kind)} aria-label={t('importexport:importButtonAria', { label })}>
                {t('importexport:importFile')}
              </button>
            </section>
          )
        })}
      </div>

      {importKind && (
        // key: jeder Import startet mit leerer Auswahl und leerem Ergebnis
        <ImportDialog key={importKind} kind={importKind} delimiter={delimiter} onClose={() => setImportKind(null)} />
      )}
    </>
  )
}
