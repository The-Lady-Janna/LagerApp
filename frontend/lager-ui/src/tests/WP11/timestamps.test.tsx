import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { PurchaseOrdersPage } from '../../pages/PurchaseOrdersPage'
import { ReturnsPage } from '../../pages/ReturnsPage'
import { formatDate, formatDateTime } from '../../lib/format'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { makePurchaseOrder, makeReturn } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'

// frontend#21: Die Seiten zeigten Zeitpunkte per new Date(x).toLocaleString(). Der Server liefert UTC ohne "Z"; der Browser
// las das als Ortszeit (1-2 h zu früh) und formatierte je nach Browser-Sprache uneinheitlich.
describe('Zeitstempel in den Seiten', () => {
  let api: MockApi
  beforeEach(() => {
    api = installMockApi({
      'GET /articles': [],
      'GET /warehouse/storage-locations': [],
      'GET /suppliers': [],
      'GET /purchase-orders/suggestions': [],
    })
  })
  afterEach(() => api.restore())

  it('Retouren: Zeitpunkte ohne Zone werden als UTC gelesen und als de-DE ausgegeben', async () => {
    api.setRoute('GET /returns', [makeReturn({ createdAt: '2025-01-01T10:00:00', processedAt: '2025-01-02T08:30:00Z' })])
    renderWithProviders(<ReturnsPage />)

    // Dieselbe Ortszeit wie bei expliziter UTC-Schreibweise …
    expect(await screen.findByText(formatDateTime('2025-01-01T10:00:00Z'))).toBeInTheDocument()
    expect(screen.getByText(formatDateTime('2025-01-02T08:30:00Z'))).toBeInTheDocument()
    // … und im deutschen Format (nicht "1/1/2025, 10:00:00 AM").
    expect(screen.getAllByText(/^\d{2}\.\d{2}\.\d{4}, \d{2}:\d{2}$/, { selector: 'td.muted' })).toHaveLength(2)
  })

  it('Retouren: nicht verarbeitete Retoure zeigt "—" statt "Invalid Date"', async () => {
    api.setRoute('GET /returns', [makeReturn({ processedAt: null })])
    renderWithProviders(<ReturnsPage />)

    expect(await screen.findByText('—')).toBeInTheDocument()
    expect(screen.queryByText(/Invalid Date/)).not.toBeInTheDocument()
  })

  it('Bestellungen: Erstellzeitpunkt in Ortszeit, erwartetes Lieferdatum als Kalendertag ohne Verschiebung, Betrag mit Komma', async () => {
    api.setRoute('GET /purchase-orders', [makePurchaseOrder()])
    renderWithProviders(<PurchaseOrdersPage />)

    expect(await screen.findByText('PO-0001')).toBeInTheDocument()
    expect(screen.getByText(formatDate('2025-01-01T23:30:00Z'))).toBeInTheDocument()
    expect(screen.getByText('25.05.2026')).toBeInTheDocument() // Mitternacht ohne Zone → derselbe Tag in jeder Zeitzone
    expect(screen.getByText(/1\.234,56\s€/)).toBeInTheDocument()
  })
})
