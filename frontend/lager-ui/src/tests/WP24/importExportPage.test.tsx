import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { configure, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ImportExportPage } from '../../features/importexport/ImportExportPage'
import { saveBlob } from '../../lib/download'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// Die Seiten-Tests tippen und suchen per Rolle: bei voll ausgelasteter Maschine brauchen sie länger als die Standardfristen.
configure({ asyncUtilTimeout: 5000 })
vi.setConfig({ testTimeout: 30_000 })

// Der Download geht über saveBlob (temporärer Link); im Test fangen wir Aufruf und Dateinamen ab.
vi.mock('../../lib/download', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../lib/download')>()),
  saveBlob: vi.fn(),
}))

const CSV = 'Sku;Name\r\nA-1;Schraube\r\n'

describe('Seite Import & Export: Export', () => {
  let api: MockApi

  beforeEach(() => {
    vi.mocked(saveBlob).mockClear()
    api = installMockApi({
      'GET /export/articles.csv': CSV,
      'GET /export/stock.csv': CSV,
      'GET /export/orders.csv': CSV,
      'GET /export/movements.csv': CSV,
      'GET /export/audit.csv': CSV,
    })
  })
  afterEach(() => api.restore())

  it('bietet jeden Export als Download an und lädt mit dem Login-Token (Datei mit UTC-Zeit im Namen)', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportExportPage />)

    for (const name of ['Artikel', 'Bestand', 'Bestellungen', 'Bewegungen (Ledger)', 'Audit']) {
      expect(screen.getByRole('region', { name: `Export ${name}` })).toBeInTheDocument()
    }
    await user.click(screen.getByRole('button', { name: 'Artikel als CSV herunterladen' }))

    expect(await screen.findByText(/^articles-\d{8}T\d{6}Z\.csv$/)).toBeInTheDocument()
    expect(api.calls('GET', '/export/articles.csv')).toHaveLength(1)
    expect(api.calls('GET', '/export/articles.csv')[0].query).toBe('delimiter=semicolon')
    expect(saveBlob).toHaveBeenCalledTimes(1)
    expect(vi.mocked(saveBlob).mock.calls[0][1]).toMatch(/^articles-\d{8}T\d{6}Z\.csv$/)
  })

  it('schickt das gewählte Trennzeichen mit', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportExportPage />)

    await user.selectOptions(screen.getByLabelText('Trennzeichen'), 'comma')
    await user.click(screen.getByRole('button', { name: 'Bestand als CSV herunterladen' }))

    await screen.findByText(/^stock-\d{8}T\d{6}Z\.csv$/)
    expect(api.calls('GET', '/export/stock.csv')[0].query).toBe('delimiter=comma')
  })

  it('filtert Bewegungen nach Zeitraum und Audit zusätzlich nach Benutzer; leere Filter entfallen', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportExportPage />)
    const movements = screen.getByRole('region', { name: 'Export Bewegungen (Ledger)' })
    const audit = screen.getByRole('region', { name: 'Export Audit' })

    // ohne Filter keine von/bis-Parameter
    await user.click(within(movements).getByRole('button', { name: 'Bewegungen (Ledger) als CSV herunterladen' }))
    await screen.findByText(/^movements-/)
    expect(api.calls('GET', '/export/movements.csv')[0].query).toBe('delimiter=semicolon')

    // Zeitraum und Benutzer (die Felder von/bis gelten für beide Karten)
    await user.type(within(movements).getByLabelText('Von'), '2026-09-01')
    await user.type(within(movements).getByLabelText('Bis (einschließlich)'), '2026-09-30')
    await user.type(within(audit).getByLabelText('Benutzer (leer = alle)'), ' admin ')
    await user.click(within(audit).getByRole('button', { name: 'Audit als CSV herunterladen' }))

    await screen.findByText(/^audit-/)
    expect(api.calls('GET', '/export/audit.csv')[0].query).toBe('delimiter=semicolon&from=2026-09-01&to=2026-09-30&user=admin')
  })

  it('sperrt den Export eines verkehrten Zeitraums und sagt warum', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportExportPage />)
    const movements = screen.getByRole('region', { name: 'Export Bewegungen (Ledger)' })

    await user.type(within(movements).getByLabelText('Von'), '2026-10-01')
    await user.type(within(movements).getByLabelText('Bis (einschließlich)'), '2026-09-01')

    expect(within(movements).getByRole('alert')).toHaveTextContent('„Von“ muss vor „Bis“ liegen.')
    expect(within(movements).getByRole('button', { name: 'Bewegungen (Ledger) als CSV herunterladen' })).toBeDisabled()
  })

  it('meldet einen fehlgeschlagenen Export verständlich und lässt ihn erneut zu', async () => {
    api.setRoute('GET /export/orders.csv', () => fail(403))
    const user = userEvent.setup()
    renderWithProviders(<ImportExportPage />)

    await user.click(screen.getByRole('button', { name: 'Bestellungen als CSV herunterladen' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Export fehlgeschlagen:')
    expect(alert).toHaveTextContent('Keine Berechtigung für diese Datei.')
    expect(saveBlob).not.toHaveBeenCalled()

    api.setRoute('GET /export/orders.csv', CSV)
    await user.click(screen.getByRole('button', { name: 'Bestellungen als CSV herunterladen' }))
    await screen.findByText(/^orders-\d{8}T\d{6}Z\.csv$/)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('Seite Import & Export: Import', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi()
  })
  afterEach(() => api.restore())

  it('öffnet für jede Art den Import-Dialog mit den Pflichtspalten und schließt ihn wieder', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportExportPage />)

    for (const [label, required] of [
      ['Artikel', 'Sku; für neue Artikel zusätzlich Name'],
      ['Bestand', 'Sku, Location (Lagerplatz), Quantity'],
      ['Bestellungen', 'OrderNumber, Sku, Quantity'],
    ] as const) {
      await user.click(screen.getByRole('button', { name: `${label} importieren` }))
      const dialog = screen.getByRole('dialog', { name: `${label} importieren` })
      expect(dialog).toHaveTextContent(`Pflichtspalten: ${required}`)
      await user.click(within(dialog).getByRole('button', { name: 'Abbrechen' }))
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    }
    expect(api.requests).toHaveLength(0)    // öffnen und schließen schreibt und liest nichts
  })

  it('gibt die Trennzeichen-Wahl der Seite an den Dialog weiter', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportExportPage />)

    await user.selectOptions(screen.getByLabelText('Trennzeichen'), 'comma')
    await user.click(screen.getByRole('button', { name: 'Artikel importieren' }))

    expect(within(screen.getByRole('dialog')).getByLabelText('Trennzeichen')).toHaveValue('comma')
  })
})
