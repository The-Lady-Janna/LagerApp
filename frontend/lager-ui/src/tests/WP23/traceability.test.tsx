import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes, useLocation } from 'react-router-dom'
import type { ChargeTraceDto } from '../../api/lotHooks'
import { TraceabilityPage } from '../../features/traceability/TraceabilityPage'
import { featureRoutes } from '../../routes/registry'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// Seite /traceability: die Suche nach einer Chargennummer zeigt Wareneingang, Bestand, Bewegungen und betroffene Bestellungen.

/** MHD relativ zu heute (UTC-Kalendertag), damit die Status-Pills nicht am Kalender hängen. */
const inDays = (n: number) => `${new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10)}T00:00:00`

const trace = (overrides: Partial<ChargeTraceDto> = {}): ChargeTraceDto => ({
  lotNumber: 'LOT-A',
  inbounds: [
    { shipmentId: 's1', shipmentNumber: 'WE-1001', articleId: 'a1', articleSku: 'ART-001', targetBinId: 'b1', targetBinCode: 'A-01-01', quantity: 10, expiryDate: inDays(40), receivedAt: '2026-05-01T08:00:00', status: 'Received' },
    { shipmentId: 's2', shipmentNumber: 'WE-1002', articleId: 'a1', articleSku: 'ART-001', targetBinId: 'b1', targetBinCode: 'A-01-01', quantity: 99, expiryDate: inDays(40), receivedAt: '2026-05-02T08:00:00', status: 'Draft' },
  ],
  currentStock: [{ stockItemId: 'st1', articleId: 'a1', articleSku: 'ART-001', binId: 'b1', binCode: 'A-01-01', quantity: 6, expiryDate: inDays(3) }],
  movements: [
    { at: '2026-05-01T08:05:00', quantityDelta: 10, reason: 'Inbound', binId: 'b1', binCode: 'A-01-01', articleId: 'a1', articleSku: 'ART-001', referenceType: 'InboundShipment', referenceId: 's1' },
    { at: '2026-05-03T09:00:00', quantityDelta: -4, reason: 'Pick', binId: 'b1', binCode: 'A-01-01', articleId: 'a1', articleSku: 'ART-001', referenceType: 'PickList', referenceId: 'pl1' },
  ],
  currentStockTotal: 6,
  inboundTotal: 10,
  orders: [{ orderId: 'o1', orderNumber: 'ORD-77', customerReference: 'Kunde Müller', status: 'Shipped', pickListId: 'pl1', pickListNumber: 'PL-0001', pickedAt: '2026-05-03T09:00:00' }],
  ...overrides,
})

/** Zeigt die aktuelle Adresse mit an (die Suche schreibt ?lot=… hinein). */
function Where() {
  const location = useLocation()
  return <output aria-label="Adresse">{location.pathname + location.search}</output>
}

function renderPage(entry = '/traceability') {
  return renderWithProviders(
    <>
      <Routes><Route path="/traceability" element={<TraceabilityPage />} /></Routes>
      <Where />
    </>,
    { initialEntries: [entry] },
  )
}

