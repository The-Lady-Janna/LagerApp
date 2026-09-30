import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { OrdersPage } from '../../pages/OrdersPage'
import { OrderDetailPage } from '../../pages/OrderDetailPage'
import { ShipmentsPage } from '../../pages/ShipmentsPage'
import { CustomersPage } from '../../pages/CustomersPage'
import { InboundPage } from '../../pages/InboundPage'
import { WavesPage } from '../../pages/WavesPage'
import { CollapsibleCard } from '../../components/CollapsibleCard'
import { queryClient } from '../../lib/queryClient'
import { useAuth } from '../../state/auth'
import { useToasts } from '../../state/toasts'
import type { CustomerDto, InboundShipmentDto, OrderDto, PickWaveDto, ShipmentDto } from '../../api/types'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'

// Schritt 8/10 von WP19: die Seiten aus Wave 3 zeigen Fehler und Ladezustände einheitlich (ErrorBanner/LoadState) und
// erzeugen keine Doppelanzeige mehr (Toast UND Banner); Status erscheinen als StatusPill aus der zentralen Tabelle.

const order = (overrides: Partial<OrderDto> = {}): OrderDto => ({
  id: 'o1', orderNumber: 'ORD-1', customerReference: null, status: 'New', source: 'Manual', createdAt: '2025-01-01T10:00:00',
  lines: [], hasStockNow: true, hasStockAfterFifo: true, ...overrides,
})

/**
 * Mit dem ECHTEN QueryClient der App: sein QueryCache/MutationCache meldet jeden Fehler global als Toast (state/toasts.ts).
 * Angezeigt würde er vom ToastHost — hier reicht der Blick in den Store: ein Toast dort wäre die Doppelanzeige.
 */
function renderWithToasts(ui: React.ReactElement, path = '/') {
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>{ui}</MemoryRouter>
    </QueryClientProvider>,
  )
}

/** Wartet, bis GENAU EIN Hinweis (role="alert") mit diesem Text übrig ist, und gibt ihn zurück. */
async function onlyAlert(text: string): Promise<HTMLElement> {
  await waitFor(() => {
    const alerts = screen.getAllByRole('alert')
    expect(alerts).toHaveLength(1)
    expect(alerts[0]).toHaveTextContent(text)
  })
  return screen.getByRole('alert')
}

