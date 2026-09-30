import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { OrdersPage } from '../../pages/OrdersPage'
import { OrderDetailPage } from '../../pages/OrderDetailPage'
import type { CustomerDto, OrderDto } from '../../api/types'
import { useAuth } from '../../state/auth'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'

function makeOrder(overrides: Partial<OrderDto> = {}): OrderDto {
  return {
    id: 'o1',
    orderNumber: 'ORD-1',
    customerReference: null,
    status: 'New',
    source: 'Manual',
    createdAt: '2025-01-01T10:00:00',
    lines: [{ id: 'l1', articleId: 'a1', articleSku: 'ART-001', quantity: 2 }],
    hasStockNow: true,
    hasStockAfterFifo: true,
    customerId: null,
    customerName: null,
    shippingAddressId: null,
    shippingAddress: null,
    priority: 0,
    dueDate: null,
    externalReference: null,
    ...overrides,
  }
}

const customer: CustomerDto = {
  id: 'c1', code: 'K-1', name: 'Müller GmbH', email: null, phone: null, notes: null, currency: 'EUR',
  defaultDiscountPercent: 0, isActive: true, createdAt: '2025-01-01T10:00:00',
  addresses: [
    { id: 'ad-ship', kind: 'Shipping', label: 'Lager', street: 'Hafenstr. 5', street2: null, zip: '20457', city: 'Hamburg', country: 'DE' },
    { id: 'ad-bill', kind: 'Billing', label: 'Buchhaltung', street: 'Postfach 1', street2: null, zip: '10115', city: 'Berlin', country: 'DE' },
  ],
}

/** Die Zellen der ersten Spalte (Bestellnummer) in der Reihenfolge der Tabelle. */
function orderNumbersInTable(): string[] {
  const table = screen.getByRole('table')
  return within(table).getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[0].textContent ?? '')
}

