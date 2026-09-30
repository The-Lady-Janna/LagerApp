import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { InboundShipmentDto, OrderDto, ShipmentDto } from '../../api/types'
import type { BackupFileDto } from '../../api/systemHooks'
import App from '../../App'
import { ArticlesPage } from '../../pages/ArticlesPage'
import { InboundPage } from '../../pages/InboundPage'
import { OrdersPage } from '../../pages/OrdersPage'
import { PickListsPage } from '../../pages/PickListsPage'
import { ReportsPage } from '../../pages/ReportsPage'
import { ShipmentsPage } from '../../pages/ShipmentsPage'
import { StockPage } from '../../pages/StockPage'
import { SystemPage } from '../../features/system/SystemPage'
import { setLanguage } from '../../i18n'
import { useAuth } from '../../state/auth'
import { makeArticle, makePickList } from '../helpers/fixtures'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'
import { makeBackup, makeSettings } from '../WP25/fixtures'

vi.setConfig({ testTimeout: 30_000 })

// WP29, Akzeptanz: Mit Sprache "en" zeigt keine der Hauptseiten (Artikel, Bestellungen, Bestand, Wareneingang, Picklisten, Versand,
// Berichte, System, Login) deutschen Text. Die Testdaten sind englisch, deutsche Wörter können also nur aus der Oberfläche stammen.
// Geprüft werden Text, aria-label, placeholder, title und alt der gerenderten Seite gegen eine Stichwortliste.

const GERMAN_WORDS = [
  'Artikel', 'Bestellung', 'Bestellungen', 'Bestand', 'Bestände', 'Lieferant', 'Lieferanten', 'Kunde', 'Kunden', 'Speichern', 'Speichere',
  'Abbrechen', 'Löschen', 'Bearbeiten', 'Anlegen', 'Lagerplatz', 'Lagerplätze', 'Wareneingang', 'Wareneingänge', 'Pickliste', 'Picklisten',
  'Versand', 'Sendung', 'Sendungen', 'Zeitraum', 'Erstellt', 'Menge', 'Nummer', 'Aktionen', 'Lade', 'Noch', 'Keine', 'Kein', 'Fehler',
  'Benutzer', 'Passwort', 'Anmelden', 'Abmelden', 'Bitte', 'Zurück', 'Öffnen', 'Hinzufügen', 'Entfernen', 'Sicherung', 'Einstellungen',
  'Herunterladen', 'nicht', 'und', 'oder', 'für', 'wird', 'der', 'die', 'das', 'mit', 'Neu', 'Neue', 'Neuer', 'Positionen', 'Gewicht',
  'Größe', 'Datei', 'Seite', 'Zeile', 'Suche', 'Erststart', 'Einmalpasswort', 'Seitenleiste', 'Hauptnavigation', 'Abgelaufen',
  'Kritisch', 'Bereit', 'Gesamt', 'Stück', 'Charge', 'Chargen', 'Zuweisen', 'Empfänger', 'Empfangen',
]
const GERMAN = new RegExp(`(?<![A-Za-zÄÖÜäöüß])(${GERMAN_WORDS.join('|')})(?![A-Za-zÄÖÜäöüß])`, 'g')

/** Alle deutschen Stichwörter im sichtbaren Text und in den Attributen, die Screenreader/Tooltips zeigen. */
function germanLeftovers(root: HTMLElement): string[] {
  const texts: string[] = [root.textContent ?? '']
  for (const el of root.querySelectorAll('*')) {
    for (const attr of ['aria-label', 'placeholder', 'title', 'alt']) {
      const value = el.getAttribute(attr)
      if (value) texts.push(value)
    }
  }
  return [...new Set(texts.flatMap((text) => [...text.matchAll(GERMAN)].map((m) => m[1])))]
}

const expectNoGerman = (root: HTMLElement) => expect(germanLeftovers(root)).toEqual([])