describe('Wave-3-Seiten: Fehler ohne Doppelanzeige', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /orders': [order()],
      'GET /articles': [],
      'GET /customers': [],
      'GET /cart-configs': [],
      'GET /shipments': [],
      'GET /shipments/carriers': [],
    })
  })
  afterEach(() => {
    api.restore()
    queryClient.clear()
    useToasts.getState().clear()
    useAuth.getState().logout()
  })

  it('OrdersPage: ein Ladefehler erscheint EINMAL als Banner mit "Erneut versuchen" — der globale Toast dazu wird zurückgenommen', async () => {
    api.setRoute('GET /orders', fail(403, null))
    renderWithToasts(<OrdersPage />)

    // Der Toast entsteht zuerst (QueryCache.onError) und wird vom Banner zurückgenommen — am Ende bleibt genau ein Hinweis.
    const alert = await onlyAlert('Konnte die Bestellungen nicht laden.')
    expect(useToasts.getState().toasts).toHaveLength(0)
    expect(alert).toHaveTextContent('Keine Berechtigung für diese Aktion.')
    expect(alert).not.toHaveTextContent('AxiosError')
    expect(within(alert).getByRole('button', { name: 'Erneut versuchen' })).toBeInTheDocument()
  })

  it('OrdersPage: ein abgelehntes Storno steht einmal inline (verständliche Meldung + Referenz), nicht zusätzlich als Toast', async () => {
    signInAs(['Manager'])
    api.setRoute('POST /orders/:id/cancel', fail(409, {
      code: 'order_not_cancellable', detail: 'Bestellung ORD-1 (Packed) lässt sich nicht mehr stornieren', correlationId: 'corr-99',
    }))
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    renderWithToasts(<OrdersPage />)

    await user.click(await screen.findByRole('button', { name: 'Bestellung ORD-1 stornieren' }))

    const alert = await onlyAlert('lässt sich nicht mehr stornieren')
    expect(useToasts.getState().toasts).toHaveLength(0)
    expect(alert).toHaveTextContent('Referenz: corr-99')
  })

  it('OrdersPage: während die Bestellungen laden, steht "Lade …" statt einer leeren Tabelle mit "Keine Bestellungen"', async () => {
    renderWithProviders(<OrdersPage />)

    // sofort nach dem ersten Render: die Abfrage läuft noch
    expect(screen.getByRole('status')).toHaveTextContent('Lade die Bestellungen…')
    expect(screen.queryByText('Keine Bestellungen.')).not.toBeInTheDocument()

    expect(await screen.findByText('ORD-1')).toBeInTheDocument()
    expect(screen.queryByText('Lade die Bestellungen…')).not.toBeInTheDocument()
  })

  it('OrderDetailPage: "nicht gefunden" erst, wenn geladen ist; vorher "Lade …", bei Fehler das Banner', async () => {
    api.setRoute('GET /orders', fail(403, null))
    const { unmount } = renderWithProviders(
      <Routes><Route path="/orders/:id" element={<OrderDetailPage />} /></Routes>,
      { initialEntries: ['/orders/o1'] },
    )
    expect(await screen.findByRole('alert')).toHaveTextContent('Konnte die Bestellung nicht laden.')
    expect(screen.queryByText('Bestellung nicht gefunden.')).not.toBeInTheDocument()
    unmount()

    api.setRoute('GET /orders', [order({ id: 'andere' })])
    renderWithProviders(
      <Routes><Route path="/orders/:id" element={<OrderDetailPage />} /></Routes>,
      { initialEntries: ['/orders/o1'] },
    )
    expect(await screen.findByText('Bestellung nicht gefunden.')).toBeInTheDocument()
  })

  it('ShipmentsPage: Ladefehler als Banner statt endlosem "Lade…"; die Kopfzeile mit "+ Neues Paket" bleibt bedienbar', async () => {
    api.setRoute('GET /shipments', fail(403, null))
    renderWithToasts(<ShipmentsPage />)

    await onlyAlert('Konnte die Sendungen nicht laden.')

    expect(useToasts.getState().toasts).toHaveLength(0)
    expect(screen.getByRole('button', { name: '+ Neues Paket' })).toBeInTheDocument()
  })

  it('CustomersPage: ein Ladefehler erscheint einmal als Banner, ohne Toast-Doppelung', async () => {
    api.setRoute('GET /customers', fail(403, null))
    renderWithToasts(<CustomersPage />)

    await onlyAlert('Konnte die Kunden nicht laden.')

    expect(useToasts.getState().toasts).toHaveLength(0)
  })
})

