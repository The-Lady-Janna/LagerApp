import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { StockItemDto, StockSummaryDto } from '../../api/types'
import { StockPage } from '../../pages/StockPage'
import { makeArticle } from '../helpers/fixtures'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// StockPage: Bestand je Charge/MHD als Unterzeilen mit Status-Pill, Link zur Rückverfolgung und Bestandskorrektur mit Lot/MHD-Auswahl.

/** MHD relativ zu heute (UTC-Kalendertag), damit die Status-Pills nicht am Kalender hängen. */
const inDays = (n: number) => `${new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10)}T00:00:00`
const dateOf = (n: number) => inDays(n).slice(0, 10)

const row = (overrides: Partial<StockItemDto>): StockItemDto => ({
  id: 'st', articleId: 'a1', articleSku: 'ART-001', articleName: 'Schraube M8', storageLocationId: 'b1', storageLocationCode: 'A-01',
  quantity: 1, lotNumber: null, expiryDate: null, ...overrides,
})

const STOCK: StockItemDto[] = [
  row({ id: 'st-b', quantity: 10, lotNumber: 'LOT-B', expiryDate: inDays(60) }),
  row({ id: 'st-a', quantity: 6, lotNumber: 'LOT-A', expiryDate: inDays(-2) }),
  row({ id: 'st-c', quantity: 4, storageLocationId: 'b2', storageLocationCode: 'B-02' }),
  row({ id: 'st-d', articleId: 'a2', articleSku: 'ART-002', articleName: 'Mutter M8', quantity: 3 }),
]
const SUMMARY: StockSummaryDto[] = [
  { articleId: 'a1', articleSku: 'ART-001', articleName: 'Schraube M8', totalQuantity: 20, locationCount: 3 },
  { articleId: 'a2', articleSku: 'ART-002', articleName: 'Mutter M8', totalQuantity: 3, locationCount: 1 },
]

