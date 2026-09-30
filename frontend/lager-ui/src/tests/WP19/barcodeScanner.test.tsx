import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { BarcodeScanner } from '../../components/BarcodeScanner'

// Safari/iOS und Firefox haben keinen BarcodeDetector, http://<PC-IP> keinen Kamerazugriff, und Kameras können ausfallen:
// in allen Fällen muss der Picker den Code von Hand eingeben können und darf nie feststecken.
describe('BarcodeScanner: Handeingabe als Fallback', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    Object.defineProperty(navigator, 'mediaDevices', { value: undefined, configurable: true })
  })

  function stubCamera(options: { getUserMedia?: () => Promise<unknown>; detector?: unknown } = {}) {
    const stop = vi.fn()
    const getUserMedia = vi.fn(options.getUserMedia ?? (() => Promise.resolve({ getTracks: () => [{ stop }] })))
    Object.defineProperty(navigator, 'mediaDevices', { value: { getUserMedia }, configurable: true })
    vi.stubGlobal('BarcodeDetector', options.detector ?? class { detect = () => Promise.resolve([]) })
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
    return { getUserMedia, stop }
  }

  it('rendert ohne BarcodeDetector einen Dialog mit Eingabefeld (Fokus im Feld), Enter meldet den Code', async () => {
    const onScan = vi.fn()
    const user = userEvent.setup()
    render(<BarcodeScanner onScan={onScan} onCancel={() => {}} />)

    expect(screen.getByRole('dialog', { name: 'Manuelle Eingabe' })).toHaveAttribute('aria-modal', 'true')
    const input = screen.getByRole('textbox', { name: 'Code' })
    expect(input).toHaveFocus()

    await user.type(input, 'BIN-A1-01{Enter}')

    expect(onScan).toHaveBeenCalledExactlyOnceWith('BIN-A1-01')
  })

  it('Escape und "Abbrechen" schließen den Scanner (Dialogverhalten)', async () => {
    const onCancel = vi.fn()
    const user = userEvent.setup()
    render(<BarcodeScanner onScan={() => {}} onCancel={onCancel} />)

    await user.keyboard('{Escape}')
    await user.click(screen.getByRole('button', { name: 'Abbrechen' }))

    expect(onCancel).toHaveBeenCalledTimes(2)
  })

  it('bietet nach einem Kamerafehler (Berechtigung verweigert) die Handeingabe samt Fehlermeldung an, statt festzustecken', async () => {
    stubCamera({ getUserMedia: () => Promise.reject(new DOMException('Permission denied', 'NotAllowedError')) })
    const onScan = vi.fn()
    const user = userEvent.setup()
    render(<BarcodeScanner onScan={onScan} onCancel={() => {}} />)

    expect(await screen.findByText(/Kamera nicht verfügbar:.*Permission denied/)).toBeInTheDocument()
    await user.type(screen.getByRole('textbox', { name: 'Code' }), 'ART-7{Enter}')

    expect(onScan).toHaveBeenCalledExactlyOnceWith('ART-7')
    expect(screen.getByRole('button', { name: 'Kamera erneut versuchen' })).toBeInTheDocument()
  })

  it('reißt bei nicht unterstützten Formaten (Konstruktor wirft) nicht die App in den ErrorBoundary, sondern fällt auf die Handeingabe zurück', async () => {
    stubCamera({ detector: class { constructor() { throw new TypeError('Unsupported format: pdf417') } } })

    render(<BarcodeScanner onScan={() => {}} onCancel={() => {}} formats={['pdf417']} />)

    expect(await screen.findByText(/Unsupported format: pdf417/)).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Code' })).toBeInTheDocument()
  })

  it('lässt sich bei laufender Kamera auf Handeingabe umschalten; die Kamera wird dabei freigegeben, "erneut versuchen" startet sie wieder', async () => {
    const { getUserMedia, stop } = stubCamera()
    const user = userEvent.setup()
    render(<BarcodeScanner onScan={() => {}} onCancel={() => {}} />)
    await waitFor(() => expect(getUserMedia).toHaveBeenCalledTimes(1))
    expect(screen.getByRole('dialog', { name: 'Barcode scannen' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Code manuell eingeben' }))

    expect(screen.getByRole('textbox', { name: 'Code' })).toBeInTheDocument()
    expect(stop).toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: 'Kamera erneut versuchen' }))
    await waitFor(() => expect(getUserMedia).toHaveBeenCalledTimes(2))
  })

  it('fragt die Erkennung gedrosselt ab (nicht bei jedem Frame) und meldet den ersten Treffer genau einmal', async () => {
    const detect = vi.fn()
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([{ rawValue: 'EAN-4006381333931' }])
    stubCamera({ detector: class { detect = detect } })
    const onScan = vi.fn()
    render(<BarcodeScanner onScan={onScan} onCancel={() => {}} />)

    await waitFor(() => expect(onScan).toHaveBeenCalledExactlyOnceWith('EAN-4006381333931'), { timeout: 2000 })

    expect(detect).toHaveBeenCalledTimes(2)
  })
})
