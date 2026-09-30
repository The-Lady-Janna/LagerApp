import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router-dom'
import { useMarkPicked, usePickList } from '../api/hooks'
import { BarcodeScanner } from '../components/BarcodeScanner'
import { getErrorMessage } from '../api/errors'
import { describeApiError } from '../lib/apiError'
import { dismissToastsForError } from '../state/toasts'
import { findDeviations, nextScanCount, pickCountsFor, type PickedHandoverState } from '../lib/pickCounts'

/** So lange bleibt eine Statusmeldung (Scan, Bin bestätigt …) sichtbar. */
const STATUS_MESSAGE_MS = 4000

/** Ein Scan im Verlauf des aktuellen Stops — Grundlage für "Rückgängig". */
interface ScanEntry {
  itemId: string
  sku: string
  /** Zählstand vor dem Scan (undefined = noch nicht gezählt, gilt als Soll). */
  previous: number | undefined
  /** Zählstand direkt nach dem Scan. */
  result: number
}

/// <summary>
/// Fullscreen mobile picker UI. Steps one bin at a time through the pick list:
///   1. Show bin code → user scans bin barcode to confirm they're at the right
///      shelf. Picker can override manually if scanning fails.
///   2. For each line in the bin: confirm picked quantity (default = planned,
///      d. h. eine nicht angefasste Position gilt als wie geplant gepickt).
///      Der Artikel-Scan zählt +1 — ab 0, sobald gescannt wird — und lässt
///      sich per "Rückgängig" zurücknehmen. Die Menge bleibt zwischen 0 und Soll
///      (der Server lehnt beim Packen alles außerhalb von 0..Soll ab).
///   3. Tap "weiter" → next bin.
///   4. After last bin: "Picking abschließen" calls /mark-picked.
///
/// Der Server kennt beim Picken keine Mengen (mark-picked hat keinen Body) und
/// bucht erst im Pack-Schritt. Die gezählten Mengen werden deshalb beim
/// Abschluss als Router-State an die Pack-Seite übergeben, die sie als Ist-Menge
/// vorbelegt (Abweichungen zum Soll sind dort sichtbar). Gebucht wird erst, wenn
/// der Packer dort bestätigt — diese Seite bucht bewusst nichts und überspringt
/// die Prüfung durch den Packer nicht. Ein Fehler beim Abschluss wird angezeigt.
/// </summary>
export function MobilePickerPage() {
  const { t } = useTranslation()
  const { id } = useParams()
  const navigate = useNavigate()
  const { data: pl, isLoading, error } = usePickList(id)
  const markPicked = useMarkPicked()

  const [stepIndex, setStepIndex] = useState(0)
  const [counts, setCounts] = useState<Record<string, number>>({})
  const [scannerOpen, setScannerOpen] = useState<'bin' | 'article' | null>(null)
  const [confirmedBins, setConfirmedBins] = useState<Set<string>>(new Set())
  const [statusMsg, setStatusMsg] = useState<string | null>(null)
  const [scanLog, setScanLog] = useState<ScanEntry[]>([])
  const [finishError, setFinishError] = useState<string | null>(null)

  // Statusmeldungen blenden sich selbst aus (vorher hing das an einer nie
  // ausgelösten CSS-Animation und blieb bis zur nächsten Meldung stehen).
  useEffect(() => {
    if (!statusMsg) return
    const timer = setTimeout(() => setStatusMsg(null), STATUS_MESSAGE_MS)
    return () => clearTimeout(timer)
  }, [statusMsg])

  // Ein Ladefehler steht als Vollbild-Meldung da — der globale Toast dazu wäre doppelt.
  useEffect(() => {
    if (error) dismissToastsForError(error)
  }, [error])

  // Group items by bin in pick sequence order. The first item's sequence number
  // determines the bin's position in the route.
  const stops = useMemo(() => {
    if (!pl) return []
    const map = new Map<string, { binId: string; binCode: string; firstSeq: number; items: typeof pl.items }>()
    for (const item of pl.items) {
      const bucket = map.get(item.storageLocationId)
      if (bucket) bucket.items.push(item)
      else map.set(item.storageLocationId, {
        binId: item.storageLocationId,
        binCode: item.storageLocationCode,
        firstSeq: item.sequenceNumber,
        items: [item],
      })
    }
    return Array.from(map.values()).sort((a, b) => a.firstSeq - b.firstSeq)
  }, [pl])

  if (isLoading) return <FullscreenMessage>{t('picking:mobile.loading')}</FullscreenMessage>
  if (error) return <FullscreenMessage error>{getErrorMessage(error)}</FullscreenMessage>
  if (!pl) return null

  const stop = stops[stepIndex]
  const isLastStop = stepIndex === stops.length - 1
  const binConfirmed = stop ? confirmedBins.has(stop.binId) : false

  // Die gezählte Menge liegt zwischen 0 und Soll: der Server lehnt beim Packen alles außerhalb von 0..Soll ab
  // (PackAsync), und die Zählung wird als Ist-Menge an die Pack-Seite übergeben.
  const setCount = (item: { id: string; quantity: number }, qty: number) =>
    setCounts((c) => ({ ...c, [item.id]: Math.min(item.quantity, Math.max(0, qty)) }))

  // Scan = +1. Gezählt wird ab 0 (nextScanCount): wer scannt, zählt nach und
  // übernimmt nicht blind das Soll. Jeder Scan landet im Verlauf des Stops.
  const registerScan = (sku: string) => {
    if (!stop) return
    // Dieselbe SKU kann in einem Bin für mehrere Bestellungen vorkommen: der Scan zählt die erste Position
    // dieser SKU, deren Soll-Menge noch nicht erreicht ist.
    const matches = stop.items.filter((i) => i.articleSku === sku)
    if (matches.length === 0) {
      setStatusMsg(t('picking:mobile.skuNotInBin', { sku }))
      return
    }
    const item = matches.find((i) => nextScanCount(counts[i.id]) <= i.quantity)
    if (!item) {
      setStatusMsg(t('picking:mobile.targetReached', { sku }))
      return
    }
    const previous = counts[item.id]
    const result = nextScanCount(previous)
    setCount(item, result)
    setScanLog((log) => [...log, { itemId: item.id, sku, previous, result }])
    setStatusMsg(t('picking:mobile.scanned', { sku, result, total: item.quantity }))
  }

  // Nimmt den letzten Scan zurück. Wurde die Menge danach von Hand geändert,
  // wird nur um 1 verringert, statt den alten Stand wiederherzustellen.
  const lastScan = scanLog[scanLog.length - 1]
  const undoLastScan = () => {
    if (!lastScan) return
    setCounts((c) => {
      const next = { ...c }
      if (c[lastScan.itemId] === lastScan.result) {
        if (lastScan.previous === undefined) delete next[lastScan.itemId]
        else next[lastScan.itemId] = lastScan.previous
      } else {
        next[lastScan.itemId] = Math.max(0, (c[lastScan.itemId] ?? 0) - 1)
      }
      return next
    })
    setScanLog((log) => log.slice(0, -1))
    setStatusMsg(t('picking:mobile.undone', { sku: lastScan.sku }))
  }

  const confirmBinScan = (scanned: string) => {
    if (!stop) return
    if (scanned === stop.binCode) {
      setConfirmedBins((s) => new Set(s).add(stop.binId))
      setStatusMsg(t('picking:mobile.binConfirmedMsg', { bin: scanned }))
    } else {
      setStatusMsg(t('picking:mobile.wrongBin', { scanned, expected: stop.binCode }))
    }
    setScannerOpen(null)
  }

  const goToStop = (index: number) => {
    setStepIndex(index)
    setStatusMsg(null)
    setScanLog([]) // "Rückgängig" gilt nur für den aktuellen Stop
  }

  const next = () => {
    if (stepIndex < stops.length - 1) goToStop(stepIndex + 1)
  }

  const deviations = findDeviations(pl.items, counts)

  const finishPicking = async () => {
    if (!id || markPicked.isPending) return
    setFinishError(null)
    try {
      await markPicked.mutateAsync(id)
    } catch (err) {
      // Bei Funkloch/Serverfehler bleibt die Seite mit allen Zählständen stehen;
      // erneutes Tippen wiederholt den Abschluss.
      // Der Fehler steht unten im Alert — der globale Toast dazu wäre doppelt.
      dismissToastsForError(err)
      setFinishError(describeApiError(err, t('picking:mobile.finishFailed')))
      return
    }
    const state: PickedHandoverState = { pickedQuantities: pickCountsFor(pl.items, counts) }
    navigate(`/picklists/${id}/pack`, { state })
  }

  if (stops.length === 0) {
    return <FullscreenMessage>{t('picking:mobile.empty')}</FullscreenMessage>
  }

  return (
    <div
      className="screen-min"
      style={{
        background: 'var(--pk-bg)',
        color: 'var(--pk-text)',
        display: 'flex',
        flexDirection: 'column',
      }}
    >
      {/* Top bar */}
      <header
        style={{
          background: 'var(--pk-surface)',
          padding: '12px 16px',
          display: 'flex',
          alignItems: 'center',
          gap: 12,
        }}
      >
        <button
          onClick={() => navigate(`/picklists/${id}`)}
          style={{ background: 'transparent', color: 'var(--pk-soft)', border: 0, fontSize: 22, padding: 0 }}
          aria-label={t('picking:mobile.back')}
        >
          ←
        </button>
        <div style={{ flex: 1 }}>
          <div style={{ fontSize: 13, color: 'var(--pk-muted)' }}>{t('picking:mobile.pickList')}</div>
          <div style={{ fontWeight: 600 }}>{pl.pickListNumber}</div>
        </div>
        <div style={{ fontSize: 14, color: 'var(--pk-soft)' }}>
          {t('picking:mobile.stop', { n: stepIndex + 1, total: stops.length })}
        </div>
      </header>

      {/* Bin header */}
      <div
        style={{
          background: binConfirmed ? 'var(--pk-ok)' : 'var(--pk-bin)',
          color: 'white',
          padding: '24px 16px',
          textAlign: 'center',
        }}
      >
        <div style={{ fontSize: 13, opacity: 0.85 }}>{binConfirmed ? t('picking:mobile.binConfirmed') : t('picking:mobile.goToBin')}</div>
        <div style={{ fontSize: 36, fontWeight: 700, letterSpacing: 1, marginTop: 4 }}>
          {stop.binCode}
        </div>
        {!binConfirmed && (
          <button
            onClick={() => setScannerOpen('bin')}
            style={{
              marginTop: 12,
              background: 'white',
              color: 'var(--pk-bin)',
              border: 0,
              padding: '10px 20px',
              fontSize: 16,
              fontWeight: 600,
              borderRadius: 8,
            }}
          >
            {t('picking:mobile.scanBin')}
          </button>
        )}
      </div>

      {/* Item list */}
      <main style={{ flex: 1, padding: 16, overflow: 'auto' }}>
        {stop.items.map((item) => {
          const planned = item.quantity
          const counted = counts[item.id]
          const actual = counted ?? planned
          const match = actual === planned
          return (
            <div
              key={item.id}
              style={{
                background: 'var(--pk-surface)',
                borderRadius: 12,
                padding: 16,
                marginBottom: 12,
                borderLeft: `4px solid ${match ? 'var(--pk-ok)' : 'var(--pk-warn)'}`,
              }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12 }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 13, color: 'var(--pk-muted)' }}>
                    <code>{item.articleSku}</code>
                  </div>
                  <div style={{ fontSize: 16, fontWeight: 500, marginTop: 2 }}>{item.articleName}</div>
                  <div style={{ fontSize: 12, color: 'var(--pk-muted)', marginTop: 4 }}>
                    {t('picking:mobile.order')} <code>{item.orderNumber}</code>
                  </div>
                </div>
                <div style={{ textAlign: 'right' }}>
                  <div style={{ fontSize: 11, color: 'var(--pk-muted)' }}>{t('picking:mobile.planned')}</div>
                  <div style={{ fontSize: 28, fontWeight: 700, lineHeight: 1 }}>{planned}</div>
                </div>
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12 }}>
                <button
                  onClick={() => setCount(item, actual - 1)}
                  style={qtyButtonStyle}
                  disabled={actual === 0}
                  aria-label={t('picking:mobile.decrease', { sku: item.articleSku })}
                >
                  −
                </button>
                <input
                  type="number"
                  inputMode="numeric"
                  value={actual}
                  min={0}
                  max={planned}
                  onChange={(e) => setCount(item, +e.target.value || 0)}
                  aria-label={t('picking:mobile.countedAria', { sku: item.articleSku })}
                  style={{
                    flex: 1,
                    fontSize: 28,
                    fontWeight: 700,
                    textAlign: 'center',
                    background: 'var(--pk-bg)',
                    color: 'white',
                    border: '1px solid var(--pk-border)',
                    borderRadius: 8,
                    padding: '8px 0',
                  }}
                />
                <button
                  onClick={() => setCount(item, actual + 1)}
                  style={qtyButtonStyle}
                  disabled={actual >= planned}
                  aria-label={t('picking:mobile.increase', { sku: item.articleSku })}
                >
                  +
                </button>
              </div>
              <div style={{ fontSize: 12, marginTop: 8, color: counted === undefined ? 'var(--pk-muted)' : match ? 'var(--pk-ok-text)' : 'var(--pk-warn-text)' }}>
                {counted === undefined ? t('picking:mobile.notCounted') : t('picking:mobile.counted', { counted, planned })}
              </div>
            </div>
          )
        })}

        <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
          <button
            onClick={() => setScannerOpen('article')}
            style={{
              flex: 1,
              background: 'var(--pk-scan)',
              color: 'white',
              border: 0,
              padding: 14,
              fontSize: 16,
              fontWeight: 600,
              borderRadius: 8,
            }}
          >
            {t('picking:mobile.scanArticle')}
          </button>
          {lastScan && (
            <button
              onClick={undoLastScan}
              style={{ background: 'var(--pk-surface-strong)', color: 'white', border: 0, padding: 14, fontSize: 14, fontWeight: 600, borderRadius: 8 }}
            >
              {t('picking:mobile.undo', { sku: lastScan.sku })}
            </button>
          )}
        </div>

        {isLastStop && deviations.length > 0 && (
          <div
            style={{ marginTop: 16, padding: 12, borderRadius: 8, background: 'var(--pk-warn-bg)', color: 'var(--pk-warn-fg)', fontSize: 13 }}
          >
            {t('picking:mobile.deviations', { count: deviations.length, list: deviations.map((d) => `${d.item.articleSku} ${d.actual}/${d.item.quantity}`).join(', ') })}
          </div>
        )}
      </main>

      {statusMsg && (
        <div
          role="status"
          style={{
            background: 'var(--pk-bg)',
            color: 'var(--pk-soft)',
            padding: '10px 16px',
            textAlign: 'center',
            fontSize: 14,
            borderTop: '1px solid var(--pk-surface)',
          }}
        >
          {statusMsg}
        </div>
      )}

      {finishError && (
        <div
          role="alert"
          style={{
            background: 'var(--pk-error-bg)',
            color: 'var(--pk-error-fg)',
            padding: '10px 16px',
            textAlign: 'center',
            fontSize: 14,
            borderTop: '1px solid var(--pk-error-border)',
          }}
        >
          <div>{finishError}</div>
          <div style={{ fontSize: 12, opacity: 0.85 }}>{t('picking:mobile.retryHint')}</div>
        </div>
      )}

      {/* Bottom nav */}
      <footer style={{ background: 'var(--pk-surface)', padding: 12, display: 'flex', gap: 8 }}>
        <button
          onClick={() => goToStop(Math.max(0, stepIndex - 1))}
          disabled={stepIndex === 0}
          style={navButtonStyle}
        >
          {t('picking:mobile.prev')}
        </button>
        {!isLastStop ? (
          <button onClick={next} style={{ ...navButtonStyle, background: 'var(--pk-primary)', color: 'white', flex: 2 }}>
            {t('picking:mobile.next')}
          </button>
        ) : (
          <button
            onClick={finishPicking}
            disabled={markPicked.isPending}
            style={{ ...navButtonStyle, background: 'var(--pk-ok)', color: 'white', flex: 2 }}
          >
            {markPicked.isPending ? t('common:saving') : t('picking:mobile.finish')}
          </button>
        )}
      </footer>

      {scannerOpen === 'bin' && (
        <BarcodeScanner onScan={confirmBinScan} onCancel={() => setScannerOpen(null)} />
      )}
      {scannerOpen === 'article' && (
        <BarcodeScanner
          onScan={(v) => {
            registerScan(v)
            setScannerOpen(null)
          }}
          onCancel={() => setScannerOpen(null)}
        />
      )}
    </div>
  )
}

const qtyButtonStyle: React.CSSProperties = {
  width: 48,
  height: 48,
  fontSize: 28,
  fontWeight: 700,
  background: 'var(--pk-border)',
  color: 'white',
  border: 0,
  borderRadius: 8,
}

const navButtonStyle: React.CSSProperties = {
  flex: 1,
  padding: '14px 16px',
  fontSize: 16,
  fontWeight: 600,
  background: 'var(--pk-surface-strong)',
  color: 'white',
  border: 0,
  borderRadius: 8,
}

function FullscreenMessage({ children, error }: { children: React.ReactNode; error?: boolean }) {
  return (
    <div
      className="screen-min"
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'var(--pk-bg)',
        color: error ? 'var(--pk-error-text)' : 'var(--pk-soft)',
        padding: 24,
        textAlign: 'center',
      }}
    >
      {children}
    </div>
  )
}