describe('OrdersPage — Lebenszyklus, Storno, Filter, Kunde', () => {
  let api: MockApi
  let orders: OrderDto[]

  beforeEach(() => {
    orders = [
      makeOrder({ id: 'o-new', orderNumber: 'ORD-NEW', status: 'New' }),
      makeOrder({ id: 'o-picking', orderNumber: 'ORD-PICKING', status: 'Picking' }),
      makeOrder({ id: 'o-picked', orderNumber: 'ORD-PICKED', status: 'Picked' }),
      makeOrder({ id: 'o-packed', orderNumber: 'ORD-PACKED', status: 'Packed' }),
      makeOrder({ id: 'o-shipped', orderNumber: 'ORD-SHIPPED', status: 'Shipped' }),
      makeOrder({ id: 'o-cancelled', orderNumber: 'ORD-CANCELLED', status: 'Cancelled' }),
    ]
    api = installMockApi({
      'GET /orders': () => orders,
      'GET /articles': [{ id: 'a1', sku: 'ART-001', name: 'Schraube' }],
      'GET /customers': [customer],
      'GET /cart-configs': [],
      'POST /orders/:id/cancel': (_request, params) => {
        const order = orders.find((o) => o.id === params.id)!
        order.status = 'Cancelled'
        return order
      },
      'POST /orders/manual': (request) => makeOrder({ id: 'o-created', orderNumber: (request.body as { orderNumber: string }).orderNumber }),
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('zeigt jeden Status des Lebenszyklus mit deutschem Text', async () => {
    signInAs(['Manager'])
    renderWithProviders(<OrdersPage />)

    await screen.findByText('ORD-NEW')
    const table = within(screen.getByRole('table'))
    for (const label of ['Neu', 'In Kommissionierung', 'Kommissioniert', 'Gepackt', 'Versendet', 'Storniert'])
      expect(table.getByText(label)).toBeInTheDocument()
  })

  it('Storno fragt nach, ruft cancel auf und lädt die Bestellungen neu', async () => {
    signInAs(['Manager'])
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const user = userEvent.setup()
    renderWithProviders(<OrdersPage />)
    const cancelNew = await screen.findByRole('button', { name: 'Bestellung ORD-NEW stornieren' })

    await user.click(cancelNew)
    expect(confirm).toHaveBeenCalledTimes(1)
    expect(confirm.mock.calls[0][0]).toContain('ORD-NEW')
    expect(api.calls('POST', '/orders/:id/cancel')).toHaveLength(0)   // abgelehnt: nichts passiert

    confirm.mockReturnValue(true)
    const loadsBefore = api.calls('GET', '/orders').length
    await user.click(cancelNew)

    await waitFor(() => expect(api.calls('POST', '/orders/o-new/cancel')).toHaveLength(1))
    // die Liste wurde neu geladen (Invalidierung) und zeigt die Bestellung jetzt als storniert, ohne Storno-Knopf
    await waitFor(() => expect(api.calls('GET', '/orders').length).toBeGreaterThan(loadsBefore))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Bestellung ORD-NEW stornieren' })).not.toBeInTheDocument())
  })

  it('bietet Storno nur aus New, Picking und Picked an - nicht für gepackte, versendete und stornierte', async () => {
    signInAs(['Manager'])
    renderWithProviders(<OrdersPage />)

    await screen.findByText('ORD-NEW')
    const names = screen.getAllByRole('button', { name: /stornieren/ }).map((b) => b.getAttribute('aria-label'))
    expect(names).toEqual([
      'Bestellung ORD-NEW stornieren', 'Bestellung ORD-PICKING stornieren', 'Bestellung ORD-PICKED stornieren',
    ])
  })

  it.each([['Picker'], ['Packer'], ['Viewer']])('blendet Storno für die Rolle %s aus (der Server verlangt Manager)', async (role) => {
    signInAs([role])
    renderWithProviders(<OrdersPage />)

    await screen.findByText('ORD-NEW')
    expect(screen.queryByRole('button', { name: /stornieren/ })).not.toBeInTheDocument()
  })

  it('zeigt einen abgelehnten Storno (409) inline mit der Meldung des Servers', async () => {
    signInAs(['Manager'])
    api.setRoute('POST /orders/:id/cancel', () => fail(409, {
      code: 'order_not_cancellable', error: 'Bestellung ORD-NEW (Packed) lässt sich nicht mehr stornieren',
    }))
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    renderWithProviders(<OrdersPage />)

    await user.click(await screen.findByRole('button', { name: 'Bestellung ORD-NEW stornieren' }))

    expect(await screen.findByText(/lässt sich nicht mehr stornieren/)).toBeInTheDocument()
  })

  it('filtert nach Status', async () => {
    signInAs(['Manager'])
    const user = userEvent.setup()
    renderWithProviders(<OrdersPage />)
    await screen.findByText('ORD-NEW')
    expect(orderNumbersInTable()).toHaveLength(6)

    await user.selectOptions(screen.getByLabelText('Status'), 'Packed')

    expect(orderNumbersInTable()).toEqual(['ORD-PACKED'])
  })

  it('sortiert neue Bestellungen wie beim Kommissionieren: Priorität, Fälligkeit, Eingang', async () => {
    signInAs(['Manager'])
    orders = [
      makeOrder({ id: 'a', orderNumber: 'ORD-OLD', createdAt: '2025-01-01T10:00:00' }),
      makeOrder({ id: 'b', orderNumber: 'ORD-DUE', createdAt: '2025-01-03T10:00:00', dueDate: '2030-01-02T00:00:00Z' }),
      makeOrder({ id: 'c', orderNumber: 'ORD-URGENT', createdAt: '2025-01-02T10:00:00', priority: 3 }),
      makeOrder({ id: 'd', orderNumber: 'ORD-NEWER', createdAt: '2025-01-04T10:00:00' }),
    ]
    renderWithProviders(<OrdersPage />)

    await screen.findByText('ORD-OLD')

    expect(orderNumbersInTable()).toEqual(['ORD-URGENT', 'ORD-DUE', 'ORD-OLD', 'ORD-NEWER'])
  })

  it('markiert eine offene Bestellung erst nach ihrem Fälligkeitstag als überfällig, nicht am Fälligkeitstag selbst', async () => {
    signInAs(['Manager'])
    const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10)
    orders = [
      makeOrder({ id: 'a', orderNumber: 'ORD-TODAY', dueDate: `${day(0)}T00:00:00Z` }),
      makeOrder({ id: 'b', orderNumber: 'ORD-LATE', dueDate: `${day(-2)}T00:00:00Z` }),
      makeOrder({ id: 'c', orderNumber: 'ORD-LATE-SHIPPED', status: 'Shipped', dueDate: `${day(-2)}T00:00:00Z` }),
    ]
    renderWithProviders(<OrdersPage />)
    await screen.findByText('ORD-TODAY')

    const rowOf = (number: string) => screen.getByText(number).closest('tr')!
    expect(rowOf('ORD-TODAY').textContent).not.toContain('überfällig')
    expect(rowOf('ORD-LATE').textContent).toContain('überfällig')
    expect(rowOf('ORD-LATE-SHIPPED').textContent).not.toContain('überfällig')   // schon versendet: nichts mehr offen
  })

  it('legt eine Bestellung mit Kunde, Lieferadresse, Priorität und Fälligkeit an', async () => {
    signInAs(['Manager'])
    const user = userEvent.setup()
    renderWithProviders(<OrdersPage />)
    await user.click(await screen.findByText('Manuelle Bestellung anlegen'))

    await user.type(screen.getByLabelText(/^Auftragsnummer/), 'ORD-NEU-1')
    const customerSelect = await screen.findByLabelText(/^Kunde\b/)
    await screen.findByRole('option', { name: 'K-1 – Müller GmbH' })
    await user.selectOptions(customerSelect, 'c1')
    // Nur Liefer- und Doppeladressen stehen zur Wahl; die einzige ist vorbelegt
    const addressSelect = screen.getByLabelText(/^Lieferadresse/)
    expect(within(addressSelect).queryByText(/Buchhaltung/)).not.toBeInTheDocument()
    expect(addressSelect).toHaveValue('ad-ship')
    await user.selectOptions(screen.getByLabelText(/^Priorität/), 'Hoch')
    fireEvent.change(screen.getByLabelText(/^Fällig am/), { target: { value: '2026-10-05' } })
    await screen.findByRole('option', { name: 'ART-001 – Schraube' })
    await user.selectOptions(screen.getByDisplayValue('– Artikel –'), 'a1')

    await user.click(screen.getByRole('button', { name: 'Bestellung anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/orders/manual')).toHaveLength(1))
    expect(api.calls('POST', '/orders/manual')[0].body).toEqual({
      orderNumber: 'ORD-NEU-1',
      customerReference: null,
      lines: [{ articleId: 'a1', quantity: 1 }],
      customerId: 'c1',
      shippingAddressId: 'ad-ship',
      priority: 2,
      dueDate: '2026-10-05T00:00:00.000Z',
    })
  })

  it('zeigt Fehler beim Anlegen inline mit der Meldung des Servers und behält die Eingaben', async () => {
    signInAs(['Manager'])
    api.setRoute('POST /orders/manual', () => fail(409, {
      code: 'duplicate_order_number', error: "Bestellung 'ORD-NEU-1' existiert bereits",
    }))
    const user = userEvent.setup()
    renderWithProviders(<OrdersPage />)
    await user.click(await screen.findByText('Manuelle Bestellung anlegen'))
    await user.type(screen.getByLabelText(/^Auftragsnummer/), 'ORD-NEU-1')
    await screen.findByRole('option', { name: 'ART-001 – Schraube' })
    await user.selectOptions(screen.getByDisplayValue('– Artikel –'), 'a1')

    await user.click(screen.getByRole('button', { name: 'Bestellung anlegen' }))

    expect(await screen.findByText(/existiert bereits/)).toBeInTheDocument()
    expect(screen.getByLabelText(/^Auftragsnummer/)).toHaveValue('ORD-NEU-1')
  })
})

describe('OrderDetailPage — Kunde, Lieferadresse, Storno, Packvorschlag', () => {
  let api: MockApi
  let orders: OrderDto[]

  const detail = (id: string) => renderWithProviders(
    <Routes><Route path="/orders/:id" element={<OrderDetailPage />} /></Routes>,
    { initialEntries: [`/orders/${id}`] },
  )

  beforeEach(() => {
    orders = [
      makeOrder({
        id: 'o1', orderNumber: 'ORD-1', status: 'Picked', customerId: 'c1', customerName: 'Müller GmbH', priority: 2,
        dueDate: '2026-10-05T00:00:00Z', externalReference: 'SHOP-42',
        shippingAddressId: 'ad-ship',
        shippingAddress: { id: 'ad-ship', kind: 'Shipping', label: 'Lager', street: 'Hafenstr. 5', street2: null, zip: '20457', city: 'Hamburg', country: 'DE' },
      }),
      makeOrder({ id: 'o2', orderNumber: 'ORD-2', status: 'New' }),
    ]
    api = installMockApi({
      'GET /orders': () => orders,
      'GET /warehouse/pick-points': [],
      'POST /packing/:id/plan': (_request, params) => ({
        orderId: params.id, orderNumber: 'ORD-1', cartons: [], unpacked: [],
        warnings: ['Artikel ART-001 hat keine Abmessungen und wurde nicht verpackt'],
      }),
      'POST /orders/:id/cancel': (_request, params) => {
        const order = orders.find((o) => o.id === params.id)!
        order.status = 'Cancelled'
        return order
      },
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('zeigt Status, Kunde, Lieferadresse, Priorität, Fälligkeit und die Warnungen des Packvorschlags', async () => {
    signInAs(['Manager'])
    detail('o1')

    expect(await screen.findByText('Kommissioniert')).toBeInTheDocument()
    expect(screen.getByText('Müller GmbH')).toBeInTheDocument()
    expect(screen.getByText(/Hafenstr\. 5/)).toBeInTheDocument()
    expect(screen.getByText('Hoch')).toBeInTheDocument()
    expect(screen.getByText('05.10.2026')).toBeInTheDocument()
    expect(screen.getByText('SHOP-42')).toBeInTheDocument()
    // Packplan: Heuristik, nicht "3D", samt Warnungen
    expect(screen.getByRole('heading', { name: 'Packvorschlag' })).toBeInTheDocument()
    expect(await screen.findByText(/hat keine Abmessungen/)).toBeInTheDocument()
  })

  it('bietet "Pickliste generieren" nur für neue Bestellungen an (sonst entstünde eine doppelte Liste)', async () => {
    signInAs(['Manager'])
    const { unmount } = detail('o1')
    await screen.findByText('Müller GmbH')
    expect(screen.queryByRole('heading', { name: 'Pickliste generieren' })).not.toBeInTheDocument()
    unmount()

    detail('o2')
    expect(await screen.findByRole('heading', { name: 'Pickliste generieren' })).toBeInTheDocument()
  })

  it('storniert nach Rückfrage und zeigt danach den Status Storniert', async () => {
    signInAs(['Manager'])
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    detail('o1')

    await user.click(await screen.findByRole('button', { name: 'Bestellung stornieren' }))

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('aus der Pickliste entfernt'))
    await waitFor(() => expect(api.calls('POST', '/orders/o1/cancel')).toHaveLength(1))
    expect(await screen.findByText('Storniert')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Bestellung stornieren' })).not.toBeInTheDocument()
  })

  it('zeigt keinen Storno-Knopf für gepackte Bestellungen', async () => {
    signInAs(['Manager'])
    orders[0].status = 'Packed'
    detail('o1')

    expect(await screen.findByText('Gepackt')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Bestellung stornieren' })).not.toBeInTheDocument()
  })
})
