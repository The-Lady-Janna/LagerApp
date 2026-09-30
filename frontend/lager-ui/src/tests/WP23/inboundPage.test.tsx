import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { CreateInboundShipmentRequest, InboundShipmentDto, StockItemDto } from '../../api/types'
import { InboundPage } from '../../pages/InboundPage'
import { makeArticle } from '../helpers/fixtures'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// Wareneingang-Erfassungsmaske: Lot/MHD je Zeile, Validierung vor dem Senden und der Wareneingangs-Bug (Zeilen gingen beim
// Anlegen verloren: die Maske schickte lines mit, der Server legte nur den Kopf an).

/** Ortsdatum relativ zu heute als "yyyy-MM-dd" (so rechnet die Maske). */
function localDate(offsetDays: number): string {
  const d = new Date()
  d.setDate(d.getDate() + offsetDays)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

const TRACKED_STOCK: StockItemDto = {
  id: 'st1', articleId: 'a2', articleSku: 'ART-002', articleName: 'Joghurt', storageLocationId: 'b1', storageLocationCode: 'A-01',
  quantity: 5, lotNumber: 'LOT-ALT', expiryDate: `${localDate(20)}T00:00:00`,
}

describe('InboundPage: Lot/MHD und Anlegen mit Zeilen', () => {
  let api: MockApi
  let shipments: InboundShipmentDto[]

  /** Wie der korrigierte Server: legt Kopf UND Zeilen an. */
  const createdFrom = (req: CreateInboundShipmentRequest): InboundShipmentDto => ({
    id: 'new1', shipmentNumber: req.shipmentNumber ?? '?', supplierReference: req.supplierReference ?? null, notes: null, status: 'Draft',
    createdAt: '2026-06-15T10:00:00', receivedAt: null,
    lines: req.lines.map((l, i) => ({
      id: `l${i}`, articleId: l.articleId, articleSku: l.articleId === 'a1' ? 'ART-001' : 'ART-002', targetBinId: l.targetBinId, targetBinCode: 'A-01',
      quantity: l.quantity, lotNumber: l.lotNumber ?? null, expiryDate: l.expiryDate ? `${l.expiryDate}T00:00:00` : null,
    })),
  })

  beforeEach(() => {
    shipments = []
    api = installMockApi({
      'GET /inbound': () => shipments,
      'GET /articles': [makeArticle({ id: 'a1', sku: 'ART-001', name: 'Schraube M8' }), makeArticle({ id: 'a2', sku: 'ART-002', name: 'Joghurt' })],
      'GET /warehouse/storage-locations': [{ id: 'b1', code: 'A-01' }, { id: 'b2', code: 'B-02' }],
      'GET /stock': [TRACKED_STOCK],
      'POST /inbound': (req) => {
        const created = createdFrom(req.body as CreateInboundShipmentRequest)
        shipments = [created]
        return created
      },
    })
  })
  afterEach(() => api.restore())

  /** Öffnet die Maske und füllt Zeile 1 (Artikel, Bin, Menge; Lot/MHD optional). */
  async function fillLine1(user: ReturnType<typeof userEvent.setup>, opts: { article?: string; lot?: string; expiry?: string; quantity?: number } = {}) {
    await user.click(await screen.findByRole('button', { name: '+ Neuer Wareneingang' }))
    await user.selectOptions(screen.getByLabelText('Artikel Zeile 1'), opts.article ?? 'a1')
    await user.selectOptions(screen.getByLabelText('Ziel-Bin Zeile 1'), 'b1')
    if (opts.quantity) fireEvent.change(screen.getByLabelText('Menge Zeile 1'), { target: { value: String(opts.quantity) } })
    if (opts.lot) await user.type(screen.getByLabelText('Lot Zeile 1'), opts.lot)
    if (opts.expiry) fireEvent.change(screen.getByLabelText('MHD Zeile 1'), { target: { value: opts.expiry } })
  }

  it('legt Kopf und Zeilen an: der POST trägt Charge und MHD, danach steht die Lieferung mit ihrer Zeile in der Liste', async () => {
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)
    const expiry = localDate(90)
    await fillLine1(user, { quantity: 12, lot: 'LOT-X', expiry })
    await user.type(screen.getByLabelText(/Shipment-Nummer/), 'WE-TEST-1')

    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))

    await waitFor(() => expect(api.calls('POST', '/inbound')).toHaveLength(1))
    expect(api.calls('POST', '/inbound')[0].body).toEqual({
      shipmentNumber: 'WE-TEST-1',
      supplierReference: null,
      lines: [{ articleId: 'a1', targetBinId: 'b1', quantity: 12, lotNumber: 'LOT-X', expiryDate: expiry }],
    })
    // Hinweis, Maske zu, Lieferung mit Position in der Liste; die Details zeigen Lot und MHD
    expect(await screen.findByRole('status')).toHaveTextContent('Lieferung WE-TEST-1 mit 1 Position(en) angelegt')
    expect(screen.queryByRole('button', { name: 'Anlegen (Draft)' })).not.toBeInTheDocument()
    const listRow = (await screen.findByText('WE-TEST-1', { selector: 'td code' })).closest('tr')!
    expect(within(listRow).getByText('1')).toBeInTheDocument() // Positionen
    await user.click(within(listRow).getByRole('button', { name: 'Details' }))
    expect(screen.getByText('LOT-X')).toBeInTheDocument()
    expect(screen.getByText('OK')).toHaveClass('pill--success') // MHD in 90 Tagen
  })

  it('ohne Nummer erzeugt die Maske eine ("Auto wenn leer") - der Server verlangt eine', async () => {
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)
    await fillLine1(user)

    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))

    await waitFor(() => expect(api.calls('POST', '/inbound')).toHaveLength(1))
    expect((api.calls('POST', '/inbound')[0].body as CreateInboundShipmentRequest).shipmentNumber).toMatch(/^WE-\d{8}-\d{6}$/)
  })

  it('ein MHD in der Vergangenheit wird nicht gesendet; heute ist erlaubt', async () => {
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)
    await fillLine1(user, { lot: 'LOT-X', expiry: localDate(-1) })

    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Zeile 1: MHD liegt in der Vergangenheit')
    expect(api.calls('POST', '/inbound')).toHaveLength(0)

    fireEvent.change(screen.getByLabelText('MHD Zeile 1'), { target: { value: localDate(0) } })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument() // die Meldung verschwindet mit der Änderung
    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))
    await waitFor(() => expect(api.calls('POST', '/inbound')).toHaveLength(1))
  })

  it('bei einem Artikel, der schon mit MHD geführt wird, sind Charge und MHD Pflicht (mit Hinweis in der Zeile)', async () => {
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)
    await screen.findByRole('button', { name: '+ Neuer Wareneingang' })
    await waitFor(() => expect(api.calls('GET', '/stock')).toHaveLength(1))
    await fillLine1(user, { article: 'a2' })
    // erst wenn die Bestandsdaten da sind, ist die MHD-Pflege erkennbar
    expect(await screen.findByText(/wird bereits mit Charge und MHD geführt/)).toBeInTheDocument()
    expect(screen.getByLabelText('Lot Zeile 1')).toBeRequired()

    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Charge angeben')
    expect(alert).toHaveTextContent('MHD angeben')
    expect(api.calls('POST', '/inbound')).toHaveLength(0)

    await user.type(screen.getByLabelText('Lot Zeile 1'), 'LOT-NEU')
    fireEvent.change(screen.getByLabelText('MHD Zeile 1'), { target: { value: localDate(30) } })
    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))
    await waitFor(() => expect(api.calls('POST', '/inbound')).toHaveLength(1))
  })

  it('eine halb ausgefüllte Zeile geht nicht still verloren, sondern wird bemängelt; eine unberührte Zeile stört nicht', async () => {
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)
    await fillLine1(user, { lot: 'LOT-X' })
    await user.click(screen.getByRole('button', { name: '+ Zeile' }))
    await user.type(screen.getByLabelText('Lot Zeile 2'), 'LOT-OHNE-ARTIKEL') // jetzt ist Zeile 2 halb befüllt: Charge, aber kein Artikel/Bin

    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Zeile 2: Artikel wählen.')
    expect(alert).toHaveTextContent('Zeile 2: Ziel-Bin wählen.')
    expect(alert).not.toHaveTextContent('Zeile 1')
    expect(api.calls('POST', '/inbound')).toHaveLength(0)
  })

  it('ein Serverfehler beim Anlegen steht als Meldung da, die Eingaben bleiben erhalten', async () => {
    api.setRoute('POST /inbound', fail(409, {
      title: 'Conflict', status: 409, code: 'lot_expiry_mismatch', detail: 'Zeile 1: Charge LOT-X kommt im selben Lagerplatz mit verschiedenem MHD vor', correlationId: 'corr-7',
    }))
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)
    await fillLine1(user, { lot: 'LOT-X', expiry: localDate(10) })

    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Anlegen fehlgeschlagen:')
    expect(alert).toHaveTextContent('verschiedenem MHD')
    expect(alert).toHaveTextContent('Referenz: corr-7')
    expect(screen.getByLabelText('Lot Zeile 1')).toHaveValue('LOT-X')
    expect(screen.getByRole('button', { name: 'Anlegen (Draft)' })).toBeEnabled()
  })

  it('kommen weniger Zeilen zurück als gesendet (älterer Server), meldet die Maske es und behält die Eingaben', async () => {
    api.setRoute('POST /inbound', (req) => ({ ...createdFrom(req.body as CreateInboundShipmentRequest), lines: [] }))
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)
    await fillLine1(user, { lot: 'LOT-X' })
    await user.type(screen.getByLabelText(/Shipment-Nummer/), 'WE-LEER')

    await user.click(screen.getByRole('button', { name: 'Anlegen (Draft)' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Lieferung WE-LEER wurde angelegt, aber nur mit 0 von 1 Position(en)')
    expect(screen.queryByRole('status')).not.toBeInTheDocument() // kein Erfolgshinweis
    expect(screen.getByLabelText('Lot Zeile 1')).toHaveValue('LOT-X')
  })
})
