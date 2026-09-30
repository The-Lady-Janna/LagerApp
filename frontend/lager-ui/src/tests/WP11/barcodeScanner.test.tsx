import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { BarcodeScanner } from '../../components/BarcodeScanner'

// tests-quality#20: formats war ein Default-Parameter (pro Render ein neues Array) und onScan meist ein Inline-Callback —
// beides landete in den Effect-Dependencies, sodass die Kamera bei jedem Render der Eltern gestoppt und neu angefordert wurde.
describe('BarcodeScanner', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    Object.defineProperty(navigator, 'mediaDevices', { value: undefined, configurable: true })
  })

  function stubCamera() {
    const stop = vi.fn()
    const getUserMedia = vi.fn().mockResolvedValue({ getTracks: () => [{ stop }] })
    Object.defineProperty(navigator, 'mediaDevices', { value: { getUserMedia }, configurable: true })
    // Erkennt nie etwas: die Kamera läuft, bis die Komponente verschwindet.
    vi.stubGlobal('BarcodeDetector', class { detect = () => Promise.resolve([]) })
    // jsdom kann kein Video abspielen.
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
    return { getUserMedia, stop }
  }

  it('fordert die Kamera einmal an und startet sie bei Re-Renders der Eltern nicht neu', async () => {
    const { getUserMedia, stop } = stubCamera()
    const { rerender, unmount } = render(<BarcodeScanner onScan={() => {}} onCancel={() => {}} />)
    await waitFor(() => expect(getUserMedia).toHaveBeenCalledTimes(1))

    // Eltern rendern neu und geben wieder einen frischen Inline-Callback mit (üblicher Fall).
    rerender(<BarcodeScanner onScan={() => {}} onCancel={() => {}} />)
    rerender(<BarcodeScanner onScan={() => {}} onCancel={() => {}} />)

    expect(getUserMedia).toHaveBeenCalledTimes(1)
    expect(stop).not.toHaveBeenCalled()

    unmount() // Schließen des Scanners gibt die Kamera frei
    expect(stop).toHaveBeenCalled()
  })

  it('startet ohne Kamera-Unterstützung nichts und bietet die manuelle Eingabe an', async () => {
    const onScan = vi.fn()
    const user = userEvent.setup()
    render(<BarcodeScanner onScan={onScan} onCancel={() => {}} />)

    expect(screen.getByRole('heading', { name: 'Manuelle Eingabe' })).toBeInTheDocument()
    await user.type(screen.getByPlaceholderText('Code eingeben + Enter'), 'ART-001{Enter}')

    expect(onScan).toHaveBeenCalledExactlyOnceWith('ART-001')
  })

  it('weist ohne sicheren Kontext (kein mediaDevices, z. B. http://<PC-IP>) auf HTTPS hin', () => {
    vi.stubGlobal('BarcodeDetector', class { detect = () => Promise.resolve([]) })
    render(<BarcodeScanner onScan={() => {}} onCancel={() => {}} />)

    expect(screen.getByText(/nur über HTTPS \(oder localhost\)/)).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Code eingeben + Enter')).toBeInTheDocument()
  })
})
