import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Modal } from './Modal'

// Modul-Konstante: ein Default-Parameter `= [...]` wäre bei jedem Render ein neues Array.
const DEFAULT_FORMATS = ['code_128', 'qr_code', 'ean_13', 'ean_8']

/** Erkennungsversuche pro Sekunde ≈ 10: genug fürs Scannen, schont den Akku (statt einem detect() pro Frame). */
const DETECT_INTERVAL_MS = 100

/// <summary>
/// Camera-based barcode scanner using the browser's BarcodeDetector API.
/// Works on Chromium-based browsers (Chrome, Edge). Wo die Kamera-Erkennung fehlt
/// (Safari/iOS, Firefox, unsicherer Kontext wie http://&lt;PC-IP&gt;) oder die Kamera
/// nicht startet (Berechtigung verweigert, Format nicht unterstützt), erscheint
/// stattdessen die Handeingabe (Feld + Enter, auch für externe Scanner, die Text
/// "tippen") — der Picker sitzt nie fest. Sie lässt sich auch bei laufender Kamera
/// per Knopf öffnen.
///
/// onScan fires for the first successful detection. The caller decides
/// whether to keep the scanner open for more scans or close it.
///
/// Die Kamera startet nur, solange die Komponente gemountet ist — die aufrufende
/// Seite blendet sie erst auf Tipp des Nutzers ein — und wird NICHT bei jedem
/// Render neu angefordert: Formate sind eine Modul-Konstante und onScan läuft
/// über eine Ref, damit ein Inline-Callback der Eltern den Effekt nicht neu auslöst.
/// Der Kamera-Zugriff braucht einen sicheren Kontext (HTTPS oder localhost).
/// Als Overlay ist der Scanner ein modaler Dialog (Escape und "Abbrechen" schließen,
/// der Fokus kehrt zum auslösenden Knopf zurück).
/// </summary>
export function BarcodeScanner({
  onScan,
  onCancel,
  formats = DEFAULT_FORMATS,
}: {
  onScan: (text: string) => void
  onCancel: () => void
  formats?: string[]
}) {
  const { t } = useTranslation()
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [forceManual, setForceManual] = useState(false)
  const [manualValue, setManualValue] = useState('')
  const hasDetector = typeof window !== 'undefined' && 'BarcodeDetector' in window
  const hasCamera = typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia
  const supported = hasDetector && hasCamera
  // Die Kamera läuft nur, solange sie sinnvoll ist: unterstützt, kein Fehler, nicht auf Handeingabe umgeschaltet.
  const cameraActive = supported && error === null && !forceManual

  const onScanRef = useRef(onScan)
  useEffect(() => {
    onScanRef.current = onScan
  }, [onScan])

  // Als String, damit ein neu erzeugtes (inhaltsgleiches) Array den Effekt nicht neu startet.
  const formatsKey = formats.join('|')

  useEffect(() => {
    if (!cameraActive) return
    let stream: MediaStream | null = null
    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | undefined

    async function start() {
      try {
        // Innerhalb des try: nicht unterstützte Formate lösen sonst einen Effekt-Fehler aus, der die ganze App in den ErrorBoundary reißt.
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const Detector = (window as any).BarcodeDetector
        const detector = new Detector({ formats: formatsKey.split('|') })
        stream = await navigator.mediaDevices.getUserMedia({
          video: { facingMode: 'environment' },
          audio: false,
        })
        if (cancelled) {
          stream.getTracks().forEach((t) => t.stop())
          return
        }
        if (videoRef.current) {
          videoRef.current.srcObject = stream
          await videoRef.current.play()
        }
        void loop(detector)
      } catch (e) {
        if (!cancelled) setError(t('common:scanner.cameraError', { message: e instanceof Error && e.message ? e.message : String(e) }))
      }
    }

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    async function loop(detector: any) {
      if (cancelled || !videoRef.current) return
      try {
        const barcodes = await detector.detect(videoRef.current)
        if (barcodes.length > 0) {
          const value: string = barcodes[0].rawValue
          if (value) {
            cancelled = true
            stream?.getTracks().forEach((t) => t.stop())
            onScanRef.current(value)
            return
          }
        }
      } catch {
        // BarcodeDetector throws between frames sometimes; just keep going.
      }
      timer = setTimeout(() => void loop(detector), DETECT_INTERVAL_MS)
    }

    void start()

    return () => {
      cancelled = true
      clearTimeout(timer)
      stream?.getTracks().forEach((t) => t.stop())
    }
  }, [cameraActive, formatsKey, t])

  if (!cameraActive) {
    const reason = error
      ?? (forceManual ? t('common:scanner.reasonOff')
        : hasDetector && !hasCamera
          ? t('common:scanner.reasonInsecure')
          : t('common:scanner.reasonUnsupported'))
    const submit = () => {
      if (manualValue) onScan(manualValue)
    }
    return (
      <Modal title={t('common:scanner.manualTitle')} width={400} onClose={onCancel}>
        <p className="muted">
          {reason}{' '}
          {t('common:scanner.manualHint')}
        </p>
        <input
          data-autofocus
          aria-label={t('common:scanner.code')}
          autoComplete="off"
          value={manualValue}
          onChange={(e) => setManualValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && manualValue) {
              e.preventDefault()
              submit()
            }
          }}
          placeholder={t('common:scanner.placeholder')}
          style={{ width: '100%', fontSize: 18, padding: 12 }}
        />
        <div className="toolbar" style={{ marginTop: 12, marginBottom: 0 }}>
          <button className="primary" style={{ flex: 1, fontSize: 16, padding: 12 }} disabled={!manualValue} onClick={submit}>
            {t('common:confirm')}
          </button>
          <button style={{ fontSize: 16, padding: 12 }} onClick={onCancel}>{t('common:cancel')}</button>
        </div>
        {supported && (error !== null || forceManual) && (
          <button
            type="button"
            className="link-button"
            style={{ marginTop: 8 }}
            onClick={() => { setError(null); setForceManual(false) }}
          >
            {t('common:scanner.retryCamera')}
          </button>
        )}
      </Modal>
    )
  }

  return (
    <Modal title={t('common:scanner.title')} hideTitle placement="fullscreen" appearance="bare" onClose={onCancel}>
      <video ref={videoRef} playsInline muted className="scanner-video" aria-label={t('common:scanner.video')} />
      <p className="scanner-hint">{t('common:scanner.hint')}</p>
      <div className="toolbar" style={{ marginBottom: 0 }}>
        <button type="button" className="scanner-cancel" onClick={() => setForceManual(true)}>{t('common:scanner.manual')}</button>
        <button type="button" className="scanner-cancel" data-autofocus onClick={onCancel}>{t('common:cancel')}</button>
      </div>
    </Modal>
  )
}
