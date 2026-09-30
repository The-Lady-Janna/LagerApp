import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ShipmentsPage } from '../../pages/ShipmentsPage'
import type { CarrierDto, OrderDto, ShipmentDto } from '../../api/types'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

function makeShipment(overrides: Partial<ShipmentDto> = {}): ShipmentDto {
  return {
    id: 's1', shipmentNumber: 'SH-0001', orderId: 'o1', orderNumber: 'ORD-1', pickListId: null, carrierCode: 'MANUAL',
    trackingNumber: null, trackingUrl: null, weightGrams: 1500, lengthMm: 300, widthMm: 200, heightMm: 100, costCents: 0,
    notes: null, status: 'Ready', createdAt: '2025-01-01T10:00:00', labeledAt: null, shippedAt: null, deliveredAt: null,
    ...overrides,
  }
}

function makeOrder(overrides: Partial<OrderDto> = {}): OrderDto {
  return {
    id: 'o1', orderNumber: 'ORD-1', customerReference: null, status: 'Packed', source: 'Manual',
    createdAt: '2025-01-01T10:00:00', lines: [], hasStockNow: true, hasStockAfterFifo: true, ...overrides,
  }
}

const carriers: CarrierDto[] = [
  { code: 'MANUAL', displayName: 'Manuell (Tracking-Nr per Hand)', isConfigured: true, note: null },
  { code: 'DHL', displayName: 'DHL', isConfigured: false, note: 'Die DHL-Anbindung ist nicht eingerichtet.' },
  { code: 'UPS', displayName: 'UPS', isConfigured: false, note: 'Die UPS-Anbindung ist nicht eingerichtet.' },
]

describe('ShipmentsPage — Tracking-Links', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /shipments': [
        makeShipment({ id: 's-js', shipmentNumber: 'SH-JS', trackingNumber: 'TRACK-JS', trackingUrl: 'javascript:alert(document.cookie)', status: 'Labeled' }),
        makeShipment({ id: 's-ok', shipmentNumber: 'SH-OK', trackingNumber: 'TRACK-OK', trackingUrl: 'https://tracking.example/t/OK', status: 'Labeled' }),
        makeShipment({ id: 's-data', shipmentNumber: 'SH-DATA', trackingNumber: 'TRACK-DATA', trackingUrl: 'data:text/html,<b>x</b>', status: 'Shipped' }),
        makeShipment({ id: 's-none', shipmentNumber: 'SH-NONE', trackingNumber: 'TRACK-NONE', trackingUrl: null, status: 'Labeled' }),
      ],
      'GET /shipments/carriers': carriers,
      'GET /orders': [],
    })
  })
  afterEach(() => api.restore())

  it('rendert javascript:- und data:-URLs nicht als Link, sondern als Text; https-Links bleiben Links', async () => {
    renderWithProviders(<ShipmentsPage />)
    await screen.findByText('SH-JS')

    // gefährliche Schemata: die Nummer ist Text, die URL steht als Text daneben - kein Link
    expect(screen.queryByRole('link', { name: 'TRACK-JS' })).not.toBeInTheDocument()
    expect(screen.getByText('TRACK-JS')).toBeInTheDocument()
    expect(screen.getByText('javascript:alert(document.cookie)')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'TRACK-DATA' })).not.toBeInTheDocument()
    expect(document.querySelectorAll('a[href^="javascript:"], a[href^="data:"]')).toHaveLength(0)

    // ein gewöhnlicher https-Link bleibt ein Link (neuer Tab, ohne Referrer/Opener)
    const link = screen.getByRole('link', { name: 'TRACK-OK' })
    expect(link).toHaveAttribute('href', 'https://tracking.example/t/OK')
    expect(link).toHaveAttribute('target', '_blank')
    expect(link.getAttribute('rel')).toContain('noreferrer')
    // ohne URL: nur die Nummer
    expect(screen.getByText('TRACK-NONE').closest('a')).toBeNull()
  })

  it('lehnt beim Zuweisen eine Tracking-URL mit gefährlichem Schema schon im Formular ab', async () => {
    api.setRoute('GET /shipments', [makeShipment({ id: 's-new', shipmentNumber: 'SH-NEW', status: 'Ready' })])
    const user = userEvent.setup()
    renderWithProviders(<ShipmentsPage />)
    await user.click(await screen.findByRole('button', { name: 'Tracking-Nr' }))

    await user.type(screen.getByLabelText(/^Tracking-Nr/), 'T-1')
    await user.type(screen.getByLabelText(/^Tracking-URL/), 'javascript:alert(1)')

    expect(screen.getByText(/absolute http\(s\)-Adresse/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Zuweisen' })).toBeDisabled()

    await user.clear(screen.getByLabelText(/^Tracking-URL/))
    await user.type(screen.getByLabelText(/^Tracking-URL/), 'https://t.example/1')
    expect(screen.getByRole('button', { name: 'Zuweisen' })).toBeEnabled()
  })
})

