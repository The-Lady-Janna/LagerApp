import { useMemo, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { useDownloadZpl, useLabelArticles, useLabelLayout, useLabelOrders, type LabelKind } from '../../api/labelHooks'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { ErrorBanner } from '../../components/ErrorBanner'
import { LoadState } from '../../components/LoadState'
import { formatNumber } from '../../lib/format'
import { LabelSheet } from './LabelSheet'
import { ArticlePicker, BinPicker, OrderPicker } from './LabelPickers'
import { canEncodeCode128, code128Problem, encodeCode128 } from './code128'
import {
  LABEL_FORMATS, MAX_COPIES, MAX_PRINT_LABELS, MIN_MODULE_WIDTH_MM, clampCopies, clampStartPosition, expandCopies, isBarcodeTooDense,
  labelsPerPage, moduleWidthMm, pageCount, type LabelFormatId,
} from './labelLayout'
import {
  articleLabel, binLabel, flattenBins, orderLabel, parseIds, parseKind, selectedLabels,
  type LabelData,
} from './labelSelection'

const KINDS: readonly LabelKind[] = ['bin', 'article', 'order']

/** Ab so vielen Etiketten (samt Kopien) fragt der Druck nach — ein versehentlicher Druck über viele Seiten wäre Papierverschwendung. */
const LARGE_PRINT_JOB = 200
/** Ab so vielen Etiketten fragt der ZPL-Download nach: je Etikett wird ein Endpunkt abgerufen. */
const LARGE_ZPL_JOB = 100

/// <summary>
/// Etiketten drucken: Lagerplatz-, Artikel- und Bestell-Etiketten mit Code 128 als Inline-SVG direkt aus dem Browser
/// (Einzeletikett 50 × 30 mm oder A4-Bogen 3 × 8) — ohne Zebra-Drucker — und dieselbe Auswahl als ZPL-Datei für einen Zebra-Drucker.
/// Die Auswahl geschieht einzeln oder per Filter (Lager, Gang, Regal; Sammeldruck sortiert nach Code). Die Vorschau IST die Druckvorlage:
/// beim Drucken bleibt nur sie sichtbar (labels.css). Andere Seiten können mit `/labels?type=bin|article|order&ids=<id>,<id>` auf
/// Etiketten verweisen.
/// </summary>
export function LabelsPage() {
  const { t, i18n } = useTranslation()
  // Die Zeilen unter dem Code ("Regal …", "Kunde: …") und die Begründungen der Code-128-Prüfung werden beim Bauen übersetzt:
  // die abgeleiteten Daten hängen deshalb an der Sprache (sonst bliebe nach dem Wechsel die alte Sprache stehen).
  const language = i18n.resolvedLanguage
  const [params] = useSearchParams()
  const [kind, setKind] = useState<LabelKind>(() => parseKind(params.get('type')))
  const [selection, setSelection] = useState<Record<LabelKind, ReadonlySet<string>>>(() => {
    const initial: Record<LabelKind, ReadonlySet<string>> = { bin: new Set(), article: new Set(), order: new Set() }
    initial[parseKind(params.get('type'))] = parseIds(params.get('ids'))
    return initial
  })
  const [formatId, setFormatId] = useState<LabelFormatId>('a4')
  const [copiesText, setCopiesText] = useState('1')
  const [startText, setStartText] = useState('1')
  const [confirm, setConfirm] = useState<'print' | 'zpl' | null>(null)

  const layout = useLabelLayout(kind === 'bin')
  const articles = useLabelArticles(kind === 'article')
  const orders = useLabelOrders(kind === 'order')
  const query = kind === 'bin' ? layout : kind === 'article' ? articles : orders
  const download = useDownloadZpl()

  const format = LABEL_FORMATS[formatId]
  const copies = clampCopies(Number(copiesText))
  const startPosition = clampStartPosition(Number(startText), format)

  const binRows = useMemo(() => flattenBins(layout.data ?? []), [layout.data])
  const labels = useMemo<LabelData[]>(() => {
    const all = kind === 'bin' ? binRows.map(binLabel)
      : kind === 'article' ? (articles.data ?? []).map(articleLabel)
        : (orders.data ?? []).map(orderLabel)
    return selectedLabels(all, selection[kind])
    // eslint-disable-next-line react-hooks/exhaustive-deps -- language: siehe oben
  }, [kind, binRows, articles.data, orders.data, selection, language])
  // Zu viele Etiketten (samt Kopien) für einen Druck im Browser: dann keine Vorschau und kein Druck, der ZPL-Download geht weiter.
  const totalLabels = labels.length * copies
  const tooMany = totalLabels > MAX_PRINT_LABELS
  const printLabels = useMemo(() => (tooMany ? [] : expandCopies(labels, copies)), [labels, copies, tooMany])
  const pages = pageCount(totalLabels, format, startPosition)

  // Codes, die Code 128 nicht darstellen kann, und solche, die auf diesem Format zu dicht würden.
  const problems = useMemo(() => {
    const unreadable: { code: string; reason: string }[] = []
    const tooDense: { code: string; widthMm: number }[] = []
    for (const label of labels) {
      const reason = code128Problem(label.code)
      if (reason) unreadable.push({ code: label.code, reason })
      else if (canEncodeCode128(label.code)) {
        const barcode = encodeCode128(label.code)
        if (isBarcodeTooDense(barcode, format)) tooDense.push({ code: label.code, widthMm: moduleWidthMm(barcode, format) })
      }
    }
    return { unreadable, tooDense }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- language: siehe oben
  }, [labels, format, language])

  const print = () => {
    setConfirm(null)
    window.print()
  }
  const downloadZpl = () => {
    setConfirm(null)
    download.mutate({ kind, items: labels.map((l) => ({ id: l.id, code: l.code })), copies })
  }
  const requestPrint = () => (totalLabels > LARGE_PRINT_JOB ? setConfirm('print') : print())
  const requestZpl = () => (labels.length > LARGE_ZPL_JOB ? setConfirm('zpl') : downloadZpl())

  const perPage = labelsPerPage(format)

  return (
    <div className="labels-page">
      <div className="labels-noprint">
        <h2>{t('labels:title')}</h2>
        <p className="muted">
          {t('labels:intro')}
        </p>

        <div className="card">
          <h3 style={{ marginTop: 0 }}>{t('labels:settings')}</h3>
          <div className="grid-3">
            <label>
              {t('labels:kindLabel')}
              <select value={kind} onChange={(e) => setKind(parseKind(e.target.value))}>
                {KINDS.map((k) => <option key={k} value={k}>{t(`labels:kind.${k}`)}</option>)}
              </select>
            </label>
            <label>
              {t('labels:format')}
              <select value={formatId} onChange={(e) => setFormatId(e.target.value as LabelFormatId)}>
                {Object.values(LABEL_FORMATS).map((f) => <option key={f.id} value={f.id}>{t(`labels:formats.${f.id}`, { defaultValue: f.name })}</option>)}
              </select>
            </label>
            <label>
              {t('labels:copies')}
              <input
                type="number" min={1} max={MAX_COPIES} inputMode="numeric"
                value={copiesText}
                onChange={(e) => setCopiesText(e.target.value)}
                onBlur={() => setCopiesText(String(copies))}
              />
            </label>
            {perPage > 1 && (
              <label>
                {t('labels:start', { max: perPage })}
                <input
                  type="number" min={1} max={perPage} inputMode="numeric"
                  value={startText}
                  onChange={(e) => setStartText(e.target.value)}
                  onBlur={() => setStartText(String(startPosition))}
                />
              </label>
            )}
          </div>

          <div className="toolbar" style={{ marginTop: 12, marginBottom: 8 }}>
            <button type="button" className="primary" onClick={requestPrint} disabled={printLabels.length === 0}>{t('labels:print')}</button>
            <button type="button" onClick={requestZpl} disabled={labels.length === 0 || download.isPending}>
              {download.isPending ? t('labels:zplLoading') : t('labels:zpl')}
            </button>
            <span className="muted" role="status">
              {labels.length === 0
                ? t('labels:nothing')
                : `${t('labels:count', { count: labels.length })}${copies > 1 ? t('labels:copiesSuffix', { copies, total: totalLabels }) : ''} ${t('labels:onPages', { count: pages })}`}
            </span>
          </div>
          <p className="muted" style={{ margin: 0, fontSize: 12 }}>
            <Trans i18nKey="labels:printHint" components={{ code: <code /> }} />
          </p>
        </div>

        <ErrorBanner error={download.error} title={t('labels:zplFailed')} onDismiss={() => download.reset()} />
        {download.isSuccess && (
          <p className="success" role="status">
            <Trans i18nKey="labels:zplDone" count={download.data.count} values={{ file: download.data.fileName }} components={{ code: <code /> }} />
          </p>
        )}

        <div className="card">
          <h3 style={{ marginTop: 0 }}>{t('labels:selection', { kind: t(`labels:kind.${kind}`) })}</h3>
          <LoadState isLoading={query.isLoading} error={query.error} hasData={query.data !== undefined} what={t(`labels:what.${kind}`)} onRetry={() => void query.refetch()}>
            {query.data === undefined ? undefined : kind === 'bin' ? (
              <BinPicker rows={binRows} selected={selection.bin} onChange={(next) => setSelection({ ...selection, bin: next })} />
            ) : kind === 'article' ? (
              <ArticlePicker articles={articles.data ?? []} selected={selection.article} onChange={(next) => setSelection({ ...selection, article: next })} />
            ) : (
              <OrderPicker orders={orders.data ?? []} selected={selection.order} onChange={(next) => setSelection({ ...selection, order: next })} />
            )}
          </LoadState>
        </div>

        {tooMany && (
          <div className="warning" role="alert" style={{ marginBottom: 16 }}>
            <Trans i18nKey="labels:tooMany" values={{ total: totalLabels, max: formatNumber(MAX_PRINT_LABELS) }} components={{ strong: <strong /> }} />
          </div>
        )}
        {problems.unreadable.length > 0 && (
          <div className="warning" role="alert" style={{ marginBottom: 16 }}>
            <strong>{t('labels:unreadable.title')}</strong>
            <ul style={{ margin: '4px 0 0' }}>
              {problems.unreadable.map((p) => <li key={p.code}><code>{p.code}</code> — {p.reason}</li>)}
            </ul>
            {t('labels:unreadable.note')}
          </div>
        )}
        {problems.tooDense.length > 0 && (
          <div className="warning" role="alert" style={{ marginBottom: 16 }}>
            <Trans i18nKey="labels:tooDense.text" values={{ min: formatNumber(MIN_MODULE_WIDTH_MM) }} components={{ strong: <strong /> }} />
            <ul style={{ margin: '4px 0 0' }}>
              {problems.tooDense.map((p) => <li key={p.code}><code>{p.code}</code> — {t('labels:tooDense.item', { width: formatNumber(p.widthMm, { maximumFractionDigits: 2 }) })}</li>)}
            </ul>
          </div>
        )}
      </div>

      <section className="label-preview" aria-label={t('labels:preview.aria')}>
        {printLabels.length === 0
          ? <p className="muted labels-noprint" style={{ margin: 0 }}>{tooMany ? t('labels:preview.tooMany') : t('labels:preview.empty')}</p>
          : <LabelSheet labels={printLabels} format={format} startPosition={startPosition} />}
      </section>

      <ConfirmDialog
        open={confirm === 'print'}
        title={t('labels:confirm.printTitle')}
        confirmLabel={t('labels:print')}
        onConfirm={print}
        onCancel={() => setConfirm(null)}
      >
        <p>{t('labels:confirm.printBody', { total: totalLabels, pages })}</p>
      </ConfirmDialog>
      <ConfirmDialog
        open={confirm === 'zpl'}
        title={t('labels:confirm.zplTitle')}
        confirmLabel={t('labels:zpl')}
        onConfirm={downloadZpl}
        onCancel={() => setConfirm(null)}
      >
        <p>{t('labels:confirm.zplBody', { count: labels.length })}</p>
      </ConfirmDialog>
    </div>
  )
}
