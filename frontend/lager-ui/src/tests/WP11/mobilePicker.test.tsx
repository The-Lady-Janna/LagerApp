import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { MobilePickerPage } from '../../pages/MobilePickerPage'
import { PackPickListPage } from '../../pages/PackPickListPage'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makePickItem, makePickList } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'

// Regression (stock-integrity#6 / business-workflows#7 / frontend#3): Der Mobile-Picker verwarf die gezählten Mengen,
// die Pack-Seite buchte anschließend standardmäßig die Soll-Menge. Jetzt landen die Zählstände als Vorbelegung im
// Pack-Request; Fehler beim Abschluss sind sichtbar; der Scan zählt nachvollziehbar (+1, Zähler, Rückgängig).
describe('MobilePickerPage', () => {
  let api: MockApi
  const pickList = makePickList({
    items: [
      makePickItem({ id: 'i1', articleSku: 'ART-001', quantity: 5 }),
      makePickItem({ id: 'i2', articleSku: 'ART-002', articleName: 'Mutter M8', quantity: 2, sequenceNumber: 2 }),
    ],
  })

  beforeEach(() => {
    api = installMockApi({
      'GET /picklists/pl1': pickList,
      'POST /picklists/pl1/mark-picked': { ...pickList, status: 'Picked' },
      'POST /picklists/pl1/pack': { ...pickList, status: 'Completed' },
    })
  })
  afterEach(() => api.restore())

  const renderFlow = () =>
    renderWithProviders(
      <Routes>
        <Route path="/picker/:id" element={<MobilePickerPage />} />
        <Route path="/picklists/:id/pack" element={<PackPickListPage />} />
        <Route path="/picklists/:id" element={<p>Pickliste-Detail</p>} />
      </Routes>,
      { initialEntries: ['/picker/pl1'] },
    )

  const countInput = (sku: string) => screen.getByLabelText(`${sku} gepickte Menge`)

  it('sendet die gezählte Menge (nicht die Soll-Menge) im Pack-Request', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    renderFlow()

    // Am Regal nur 3 von 5 gefunden; die zweite Position bleibt unangetastet (= wie geplant gepickt).
    await screen.findByText('A-01-01')
    fireEvent.change(countInput('ART-001'), { target: { value: '3' } })
    await user.click(screen.getByRole('button', { name: /Picking abschließen/ }))

    // Picking wird abgeschlossen und die Pack-Seite zeigt die gezählte Menge als Ist-Menge.
    await waitFor(() => expect(api.calls('POST', '/picklists/pl1/mark-picked')).toHaveLength(1))
    expect(await screen.findByText(/Vom Mobile-Picker übernommen/)).toHaveTextContent('1 mit Abweichung vom Soll (ART-001 3/5)')
    const istFelder = screen.getAllByRole('spinbutton')
    expect(istFelder.map((f) => (f as HTMLInputElement).value)).toEqual(['3', '2'])

    // Erst die Bestätigung des Packers bucht — und zwar mit 3, nicht mit 5.
    expect(api.calls('POST', '/picklists/pl1/pack')).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Packen abschließen' }))
    await waitFor(() => expect(api.calls('POST', '/picklists/pl1/pack')).toHaveLength(1))
    expect(api.calls('POST', '/picklists/pl1/pack')[0].body).toEqual({
      items: [
        { pickItemId: 'i1', actualQuantity: 3 },
        { pickItemId: 'i2', actualQuantity: 2 },
      ],
    })
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('weichen vom Soll ab'))
  })

  it('bucht ohne Zählung wie bisher die Soll-Menge (nicht angefasste Positionen gelten als bestätigt)', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    renderFlow()

    await screen.findByText('A-01-01')
    await user.click(screen.getByRole('button', { name: /Picking abschließen/ }))
    await user.click(await screen.findByRole('button', { name: 'Packen abschließen' }))

    await waitFor(() => expect(api.calls('POST', '/picklists/pl1/pack')).toHaveLength(1))
    expect(api.calls('POST', '/picklists/pl1/pack')[0].body).toEqual({
      items: [
        { pickItemId: 'i1', actualQuantity: 5 },
        { pickItemId: 'i2', actualQuantity: 2 },
      ],
    })
    expect(window.confirm).not.toHaveBeenCalled() // keine Abweichung → keine Rückfrage
  })

  it('zeigt einen Fehler beim Abschluss an, behält die Zählstände und wiederholt beim nächsten Tippen', async () => {
    api.setRoute('POST /picklists/pl1/mark-picked', fail(500, { error: 'Datenbank gesperrt' }))
    const user = userEvent.setup()
    renderFlow()

    await screen.findByText('A-01-01')
    fireEvent.change(countInput('ART-001'), { target: { value: '4' } })
    await user.click(screen.getByRole('button', { name: /Picking abschließen/ }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Datenbank gesperrt')
    expect(screen.queryByText('Pickliste-Detail')).not.toBeInTheDocument()
    expect(countInput('ART-001')).toHaveValue(4) // nichts ist verloren

    // Server ist wieder da → erneut tippen schließt ab und geht zur Pack-Seite.
    api.setRoute('POST /picklists/pl1/mark-picked', { ...pickList, status: 'Picked' })
    await user.click(screen.getByRole('button', { name: /Picking abschließen/ }))
    expect(await screen.findByText(/Vom Mobile-Picker übernommen/)).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('warnt vor dem Abschluss, welche Positionen vom Soll abweichen', async () => {
    renderFlow()
    await screen.findByText('A-01-01')

    fireEvent.change(countInput('ART-001'), { target: { value: '3' } })

    expect(screen.getByText(/1 Position weichen vom Soll ab/)).toHaveTextContent('ART-001 3/5')
  })

  describe('Scan (+1)', () => {
    // Im Testumfeld gibt es keinen BarcodeDetector → der Scanner zeigt die manuelle Eingabe.
    const scan = async (user: ReturnType<typeof userEvent.setup>, code: string) => {
      await user.click(screen.getByRole('button', { name: /Artikel scannen/ }))
      await user.type(await screen.findByPlaceholderText('Code eingeben + Enter'), `${code}{Enter}`)
    }

    it('zählt ab 0 (statt die angezeigte Soll-Menge zu überspringen) und lässt sich zurücknehmen', async () => {
      const user = userEvent.setup()
      renderFlow()
      await screen.findByText('A-01-01')
      expect(screen.getAllByText('Nicht gezählt – gilt als Soll')).toHaveLength(2)

      await scan(user, 'ART-001')
      expect(countInput('ART-001')).toHaveValue(1)
      expect(screen.getByText('Gezählt: 1 von 5')).toBeInTheDocument()
      expect(screen.getByRole('status')).toHaveTextContent('ART-001 +1 → 1 von 5')

      await scan(user, 'ART-001')
      expect(countInput('ART-001')).toHaveValue(2)

      // Rückgängig: Schritt für Schritt zurück, am Ende wieder "gilt als Soll".
      await user.click(screen.getByRole('button', { name: /Scan rückgängig/ }))
      expect(countInput('ART-001')).toHaveValue(1)
      await user.click(screen.getByRole('button', { name: /Scan rückgängig/ }))
      expect(countInput('ART-001')).toHaveValue(5)
      expect(screen.getAllByText('Nicht gezählt – gilt als Soll')).toHaveLength(2)
      expect(screen.queryByRole('button', { name: /Scan rückgängig/ })).not.toBeInTheDocument()
    })

    it('meldet eine unbekannte SKU, ohne eine Menge zu ändern', async () => {
      const user = userEvent.setup()
      renderFlow()
      await screen.findByText('A-01-01')

      await scan(user, 'FREMD-9')

      expect(screen.getByRole('status')).toHaveTextContent('SKU "FREMD-9" nicht in diesem Bin')
      expect(countInput('ART-001')).toHaveValue(5)
      expect(screen.queryByRole('button', { name: /Scan rückgängig/ })).not.toBeInTheDocument()
    })

    it('zählt nicht über die Soll-Menge hinaus (der Server lehnt beim Packen alles außerhalb von 0..Soll ab)', async () => {
      const user = userEvent.setup()
      renderFlow()
      await screen.findByText('A-01-01')

      await scan(user, 'ART-002') // Soll 2
      await scan(user, 'ART-002')
      expect(countInput('ART-002')).toHaveValue(2)
      await scan(user, 'ART-002')

      expect(countInput('ART-002')).toHaveValue(2)
      expect(screen.getByRole('status')).toHaveTextContent('ART-002: Soll-Menge bereits gezählt')
    })

    it('zählt bei derselben SKU in mehreren Positionen (mehrere Bestellungen im Bin) die nächste offene Position', async () => {
      api.setRoute('GET /picklists/pl1', makePickList({
        items: [
          makePickItem({ id: 'i1', articleSku: 'ART-001', quantity: 1 }),
          makePickItem({ id: 'i2', articleSku: 'ART-001', quantity: 2, orderId: 'o2', orderNumber: 'ORD-2', sequenceNumber: 2 }),
        ],
      }))
      const user = userEvent.setup()
      renderFlow()
      await screen.findByText('A-01-01')
      const [first, second] = screen.getAllByLabelText('ART-001 gepickte Menge')

      await scan(user, 'ART-001')
      expect([first, second].map((f) => (f as HTMLInputElement).value)).toEqual(['1', '2']) // erste Position voll, zweite unberührt (= Soll)
      await scan(user, 'ART-001')
      expect(second).toHaveValue(1) // der zweite Scan zählt die zweite Position
      await scan(user, 'ART-001')
      expect(second).toHaveValue(2)
      await scan(user, 'ART-001')
      expect(screen.getByRole('status')).toHaveTextContent('ART-001: Soll-Menge bereits gezählt')
    })
  })

  it('begrenzt Plus-Button und Eingabe auf die Soll-Menge', async () => {
    renderFlow()
    await screen.findByText('A-01-01')

    expect(screen.getByRole('button', { name: 'ART-002 Menge erhöhen' })).toBeDisabled() // Soll 2, angezeigt 2
    fireEvent.change(countInput('ART-001'), { target: { value: '9' } })

    expect(countInput('ART-001')).toHaveValue(5)
  })

  it('blendet Statusmeldungen nach kurzer Zeit selbst aus', async () => {
    renderFlow()
    await screen.findByText('A-01-01')

    vi.useFakeTimers()
    try {
      // Falscher Bin gescannt → Statusmeldung.
      fireEvent.click(screen.getByRole('button', { name: /Bin scannen/ }))
      fireEvent.change(screen.getByPlaceholderText('Code eingeben + Enter'), { target: { value: 'X-99' } })
      fireEvent.keyDown(screen.getByPlaceholderText('Code eingeben + Enter'), { key: 'Enter' })
      expect(screen.getByRole('status')).toHaveTextContent('Falscher Bin gescannt: X-99')

      act(() => {
        vi.advanceTimersByTime(4100)
      })
      expect(screen.queryByRole('status')).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })
})