describe('Seite /traceability', () => {
  let api: MockApi
  beforeEach(() => {
    api = installMockApi({
      'GET /reports/charge/:lot': (_req, params) => (decodeURIComponent(params.lot) === 'LOT-A' ? trace() : fail(404, { code: 'not_found' })),
    })
  })
  afterEach(() => api.restore())

  it('ist als Feature-Route der Gruppe Auswertung angemeldet, für jeden Angemeldeten (keine Rollenbindung)', () => {
    const route = featureRoutes.find((r) => r.path === '/traceability')

    expect(route).toBeDefined()
    expect(route).toMatchObject({ label: 'Chargen-Trace', group: 'auswertung' })
    expect(route?.roles).toBeUndefined()
  })

  it('zeigt zu einer bekannten Charge Eingang, Bestand, Bewegungen und betroffene Bestellungen', async () => {
    renderPage('/traceability?lot=LOT-A')

    // Wareneingänge: der gebuchte zählt, der Entwurf steht mit seinem Status da
    const inbound = await screen.findByRole('table', { name: 'Wareneingänge der Charge LOT-A' })
    expect(within(inbound).getByText('WE-1001')).toBeInTheDocument()
    expect(within(inbound).getByText('Received')).toHaveClass('pill--success')
    expect(within(inbound).getByText('Draft')).toBeInTheDocument()
    expect(within(inbound).getAllByText('A-01-01')).toHaveLength(2)

    // aktueller Bestand je Bin, MHD in 3 Tagen -> kritisch
    const stock = screen.getByRole('table', { name: 'Aktueller Bestand der Charge LOT-A' })
    expect(within(stock).getByText('A-01-01')).toBeInTheDocument()
    expect(within(stock).getByText('Kritisch')).toHaveClass('pill--warning')

    // Bewegungen aus dem Ledger: Vorgang übersetzt, Menge mit Vorzeichen, Pickliste verlinkt
    const moves = screen.getByRole('table', { name: 'Bewegungen der Charge LOT-A' })
    expect(within(moves).getAllByText('Wareneingang')).toHaveLength(2) // Vorgang und Beleg
    expect(within(moves).getByText('+10')).toBeInTheDocument()
    expect(within(moves).getByText('-4')).toBeInTheDocument()
    expect(within(moves).getByRole('link', { name: 'Pickliste' })).toHaveAttribute('href', '/picklists/pl1')

    // betroffene Bestellung mit Link, Kunde und Status
    const orders = screen.getByRole('table', { name: 'Bestellungen zur Charge LOT-A' })
    expect(within(orders).getByRole('link', { name: 'ORD-77' })).toHaveAttribute('href', '/orders/o1')
    expect(within(orders).getByText('Kunde Müller')).toBeInTheDocument()
    expect(within(orders).getByText('Shipped')).toHaveClass('pill--success')
    expect(within(orders).getByRole('link', { name: 'PL-0001' })).toHaveAttribute('href', '/picklists/pl1')

    // Kennzahlen: nur gebuchte Eingänge zählen (10, nicht 109)
    expect(screen.getByText('Eingang gebucht').nextElementSibling).toHaveTextContent('10')
    expect(screen.getByText('Aktueller Bestand', { selector: '.stat-tile-label' }).nextElementSibling).toHaveTextContent('6')
    expect(api.calls('GET', '/reports/charge/LOT-A')).toHaveLength(1)
  })

  it('die Suche schreibt die Charge in die Adresse und fragt sie ab; eine unbekannte Charge ist "nicht gefunden", kein Fehler', async () => {
    const user = userEvent.setup()
    renderPage()
    expect(api.requests).toHaveLength(0) // ohne Suchbegriff keine Abfrage
    expect(screen.getByRole('button', { name: 'Suchen' })).toBeDisabled()

    await user.type(screen.getByLabelText('Chargennummer'), 'GIBT-ES-NICHT')
    await user.click(screen.getByRole('button', { name: 'Suchen' }))

    expect(await screen.findByText(/Keine Charge „GIBT-ES-NICHT“ gefunden/)).toBeInTheDocument()
    expect(screen.getByLabelText('Adresse')).toHaveTextContent('/traceability?lot=GIBT-ES-NICHT')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()

    await user.clear(screen.getByLabelText('Chargennummer'))
    await user.type(screen.getByLabelText('Chargennummer'), '  LOT-A ')
    await user.click(screen.getByRole('button', { name: 'Suchen' }))

    expect(await screen.findByRole('table', { name: 'Wareneingänge der Charge LOT-A' })).toBeInTheDocument()
    expect(screen.getByLabelText('Adresse')).toHaveTextContent('/traceability?lot=LOT-A')
  })

  it('eine Charge ohne Bestand und ohne Bestellungen zeigt das, statt leere Tabellen', async () => {
    api.setRoute('GET /reports/charge/:lot', trace({ currentStock: [], currentStockTotal: 0, orders: [], inbounds: [] }))
    renderPage('/traceability?lot=LOT-A')

    expect(await screen.findByText(/Kein Bestand mehr/)).toBeInTheDocument()
    expect(screen.getByText('Keine Bestellung aus dieser Charge ableitbar.')).toBeInTheDocument()
    expect(screen.getByText(/Kein Wareneingang mit dieser Charge erfasst/)).toBeInTheDocument()
  })

  it('ein Serverfehler erscheint als Banner mit "Erneut versuchen"', async () => {
    api.setRoute('GET /reports/charge/:lot', fail(500, { code: 'internal_error' }))
    renderPage('/traceability?lot=LOT-A')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Konnte die Charge nicht laden.')
    expect(within(alert).getByRole('button', { name: 'Erneut versuchen' })).toBeInTheDocument()
  })

  it('eine Chargennummer mit Schrägstrich wird nicht angefragt (der Server kann sie nicht auflösen), sondern erklärt', async () => {
    renderPage('/traceability?lot=2026%2F09')

    expect(await screen.findByRole('alert')).toHaveTextContent('Chargennummern mit „/“')
    expect(api.requests).toHaveLength(0)
  })
})