describe('ShipmentsPage — neues Paket', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /shipments': [],
      'GET /shipments/carriers': carriers,
      'GET /orders': [
        makeOrder({ id: 'o-packed', orderNumber: 'ORD-PACKED', status: 'Packed', customerName: 'Müller GmbH',
          shippingAddress: { id: 'ad', kind: 'Shipping', label: 'Lager', street: 'Hafenstr. 5', street2: null, zip: '20457', city: 'Hamburg', country: 'DE' } }),
        makeOrder({ id: 'o-new', orderNumber: 'ORD-NEW', status: 'New' }),
        makeOrder({ id: 'o-shipped', orderNumber: 'ORD-SHIPPED', status: 'Shipped' }),
      ],
      'POST /packing/:id/plan': {
        orderId: 'o-packed', orderNumber: 'ORD-PACKED', unpacked: [], warnings: null,
        cartons: [{ index: 0, cartonType: 'Karton M', innerLengthMm: 400, innerWidthMm: 300, innerHeightMm: 200, totalWeightGrams: 2300, fillRatio: 0.5, allocations: [] }],
      },
      'POST /shipments': (request) => makeShipment({ id: 's-created', orderId: (request.body as { orderId: string }).orderId }),
    })
  })
  afterEach(() => api.restore())

  async function openForm() {
    const user = userEvent.setup()
    renderWithProviders(<ShipmentsPage />)
    await user.click(await screen.findByRole('button', { name: '+ Neues Paket' }))
    return user
  }

  it('bietet nur gepackte Bestellungen an', async () => {
    await openForm()

    const orderSelect = screen.getByLabelText(/^Order/)
    expect(await within(orderSelect).findByRole('option', { name: /ORD-PACKED/ })).toBeInTheDocument()
    expect(within(orderSelect).queryByRole('option', { name: /ORD-NEW/ })).not.toBeInTheDocument()
    expect(within(orderSelect).queryByRole('option', { name: /ORD-SHIPPED/ })).not.toBeInTheDocument()
  })

  it('bietet nur konfigurierte Carrier an; nicht angebundene stehen als "nicht verfügbar" da, ohne Entwickler-Jargon', async () => {
    await openForm()

    const carrierSelect = screen.getByLabelText(/^Carrier/)
    const manual = await within(carrierSelect).findByRole('option', { name: 'Manuell (Tracking-Nr per Hand)' })
    expect(manual).not.toBeDisabled()
    for (const name of ['DHL – nicht verfügbar', 'UPS – nicht verfügbar'])
      expect(within(carrierSelect).getByRole('option', { name })).toBeDisabled()
    // der konfigurierte ist vorgewählt
    expect(carrierSelect).toHaveValue('MANUAL')
    expect(document.body.textContent).not.toMatch(/TODO|Stub/i)
    expect(screen.getByText(/Nicht verfügbar: DHL \(Die DHL-Anbindung ist nicht eingerichtet\.\)/)).toBeInTheDocument()
  })

  it('sendet keine Platzhalter-Maße: die Felder sind leer, Anlegen bleibt gesperrt bis alles eingegeben ist', async () => {
    const user = await openForm()
    await within(screen.getByLabelText(/^Order/)).findByRole('option', { name: /ORD-PACKED/ })
    await user.selectOptions(screen.getByLabelText(/^Order/), 'o-packed')

    for (const label of [/^Gewicht/, /^Länge/, /^Breite/, /^Höhe/])
      expect(screen.getByLabelText(label)).toHaveValue(null)
    const create = screen.getByRole('button', { name: 'Anlegen' })
    expect(create).toBeDisabled()

    await user.type(screen.getByLabelText(/^Länge/), '300')
    await user.type(screen.getByLabelText(/^Breite/), '200')
    await user.type(screen.getByLabelText(/^Höhe/), '0')     // 0 ist kein gültiges Maß
    await user.type(screen.getByLabelText(/^Gewicht/), '1500')
    expect(create).toBeDisabled()
    await user.clear(screen.getByLabelText(/^Höhe/))
    await user.type(screen.getByLabelText(/^Höhe/), '100')
    expect(create).toBeEnabled()

    await user.click(create)

    await waitFor(() => expect(api.calls('POST', '/shipments')).toHaveLength(1))
    expect(api.calls('POST', '/shipments')[0].body).toEqual({
      orderId: 'o-packed', carrierCode: 'MANUAL', lengthMm: 300, widthMm: 200, heightMm: 100, weightGrams: 1500, notes: null,
    })
  })

  it('übernimmt Maße und Gewicht aus dem Packvorschlag der Bestellung', async () => {
    const user = await openForm()
    await within(screen.getByLabelText(/^Order/)).findByRole('option', { name: /ORD-PACKED/ })
    await user.selectOptions(screen.getByLabelText(/^Order/), 'o-packed')

    await user.click(await screen.findByRole('button', { name: /^Karton 1/ }))

    expect(screen.getByLabelText(/^Länge/)).toHaveValue(400)
    expect(screen.getByLabelText(/^Breite/)).toHaveValue(300)
    expect(screen.getByLabelText(/^Höhe/)).toHaveValue(200)
    expect(screen.getByLabelText(/^Gewicht/)).toHaveValue(2300)
    expect(screen.getByText(/Empfänger: Müller GmbH, Hafenstr\. 5, 20457 Hamburg/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Anlegen' })).toBeEnabled()
  })

  it('zeigt einen abgelehnten Versandauftrag inline mit der Meldung des Servers und behält die Eingaben', async () => {
    api.setRoute('POST /shipments', () => fail(409, {
      code: 'order_not_packed', error: 'Für Bestellung ORD-PACKED lässt sich keine Sendung anlegen: sie hat den Status Picking',
    }))
    const user = await openForm()
    await within(screen.getByLabelText(/^Order/)).findByRole('option', { name: /ORD-PACKED/ })
    await user.selectOptions(screen.getByLabelText(/^Order/), 'o-packed')
    await user.click(await screen.findByRole('button', { name: /^Karton 1/ }))

    await user.click(screen.getByRole('button', { name: 'Anlegen' }))

    expect(await screen.findByText(/lässt sich keine Sendung anlegen/)).toBeInTheDocument()
    expect(screen.getByLabelText(/^Länge/)).toHaveValue(400)
  })
})
