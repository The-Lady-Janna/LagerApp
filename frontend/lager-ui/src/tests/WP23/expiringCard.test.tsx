import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ExpiringStockDto } from '../../api/lotHooks'
import { ExpiringStockCard } from '../../features/traceability/ExpiringStockCard'
import { ReportsPage } from '../../pages/ReportsPage'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// Karte "Ablaufende Chargen" (MHD-Warnliste): Frist-Filter 7/30/90, Status, Tage, Link zur Rückverfolgung.

const dto = (overrides: Partial<ExpiringStockDto>): ExpiringStockDto => ({
  stockItemId: 'st', articleId: 'a1', articleSku: 'ART-001', articleName: 'Joghurt', binId: 'b1', binCode: 'A-01', lotNumber: 'LOT-A',
  quantity: 4, expiryDate: '2026-06-10T00:00:00', daysUntilExpiry: -5, status: 'Expired', ...overrides,
})

const WARNINGS: ExpiringStockDto[] = [
  dto({ stockItemId: 'st1' }),
  dto({ stockItemId: 'st2', articleSku: 'ART-002', articleName: 'Milch', lotNumber: 'LOT-B', binCode: 'B-02', quantity: 12, expiryDate: '2026-06-18T00:00:00', daysUntilExpiry: 3, status: 'Critical' }),
  dto({ stockItemId: 'st3', articleSku: 'ART-003', articleName: 'Käse', lotNumber: null, binCode: 'C-03', quantity: 1, expiryDate: '2026-07-10T00:00:00', daysUntilExpiry: 25, status: 'Soon' }),
]

describe('ExpiringStockCard', () => {
  let api: MockApi
  beforeEach(() => {
    api = installMockApi({
      'GET /reports/expiring': (req) => (req.query === 'days=7' ? WARNINGS.slice(0, 2) : req.query === 'days=90' ? [] : WARNINGS),
    })
  })
  afterEach(() => api.restore())

  it('listet abgelaufene und bald ablaufende Chargen mit Status, Bin, Menge, Tagen und Link zur Rückverfolgung (Standardfrist 30 Tage)', async () => {
    renderWithProviders(<ExpiringStockCard />)

    const table = await screen.findByRole('table', { name: 'Abgelaufene und bald ablaufende Chargen' })
    expect(api.requests[0].query).toBe('days=30')
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(3)

    // sortiert wie geliefert (nach MHD): abgelaufen, kritisch, bald - mit Farbe, Bin, Menge und Tagen
    expect(within(rows[0]).getByText('Abgelaufen')).toHaveClass('pill--danger')
    expect(rows[0]).toHaveTextContent('ART-001')
    expect(rows[0]).toHaveTextContent('seit 5 Tagen abgelaufen')
    expect(rows[0]).toHaveTextContent('10.06.2026')
    expect(within(rows[0]).getByRole('link', { name: 'LOT-A' })).toHaveAttribute('href', '/traceability?lot=LOT-A')
    expect(within(rows[1]).getByText('Kritisch')).toHaveClass('pill--warning')
    expect(rows[1]).toHaveTextContent('B-02')
    expect(rows[1]).toHaveTextContent('noch 3 Tage')
    expect(within(rows[1]).getByText('12')).toBeInTheDocument()
    expect(within(rows[2]).getByText('Bald')).toHaveClass('pill--info')
    expect(within(rows[2]).queryByRole('link')).not.toBeInTheDocument() // ohne Charge kein Link

    expect(screen.getAllByRole('status').map((e) => e.textContent).join(' ')).toContain('3 Bestandszeile(n): 1 abgelaufen, 1 kritisch, 1 bald')
  })

  it('der Tage-Filter (7/30/90) lädt die Liste für die gewählte Frist neu; keine Treffer heißt: alles in Ordnung', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ExpiringStockCard />)
    await screen.findByRole('table')
    expect(screen.getByRole('button', { name: '30 Tage' })).toHaveAttribute('aria-pressed', 'true')

    await user.click(screen.getByRole('button', { name: '7 Tage' }))
    await waitFor(() => expect(within(screen.getByRole('table')).getAllByRole('row')).toHaveLength(3)) // Kopf + 2 Zeilen
    expect(api.requests.map((r) => r.query)).toEqual(['days=30', 'days=7'])
    expect(screen.getByRole('button', { name: '7 Tage' })).toHaveAttribute('aria-pressed', 'true')

    await user.click(screen.getByRole('button', { name: '90 Tage' }))
    expect(await screen.findByText(/Keine abgelaufenen oder bald ablaufenden Chargen in den nächsten 90 Tagen/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('ein Ladefehler erscheint als Banner mit "Erneut versuchen"', async () => {
    api.setRoute('GET /reports/expiring', fail(500, { code: 'internal_error' }))
    renderWithProviders(<ExpiringStockCard />)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Konnte die MHD-Warnliste nicht laden.')
    expect(within(alert).getByRole('button', { name: 'Erneut versuchen' })).toBeInTheDocument()
  })

  it('die Reports-Seite enthält die Karte "Ablaufende Chargen"', async () => {
    renderWithProviders(<ReportsPage />)

    expect(await screen.findByRole('heading', { name: 'Ablaufende Chargen' })).toBeInTheDocument()
    expect(await screen.findByRole('table', { name: 'Abgelaufene und bald ablaufende Chargen' })).toBeInTheDocument()
  })
})