describe('StockPage: Chargen und MHD', () => {
  let api: MockApi
  beforeEach(() => {
    api = installMockApi({
      'GET /stock': STOCK,
      'GET /stock/summary': SUMMARY,
      'GET /stock/alerts': [],
      'GET /articles': [makeArticle({ id: 'a1', sku: 'ART-001', name: 'Schraube M8' }), makeArticle({ id: 'a2', sku: 'ART-002', name: 'Mutter M8' })],
      'GET /warehouse/storage-locations': [{ id: 'b1', code: 'A-01' }, { id: 'b2', code: 'B-02' }],
      'POST /stock/adjust': STOCK[0],
    })
  })
  afterEach(() => api.restore())

  it('zeigt den Bestand je Lot/MHD als Unterzeilen in FEFO-Reihenfolge, mit Status-Pill und Link zur Rückverfolgung', async () => {
    const user = userEvent.setup()
    renderWithProviders(<StockPage />)

    // Übersicht: nächstes MHD des Artikels = das früheste (hier abgelaufen); Artikel ohne Chargen hat keine Unterzeilen
    const summary = await screen.findByRole('table', { name: 'Bestand pro Artikel' })
    const art1 = (await within(summary).findByText('ART-001')).closest('tr')!
    expect(within(art1).getByText('Abgelaufen')).toHaveClass('pill--danger')
    expect(screen.queryByRole('button', { name: /Chargen von ART-002/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('table', { name: /je Charge und MHD/ })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Chargen von ART-001 anzeigen' }))

    const sub = screen.getByRole('table', { name: 'Bestand von ART-001 je Charge und MHD' })
    const rows = within(sub).getAllByRole('row').slice(1) // ohne Kopfzeile
    expect(rows).toHaveLength(3)
    // frühestes MHD zuerst (LOT-A, abgelaufen), dann LOT-B (in 60 Tagen: OK), zuletzt die Zeile ohne Charge/MHD
    expect(rows[0]).toHaveTextContent('LOT-A')
    expect(within(rows[0]).getByText('Abgelaufen')).toHaveClass('pill--danger')
    expect(rows[1]).toHaveTextContent('LOT-B')
    expect(within(rows[1]).getByText('OK')).toHaveClass('pill--success')
    expect(rows[2]).toHaveTextContent('B-02')
    expect(within(rows[2]).queryByText(/Abgelaufen|OK|Kritisch|Bald/)).not.toBeInTheDocument()
    expect(within(rows[0]).getByRole('link', { name: 'LOT-A' })).toHaveAttribute('href', '/traceability?lot=LOT-A')
    expect(within(rows[0]).getByText('6')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Chargen von ART-001 ausblenden' }))
    expect(screen.queryByRole('table', { name: /je Charge und MHD/ })).not.toBeInTheDocument()
  })

  it('"Detail pro Platz" hat eine MHD-Spalte mit Status und verlinkt die Charge', async () => {
    const user = userEvent.setup()
    renderWithProviders(<StockPage />)
    await user.click(await screen.findByRole('button', { name: 'Detail pro Platz' }))

    const detail = await screen.findByRole('table', { name: 'Bestand pro Lagerplatz' })
    expect(within(detail).getByRole('columnheader', { name: 'MHD' })).toBeInTheDocument()
    const lotB = (await within(detail).findByRole('link', { name: 'LOT-B' })).closest('tr')!
    expect(within(lotB).getByText('OK')).toHaveClass('pill--success')
    expect(within(lotB).getByRole('link', { name: 'LOT-B' })).toHaveAttribute('href', '/traceability?lot=LOT-B')
  })

  it('Bestandskorrektur mit Lot/MHD-Auswahl: die Charge einer vorhandenen Zeile wird mit ihrem MHD gebucht', async () => {
    const user = userEvent.setup()
    renderWithProviders(<StockPage />)
    await user.click(await screen.findByRole('button', { name: 'Buchung' }))

    await user.selectOptions(screen.getByLabelText('Artikel'), 'a1')
    await user.selectOptions(screen.getByLabelText('Lagerplatz'), 'b1')
    fireEvent.change(screen.getByLabelText('Delta (+/-)'), { target: { value: '-3' } })

    // angeboten werden die Chargen des Artikels in DIESEM Lagerplatz (mit MHD und Menge), FEFO; die Zeile in B-02 nicht
    const select = screen.getByLabelText('Charge / MHD')
    const options = within(select).getAllByRole('option').map((o) => o.textContent)
    expect(options).toHaveLength(3)
    expect(options[0]).toContain('Automatisch')
    expect(options[1]).toMatch(/^LOT-A · MHD \d{2}\.\d{2}\.\d{4} · 6 Stk$/)
    expect(options[2]).toMatch(/^LOT-B · MHD \d{2}\.\d{2}\.\d{4} · 10 Stk$/)
    expect(within(select).queryByRole('option', { name: /Neue Charge/ })).not.toBeInTheDocument() // Abgang: keine neue Charge

    await user.selectOptions(select, 'st-b')
    await user.click(screen.getByRole('button', { name: 'Buchen' }))

    await waitFor(() => expect(api.calls('POST', '/stock/adjust')).toHaveLength(1))
    expect(api.calls('POST', '/stock/adjust')[0].body).toEqual({
      articleId: 'a1', storageLocationId: 'b1', delta: -3, lotNumber: 'LOT-B', expiryDate: dateOf(60),
    })
  })

  it('ohne Auswahl geht die Korrektur ohne Charge raus; "Neue Charge" braucht beim Zugang eine Nummer und schickt Charge + MHD', async () => {
    const user = userEvent.setup()
    renderWithProviders(<StockPage />)
    await user.click(await screen.findByRole('button', { name: 'Buchung' }))
    await user.selectOptions(screen.getByLabelText('Artikel'), 'a2')
    await user.selectOptions(screen.getByLabelText('Lagerplatz'), 'b1')

    fireEvent.change(screen.getByLabelText('Delta (+/-)'), { target: { value: '2' } })
    await user.click(screen.getByRole('button', { name: 'Buchen' }))
    await waitFor(() => expect(api.calls('POST', '/stock/adjust')).toHaveLength(1))
    expect(api.calls('POST', '/stock/adjust')[0].body).toEqual({ articleId: 'a2', storageLocationId: 'b1', delta: 2, lotNumber: null, expiryDate: null })

    // neue Charge: ohne Nummer gesperrt, mit Nummer und MHD gebucht
    fireEvent.change(screen.getByLabelText('Delta (+/-)'), { target: { value: '5' } })
    await user.selectOptions(screen.getByLabelText('Charge / MHD'), 'Neue Charge …')
    expect(screen.getByRole('button', { name: 'Buchen' })).toBeDisabled()
    await user.type(screen.getByLabelText('Neue Chargennummer'), ' LOT-NEU ')
    fireEvent.change(screen.getByLabelText('MHD (optional)'), { target: { value: '2027-03-31' } })
    expect(screen.getByRole('button', { name: 'Buchen' })).toBeEnabled()
    await user.click(screen.getByRole('button', { name: 'Buchen' }))

    await waitFor(() => expect(api.calls('POST', '/stock/adjust')).toHaveLength(2))
    expect(api.calls('POST', '/stock/adjust')[1].body).toEqual({
      articleId: 'a2', storageLocationId: 'b1', delta: 5, lotNumber: 'LOT-NEU', expiryDate: '2027-03-31',
    })
  })

  it('ein abgelehnte Buchung (Charge mit anderem MHD) steht als Meldung da', async () => {
    api.setRoute('POST /stock/adjust', fail(409, {
      title: 'Conflict', status: 409, code: 'lot_expiry_mismatch', detail: 'Charge LOT-A ist mit MHD 2026-01-01 geführt - eine Charge hat genau ein MHD',
    }))
    const user = userEvent.setup()
    renderWithProviders(<StockPage />)
    await user.click(await screen.findByRole('button', { name: 'Buchung' }))
    await user.selectOptions(screen.getByLabelText('Artikel'), 'a1')
    await user.selectOptions(screen.getByLabelText('Lagerplatz'), 'b1')
    fireEvent.change(screen.getByLabelText('Delta (+/-)'), { target: { value: '1' } })
    await user.selectOptions(screen.getByLabelText('Charge / MHD'), 'st-a')

    await user.click(screen.getByRole('button', { name: 'Buchen' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Buchung fehlgeschlagen:')
    expect(alert).toHaveTextContent('eine Charge hat genau ein MHD')
  })
})