const order = (overrides: Partial<OrderDto>): OrderDto => ({
  id: 'o1', orderNumber: 'ORD-1', customerReference: 'Acme', status: 'New', source: 'Manual', createdAt: '2026-01-05T10:00:00',
  lines: [{ id: 'l1', articleId: 'a1', articleSku: 'W-1', quantity: 2 }], hasStockNow: true, hasStockAfterFifo: true, ...overrides,
})

const widget = makeArticle({ id: 'a1', sku: 'W-1', name: 'Widget' })

describe('Hauptseiten in englischer Sprache', () => {
  let api: MockApi

  beforeEach(async () => {
    signInAs(['Admin'])
    await act(() => setLanguage('en'))
  })
  afterEach(() => {
    api?.restore()
    useAuth.getState().logout()
  })

  it('Sanity: die Stichwortliste erkennt deutschen Text', () => {
    const div = document.createElement('div')
    div.innerHTML = '<p>Noch keine Artikel angelegt.</p><button aria-label="Zurück">x</button>'
    expect(germanLeftovers(div)).toEqual(['Noch', 'Artikel', 'Zurück'])
  })

  it('Login', async () => {
    useAuth.getState().logout()
    api = installMockApi({})
    const { container } = renderWithProviders(<App />)
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByText('Username')).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('App-Shell (Seitenleiste) mit Artikelliste', async () => {
    api = installMockApi({
      'GET /auth/me': { id: 'u-test', username: 'tester', email: null, displayName: null, roles: ['Admin'], isActive: true, mustChangePassword: false },
      'GET /warehouse/layout': [],
      'GET /articles': [widget, makeArticle({ id: 'a2', sku: 'B-1', name: 'Combo', isBundle: true, bundleComponents: [{ componentArticleId: 'a1', componentSku: 'W-1', quantity: 2 }], validFrom: '2026-03-01T00:00:00', validUntil: '2026-09-30T00:00:00' })],
    })
    const { container } = renderWithProviders(<App />, { initialEntries: ['/articles'] })
    expect(await screen.findByRole('heading', { name: 'Articles' })).toBeInTheDocument()
    expect(await screen.findByText('Combo')).toBeInTheDocument()
    const nav = screen.getByRole('navigation', { name: 'Main navigation' })
    expect(within(nav).getByRole('link', { name: 'Goods receipt' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Users' })).toBeInTheDocument()
    expect(screen.getByRole('group', { name: 'Language' })).toBeInTheDocument()
    expect(screen.getByText('Logged in as')).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('Artikel (Liste, Leerzustand und Filter)', async () => {
    api = installMockApi({ 'GET /articles': [] })
    const { container } = renderWithProviders(<ArticlesPage />)
    expect(await screen.findByText('No articles yet.')).toBeInTheDocument()
    expect(screen.getByRole('searchbox', { name: 'Search articles' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Out of season (inactive)' })).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('Bestellungen', async () => {
    api = installMockApi({
      'GET /orders': [order({}), order({ id: 'o2', orderNumber: 'ORD-2', status: 'New', priority: 2, hasStockNow: false, hasStockAfterFifo: false, dueDate: '2020-01-01T00:00:00' }), order({ id: 'o3', orderNumber: 'ORD-3', status: 'Picking' }), order({ id: 'o4', orderNumber: 'ORD-4', status: 'Shipped' })],
      'GET /articles': [widget],
      'GET /customers': [],
      'GET /cart-configs': [],
    })
    const { container } = renderWithProviders(<OrdersPage />)
    expect(await screen.findByText('ORD-2')).toBeInTheDocument()
    // Text mit Link (Trans): "Jetzt einrichten" verweist auf die Wagen-Konfiguration
    expect(screen.getByRole('link', { name: 'Set one up now' })).toHaveAttribute('href', '/cart-configs')
    const table = screen.getByRole('table', { name: 'Orders' })
    expect(within(table).getByText('Picking')).toBeInTheDocument()
    expect(within(table).getByText('High')).toBeInTheDocument()
    expect(within(table).getByText(/\(overdue\)/)).toBeInTheDocument()
    expect(within(table).getByText('no stock')).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('Bestand (Alarme, Chargen, MHD)', async () => {
    const inDays = (n: number) => `${new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10)}T00:00:00`
    api = installMockApi({
      'GET /stock': [
        { id: 'st1', articleId: 'a1', articleSku: 'W-1', articleName: 'Widget', storageLocationId: 'b1', storageLocationCode: 'A-01', quantity: 10, lotNumber: 'LOT-A', expiryDate: inDays(3) },
      ],
      'GET /stock/summary': [{ articleId: 'a1', articleSku: 'W-1', articleName: 'Widget', totalQuantity: 10, locationCount: 1 }],
      'GET /stock/alerts': [{ articleId: 'a1', articleSku: 'W-1', articleName: 'Widget', totalQuantity: 1, minStock: 5, reorderPoint: 10, maxStock: 100, severity: 'critical' }],
      'GET /articles': [widget],
      'GET /warehouse/storage-locations': [{ id: 'b1', code: 'A-01' }],
    })
    const user = userEvent.setup()
    const { container } = renderWithProviders(<StockPage />)
    expect(await screen.findByText(/Critical — below min stock/)).toBeInTheDocument()
    await user.click(await screen.findByRole('button', { name: 'Show lots of W-1' }))
    expect((await screen.findAllByText('Critical')).length).toBeGreaterThan(0)
    await user.click(screen.getByRole('button', { name: 'Stock booking' }))
    expect(await screen.findByText('Delta (+/-)')).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('Wareneingang (Liste mit Positionen und Formular)', async () => {
    const shipment: InboundShipmentDto = {
      id: 'in1', shipmentNumber: 'WE-1', supplierReference: 'Acme', notes: null, status: 'Draft', createdAt: '2026-01-05T10:00:00', receivedAt: null,
      lines: [{ id: 'il1', articleId: 'a1', articleSku: 'W-1', targetBinId: 'b1', targetBinCode: 'A-01', quantity: 5, lotNumber: 'LOT-1', expiryDate: '2030-01-01T00:00:00' }],
    }
    api = installMockApi({
      'GET /inbound': [shipment],
      'GET /articles': [widget],
      'GET /warehouse/storage-locations': [{ id: 'b1', code: 'A-01' }],
      'GET /stock': [],
      'GET /putaway/suggestions': [],
    })
    const user = userEvent.setup()
    const { container } = renderWithProviders(<InboundPage />)
    expect(await screen.findByText('WE-1')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Details' }))
    await user.click(screen.getByRole('button', { name: '+ New goods receipt' }))
    await user.selectOptions(screen.getByLabelText('Article line 1'), 'a1')
    await user.click(screen.getByRole('button', { name: 'Create (draft)' }))
    expect(await screen.findByText('Please correct:')).toBeInTheDocument()
    expect(screen.getByText('Line 1: choose a target bin.')).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('Picklisten', async () => {
    api = installMockApi({
      'GET /picklists': [makePickList({ id: 'p1', pickListNumber: 'PL-1', status: 'InProgress' }), makePickList({ id: 'p2', pickListNumber: 'PL-2', status: 'Completed' })],
    })
    const { container } = renderWithProviders(<PickListsPage />)
    expect(await screen.findByText('PL-1')).toBeInTheDocument()
    expect(screen.getByText('In progress')).toBeInTheDocument()
    expect(screen.getByText('Packed')).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('Versand (Sendungen und Formular)', async () => {
    const shipment = (overrides: Partial<ShipmentDto>): ShipmentDto => ({
      id: 's1', shipmentNumber: 'SH-1', orderId: 'o1', orderNumber: 'ORD-1', pickListId: null, carrierCode: 'MANUAL', trackingNumber: null, trackingUrl: null,
      weightGrams: 1500, lengthMm: 300, widthMm: 200, heightMm: 100, costCents: 0, notes: null, status: 'Ready', createdAt: '2026-01-05T10:00:00',
      labeledAt: null, shippedAt: null, deliveredAt: null, ...overrides,
    })
    api = installMockApi({
      'GET /shipments': [shipment({}), shipment({ id: 's2', shipmentNumber: 'SH-2', status: 'Labeled', trackingNumber: 'T-1' })],
      'GET /shipments/carriers': [{ code: 'MANUAL', displayName: 'Manual', isConfigured: true, note: null }, { code: 'DHL', displayName: 'DHL', isConfigured: false, note: null }],
      'GET /orders': [order({ status: 'Packed' })],
    })
    const user = userEvent.setup()
    const { container } = renderWithProviders(<ShipmentsPage />)
    expect(await screen.findByText('SH-1')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Tracking no.' }))
    await user.click(screen.getByRole('button', { name: '+ New parcel' }))
    expect(await screen.findByText('Order (packed only)')).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'DHL – not available' })).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('Berichte (Kennzahlen aus dem deutschen Server-Text werden übersetzt)', async () => {
    api = installMockApi({
      'GET /reports/dashboard': {
        rangeDays: 7, from: '2026-01-01T00:00:00', to: '2026-01-08T00:00:00',
        headline: [
          { label: 'Bestellungen (Zeitraum)', value: '5', unit: null, hint: 'seit 01.01.2026' },
          { label: 'Picklisten (Zeitraum)', value: '3', unit: null, hint: '2 fertig' },
          { label: 'Ø Pickdistanz', value: '12.5', unit: 'm', hint: 'über abgeschlossene Picklisten' },
        ],
        topArticles: [], topBins: [], orderStatusBreakdown: [{ status: 'New', count: 4 }],
        pickListsPerDay: [{ date: '2026-01-01', count: 2 }, { date: '2026-01-02', count: 5 }], ordersPerDay: [],
      },
      'GET /reports/live-status': { ordersOpen: 1, ordersInPicking: 2, ordersPicked: 0, ordersPacked: 0, picklistsPending: 0, picklistsInProgress: 0, picklistsPickedReadyToPack: 1, picklistsCompletedToday: 0, inventoryCountsOpen: 0, replenishmentTasksOpen: 0, stockAlertsCritical: 1, stockAlertsWarning: 0 },
      'GET /reports/dead-stock': [],
      'GET /reports/abc-analysis': [],
      'GET /reports/bin-heatmap': [],
      'GET /reports/stock-valuation': { at: '2026-01-08T10:00:00', articleCount: 3, totalQuantity: 1234, totalValueCents: 123450, currency: 'EUR', lines: [], fallbackValueCents: 0 },
      'GET /reports/slotting-suggestions': [],
      'GET /reports/picker-performance': { rows: [] },
      'GET /reports/expiring': [],
    })
    const { container } = renderWithProviders(<ReportsPage />)
    expect(await screen.findByText('Orders (period)')).toBeInTheDocument()
    expect(screen.getByText('since 01/01/2026')).toBeInTheDocument()
    expect(screen.getByText('2 completed')).toBeInTheDocument()
    expect(screen.getByText('over completed pick lists')).toBeInTheDocument()
    expect(screen.getByText('1,234.50 EUR')).toBeInTheDocument()
    expect(await screen.findByText('No expired or soon-to-expire lots in the next 30 days. 🎉')).toBeInTheDocument()
    expectNoGerman(container)
  })

  it('System (Backup und Restore)', async () => {
    const files: BackupFileDto[] = [makeBackup('b-1.db'), makeBackup('before-1.db', { kind: 'before-restore' })]
    api = installMockApi({ 'GET /admin/backup-settings': makeSettings(), 'GET /admin/backups': files })
    const { container } = renderWithProviders(<SystemPage />)
    expect(await screen.findByText('b-1.db')).toBeInTheDocument()
    expect(screen.getByText('Before restore')).toBeInTheDocument()
    expect(screen.getByText('daily 02:00 UTC')).toBeInTheDocument()
    expect(screen.getByText('the last 14 backups')).toBeInTheDocument()
    expect(screen.getAllByText('1.5 KB')).toHaveLength(2)
    expectNoGerman(container)
  })
})