describe('Wave-3-Seiten: Status und Bedienbarkeit', () => {
  let api: MockApi

  afterEach(() => api.restore())

  it('ShipmentsPage zeigt den Status als StatusPill der Versand-Tabelle (Delivered = grün, Shipped = Warnung)', async () => {
    const shipment = (id: string, status: ShipmentDto['status']): ShipmentDto => ({
      id, shipmentNumber: `SH-${id}`, orderId: 'o1', orderNumber: 'ORD-1', pickListId: null, carrierCode: 'MANUAL', trackingNumber: null,
      trackingUrl: null, weightGrams: 1000, lengthMm: 1, widthMm: 1, heightMm: 1, costCents: 0, notes: null, status,
      createdAt: '2025-01-01T10:00:00', labeledAt: null, shippedAt: null, deliveredAt: null,
    })
    api = installMockApi({
      'GET /shipments': [shipment('1', 'Delivered'), shipment('2', 'Shipped')],
      'GET /shipments/carriers': [],
      'GET /orders': [],
    })
    renderWithProviders(<ShipmentsPage />)

    await screen.findByText('SH-1')

    expect(screen.getByText('Delivered')).toHaveClass('pill', 'pill--success')
    expect(screen.getByText('Shipped')).toHaveClass('pill', 'pill--warning')
  })

  it('CustomersPage: die Kundenzeile ist ein Button mit aria-expanded/aria-controls (Tastatur), kein klickbares div', async () => {
    const customer: CustomerDto = {
      id: 'c1', code: 'K-1', name: 'Müller GmbH', email: null, phone: null, notes: null, currency: 'EUR', defaultDiscountPercent: 0,
      isActive: true, createdAt: '2025-01-01T10:00:00', addresses: [],
    }
    api = installMockApi({ 'GET /customers': [customer] })
    const user = userEvent.setup()
    renderWithProviders(<CustomersPage />)

    const toggle = await screen.findByRole('button', { name: /K-1/ })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    toggle.focus()
    await user.keyboard('{Enter}')

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    const details = document.getElementById(toggle.getAttribute('aria-controls')!)
    expect(details).not.toBeNull()
    expect(within(details!).getByLabelText('Email')).toBeInTheDocument()
  })

  it('InboundPage und WavesPage nutzen die zentrale Status-Tabelle', async () => {
    const shipment: InboundShipmentDto = {
      id: 'i1', shipmentNumber: 'WE-1', supplierReference: null, notes: null, status: 'Received',
      createdAt: '2025-01-01T10:00:00', receivedAt: '2025-01-02T10:00:00', lines: [],
    }
    const wave: PickWaveDto = {
      id: 'w1', waveNumber: 'W-1', description: null, status: 'Released', cutoffAt: null, createdAt: '2025-01-01T10:00:00',
      releasedAt: null, completedAt: null, orderIds: [], pickListIds: [],
    }
    api = installMockApi({
      'GET /inbound': [shipment], 'GET /articles': [], 'GET /warehouse/storage-locations': [],
      'GET /pick-waves': [wave], 'GET /orders': [],
    })

    const inbound = renderWithProviders(<InboundPage />)
    expect(await screen.findByText('Received')).toHaveClass('pill--success')
    inbound.unmount()

    renderWithProviders(<WavesPage />)
    expect(await screen.findByText('Released')).toHaveClass('pill--info')
  })
})

describe('CollapsibleCard', () => {
  it('der Titel ist ein Button mit aria-expanded/aria-controls und per Tastatur bedienbar; headerRight liegt daneben, nicht darin', async () => {
    const user = userEvent.setup()
    render(
      <CollapsibleCard title="Filter" headerRight={<button>Zurücksetzen</button>} defaultOpen={false}>
        <p>Inhalt</p>
      </CollapsibleCard>,
    )
    const toggle = screen.getByRole('button', { name: 'Filter' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByText('Inhalt')).not.toBeInTheDocument()
    expect(toggle).not.toContainElement(screen.getByRole('button', { name: 'Zurücksetzen' }))

    await user.tab()
    expect(toggle).toHaveFocus()
    await user.keyboard(' ')

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(document.getElementById(toggle.getAttribute('aria-controls')!)).toHaveTextContent('Inhalt')
    expect(screen.getByRole('heading', { name: 'Filter' })).toContainElement(toggle)
  })

  it('merkt sich den Zustand per storageKey', async () => {
    const user = userEvent.setup()
    const { unmount } = render(<CollapsibleCard title="T" storageKey="wp19-test" defaultOpen>x</CollapsibleCard>)
    await user.click(screen.getByRole('button', { name: 'T' }))
    unmount()

    render(<CollapsibleCard title="T" storageKey="wp19-test" defaultOpen>x</CollapsibleCard>)

    expect(screen.getByRole('button', { name: 'T' })).toHaveAttribute('aria-expanded', 'false')
  })
})
