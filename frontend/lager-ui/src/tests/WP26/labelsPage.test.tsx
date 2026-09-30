import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { LabelsPage } from '../../features/labels/LabelsPage'
import { encodeCode128 } from '../../features/labels/code128'
import routes from '../../features/labels/route'
import { collectFeatureRoutes, RESERVED_PATHS } from '../../routes/registry'
import { buildNavGroups, STATIC_NAV_GROUPS, visibleNavGroups } from '../../routes/navigation'
import { hasRequiredRole, type RoleName } from '../../lib/roles'
import { saveBlob } from '../../lib/download'
import type { OrderDto } from '../../api/types'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makeArticle } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'
import { makeLayout } from './labelFixtures'

// Der Download geht über saveBlob (temporärer Link); im Test fangen wir Blob und Dateinamen ab.
vi.mock('../../lib/download', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../lib/download')>()),
  saveBlob: vi.fn(),
}))

const readBlob = (blob: Blob) =>
  new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result))
    reader.onerror = () => reject(reader.error)
    reader.readAsText(blob)
  })

const makeOrder = (overrides: Partial<OrderDto> = {}): OrderDto => ({
  id: 'o1', orderNumber: 'ORD-DEMO-01', customerReference: 'K-4711', status: 'New', source: 'Manual',
  createdAt: '2025-01-01T10:00:00', lines: [], hasStockNow: true, hasStockAfterFifo: true, ...overrides,
})

/** Diese Tests rendern hunderte Etiketten in jsdom: großzügige Frist, damit sie auch bei ausgelastetem Rechner nicht wackeln. */
const HEAVY_TEST_TIMEOUT = 30_000

const zpl = (code: string) => `^XA\n^FD${code}^FS\n^XZ\n`

/** Die Etiketten der Vorschau in Anzeigereihenfolge (Code je Etikett). */
const previewCodes = () => [...document.querySelectorAll<HTMLElement>('.label-cell')].map((cell) => cell.dataset.code)

describe('Seite Etiketten', () => {
  let api: MockApi

  beforeEach(() => {
    vi.mocked(saveBlob).mockClear()   // restoreMocks setzt nur Spies zurück, nicht die Aufrufe eines vi.fn() aus der Modul-Fabrik
    api = installMockApi({
      'GET /warehouse/layout': makeLayout(),
      'GET /articles': [
        makeArticle({ id: 'a1', sku: 'ART-001', name: 'Schraube M8' }),
        makeArticle({ id: 'a2', sku: 'ART-002', name: 'Mutter M8' }),
        makeArticle({ id: 'a3', sku: 'Größe-3', name: 'Sonderteil' }),
      ],
      'GET /orders': [
        makeOrder(),
        makeOrder({ id: 'o2', orderNumber: 'ORD-DEMO-02', customerReference: null, status: 'Packed' }),
      ],
      'GET /labels/bin/b1.zpl': zpl('A-01-1'),
      'GET /labels/bin/b2.zpl': zpl('A-01-2'),
      'GET /labels/bin/b10.zpl': zpl('A-01-10'),
      'GET /labels/order/o1.zpl': zpl('ORD-DEMO-01'),
    })
  })
  afterEach(() => api.restore())

  async function pickShelfS1(user: ReturnType<typeof userEvent.setup>) {
    await user.selectOptions(await screen.findByRole('combobox', { name: /^Regal/ }), 's1')
    await user.click(screen.getByRole('button', { name: /^Alle Treffer auswählen/ }))
  }

  it('Sammeldruck: alle Lagerplätze eines Regals in einem Druckjob, sortiert nach Code, als A4-Bogen 3 × 8', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)

    await pickShelfS1(user)

    expect(previewCodes()).toEqual(['A-01-1', 'A-01-2', 'A-01-10'])                  // natürlich sortiert, nicht nach Anklick-Reihenfolge
    expect(screen.getByText('3 Etiketten auf 1 Seite')).toBeInTheDocument()
    expect(document.querySelectorAll('.label-page')).toHaveLength(1)
    // der Barcode jedes Etiketts ist der Code des Lagerplatzes
    const svg = document.querySelector<SVGElement>('.label-cell[data-code="A-01-10"] svg')!
    expect(svg.getAttribute('data-modules')).toBe(encodeCode128('A-01-10').modules)
    expect(svg).toHaveAccessibleName('Code 128: A-01-10')
  })

  it('filtert nach Lager und Gang; ein Gang umfasst mehrere Regale, ein Lager mehrere Gänge', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)

    await user.selectOptions(await screen.findByRole('combobox', { name: /^Lager/ }), 'w1')
    expect(screen.getByRole('button', { name: 'Alle Treffer auswählen (5)' })).toBeInTheDocument()

    await user.selectOptions(screen.getByRole('combobox', { name: /^Gang/ }), 'a1')
    expect(screen.getByRole('button', { name: 'Alle Treffer auswählen (4)' })).toBeInTheDocument()
    // die Regal-Liste zeigt nur Regale des gewählten Gangs
    const shelfOptions = within(screen.getByRole('combobox', { name: /^Regal/ })).getAllByRole('option').map((o) => o.textContent)
    expect(shelfOptions).toEqual(['Alle Regale', 'S1', 'S2'])

    await user.click(screen.getByRole('button', { name: /^Alle Treffer auswählen/ }))
    expect(previewCodes()).toEqual(['A-01-1', 'A-01-2', 'A-01-10', 'A-02-1'])

    // Lager wechseln setzt Gang und Regal zurück; die Auswahl bleibt bestehen
    await user.selectOptions(screen.getByRole('combobox', { name: /^Lager/ }), 'w2')
    expect(screen.getByRole('button', { name: 'Alle Treffer auswählen (1)' })).toBeInTheDocument()
    expect(previewCodes()).toHaveLength(4)
  })

  it('einzeln wählen und abwählen; die Vorschau folgt', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)

    await user.click(await screen.findByRole('checkbox', { name: 'Etikett für Lagerplatz B-01-1' }))
    await user.click(screen.getByRole('checkbox', { name: 'Etikett für Lagerplatz A-02-1' }))
    expect(previewCodes()).toEqual(['A-02-1', 'B-01-1'])

    await user.click(screen.getByRole('checkbox', { name: 'Etikett für Lagerplatz A-02-1' }))
    expect(previewCodes()).toEqual(['B-01-1'])
    await user.click(screen.getByRole('button', { name: 'Auswahl leeren' }))
    expect(previewCodes()).toEqual([])
    expect(screen.getByText('Noch nichts ausgewählt.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Drucken' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'ZPL herunterladen' })).toBeDisabled()
  })

  it('Format, Kopienzahl und Startposition bestimmen die Seiten', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)
    await pickShelfS1(user)

    // 3 Etiketten × 9 Kopien = 27 Etiketten = 2 Bögen
    await user.clear(screen.getByRole('spinbutton', { name: /^Kopien/ }))
    await user.type(screen.getByRole('spinbutton', { name: /^Kopien/ }), '9')
    expect(screen.getByText('3 Etiketten × 9 Kopien = 27 auf 2 Seiten')).toBeInTheDocument()
    expect(document.querySelectorAll('.label-page')).toHaveLength(2)
    expect(previewCodes().slice(0, 10)).toEqual([...Array(9).fill('A-01-1'), 'A-01-2'])

    // Startposition 23: nur 2 Plätze frei, 27 Etiketten brauchen 2 Bögen + 1
    await user.clear(screen.getByRole('spinbutton', { name: /^Startposition/ }))
    await user.type(screen.getByRole('spinbutton', { name: /^Startposition/ }), '23')
    expect(screen.getByText('3 Etiketten × 9 Kopien = 27 auf 3 Seiten')).toBeInTheDocument()

    // Einzeletikett: jede Etikett-Kopie eine eigene Seite, keine Startposition
    await user.selectOptions(screen.getByRole('combobox', { name: /^Format/ }), 'single')
    expect(document.querySelectorAll('.label-page')).toHaveLength(27)
    expect(screen.queryByRole('spinbutton', { name: /^Startposition/ })).not.toBeInTheDocument()
    expect(document.querySelector('.label-sheets style')!.textContent).toContain('size: 50mm 30mm')
  }, HEAVY_TEST_TIMEOUT)

  it('"Drucken" öffnet den Druckdialog des Browsers; bei sehr vielen Etiketten fragt die Seite vorher nach', async () => {
    const print = vi.spyOn(window, 'print').mockImplementation(() => {})
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)
    await pickShelfS1(user)

    await user.click(screen.getByRole('button', { name: 'Drucken' }))
    expect(print).toHaveBeenCalledTimes(1)

    // 3 × 100 Kopien = 300 Etiketten: erst bestätigen
    fireEvent.change(screen.getByRole('spinbutton', { name: /^Kopien/ }), { target: { value: '100' } })
    await user.click(screen.getByRole('button', { name: 'Drucken' }))
    const dialog = await screen.findByRole('dialog', { name: 'Viele Etiketten drucken?' })
    expect(within(dialog).getByText(/300 Etiketten auf 13 Seiten/)).toBeInTheDocument()
    expect(print).toHaveBeenCalledTimes(1)
    await user.click(within(dialog).getByRole('button', { name: 'Abbrechen' }))
    expect(print).toHaveBeenCalledTimes(1)

    await user.click(screen.getByRole('button', { name: 'Drucken' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Drucken' }))
    expect(print).toHaveBeenCalledTimes(2)
  }, HEAVY_TEST_TIMEOUT)

  it('mehr als 1000 Etiketten samt Kopien werden im Browser nicht gedruckt (keine Vorschau); der ZPL-Download bleibt möglich', async () => {
    api.setRoute('GET /articles', Array.from({ length: 25 }, (_, i) => makeArticle({ id: `m${i}`, sku: `MASS-${String(i).padStart(2, '0')}`, name: `Massenartikel ${i}` })))
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />, { initialEntries: ['/labels?type=article'] })
    await user.click(await screen.findByRole('button', { name: 'Alle Treffer auswählen (25)' }))
    expect(previewCodes()).toHaveLength(25)

    fireEvent.change(screen.getByRole('spinbutton', { name: /^Kopien/ }), { target: { value: '41' } })   // 25 × 41 = 1025

    expect(screen.getByText(/Zu viele Etiketten für einen Druck im Browser/).closest('[role="alert"]')).toHaveTextContent('1025')
    expect(previewCodes()).toEqual([])
    expect(screen.getByRole('button', { name: 'Drucken' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'ZPL herunterladen' })).toBeEnabled()

    fireEvent.change(screen.getByRole('spinbutton', { name: /^Kopien/ }), { target: { value: '2' } })    // 50: wieder im erlaubten Bereich
    expect(screen.queryByText(/Zu viele Etiketten für einen Druck im Browser/)).not.toBeInTheDocument()
    expect(previewCodes()).toHaveLength(50)
    expect(screen.getByRole('button', { name: 'Drucken' })).toBeEnabled()
  })

  it('"ZPL herunterladen": ruft je Etikett den vorhandenen Endpunkt auf (mit Token) und speichert eine Datei mit allen Etiketten', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)
    await pickShelfS1(user)
    await user.clear(screen.getByRole('spinbutton', { name: /^Kopien/ }))
    await user.type(screen.getByRole('spinbutton', { name: /^Kopien/ }), '2')

    await user.click(screen.getByRole('button', { name: 'ZPL herunterladen' }))

    await waitFor(() => expect(saveBlob).toHaveBeenCalledTimes(1))
    const [blob, fileName] = (saveBlob as Mock).mock.calls[0] as [Blob, string]
    expect(fileName).toBe('etiketten-lagerplaetze-3.zpl')
    // sortiert nach Code, Kopienzahl als ^PQ-Query je Endpunkt
    expect(await readBlob(blob)).toBe(zpl('A-01-1') + zpl('A-01-2') + zpl('A-01-10'))
    const requests = api.requests.filter((r) => r.path.startsWith('/labels/'))
    expect(requests.map((r) => r.path).sort()).toEqual(['/labels/bin/b1.zpl', '/labels/bin/b10.zpl', '/labels/bin/b2.zpl'])
    expect(requests.every((r) => r.query === 'copies=2')).toBe(true)
    expect(await screen.findByText(/3 Etiketten als/)).toHaveTextContent('etiketten-lagerplaetze-3.zpl')
  })

  it('ein einzelnes Etikett bekommt den Dateinamen des Servers; ein Fehler des Servers erscheint als Meldung, ohne Datei', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)
    await user.click(await screen.findByRole('checkbox', { name: 'Etikett für Lagerplatz A-01-1' }))
    await user.click(screen.getByRole('button', { name: 'ZPL herunterladen' }))
    await waitFor(() => expect(saveBlob).toHaveBeenCalledTimes(1))
    expect((saveBlob as Mock).mock.calls[0][1]).toBe('bin-A-01-1.zpl')

    api.setRoute('GET /labels/bin/b1.zpl', fail(403, { code: 'forbidden', detail: 'Keine Berechtigung.' }))
    await user.click(screen.getByRole('button', { name: 'ZPL herunterladen' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('ZPL-Download fehlgeschlagen:')
    expect(saveBlob).toHaveBeenCalledTimes(1)
  })

  it('Artikel: Suche über SKU und Name, Etikett mit SKU als Barcode und Name darunter; nur die Artikel werden geladen', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />, { initialEntries: ['/labels?type=article'] })

    await user.type(await screen.findByRole('searchbox', { name: /^Suche/ }), 'mutter')
    expect(screen.queryByRole('checkbox', { name: 'Etikett für Artikel ART-001' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('checkbox', { name: 'Etikett für Artikel ART-002' }))

    expect(previewCodes()).toEqual(['ART-002'])
    expect(within(document.querySelector<HTMLElement>('.label-cell')!).getByText('Mutter M8')).toHaveClass('label-title')
    expect(api.calls('GET', '/warehouse/layout')).toHaveLength(0)
    expect(api.calls('GET', '/orders')).toHaveLength(0)
    expect(api.calls('GET', '/articles')).toHaveLength(1)
  })

  it('ein Code, den Code 128 nicht darstellen kann, wird gemeldet und erscheint nur als Text', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)
    await user.selectOptions(await screen.findByRole('combobox', { name: /^Etikettenart/ }), 'article')
    await user.click(await screen.findByRole('checkbox', { name: 'Etikett für Artikel Größe-3' }))

    const warning = await screen.findByText('Nicht als Code 128 darstellbar:')
    expect(warning.closest('[role="alert"]')).toHaveTextContent('U+00F6')
    expect(document.querySelector('.label-cell svg')).toBeNull()
    expect(screen.getByText('Code nicht als Code 128 darstellbar')).toBeInTheDocument()
  })

  it('ein sehr langer Code auf dem kleinen Etikett wird als zu dicht gemeldet, auf dem A4-Bogen nicht', async () => {
    api.setRoute('GET /articles', [makeArticle({ id: 'a9', sku: 'ORD-2026-0001-ABCDEFGH', name: 'Lang' })])
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)
    await user.selectOptions(await screen.findByRole('combobox', { name: /^Etikettenart/ }), 'article')
    await user.click(await screen.findByRole('checkbox', { name: 'Etikett für Artikel ORD-2026-0001-ABCDEFGH' }))

    expect(screen.queryByText(/Zu lang für dieses Format/)).not.toBeInTheDocument()
    await user.selectOptions(screen.getByRole('combobox', { name: /^Format/ }), 'single')
    expect(await screen.findByText(/Zu lang für dieses Format/)).toBeInTheDocument()
  })

  it('Bestellungen: Nummer als Barcode, Kundenreferenz als Zusatz, ZPL über den Bestell-Endpunkt', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LabelsPage />)
    await user.selectOptions(await screen.findByRole('combobox', { name: /^Etikettenart/ }), 'order')

    // Status als Pill, Kundenreferenz in der Liste
    const row = (await screen.findByRole('checkbox', { name: 'Etikett für Bestellung ORD-DEMO-01' })).closest('tr')!
    expect(within(row).getByText('K-4711')).toBeInTheDocument()
    await user.click(within(row).getByRole('checkbox'))

    expect(previewCodes()).toEqual(['ORD-DEMO-01'])
    expect(screen.getByText('Kunde: K-4711', { selector: '.label-detail' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'ZPL herunterladen' }))
    await waitFor(() => expect(saveBlob).toHaveBeenCalledTimes(1))
    expect((saveBlob as Mock).mock.calls[0][1]).toBe('order-ORD-DEMO-01.zpl')
    expect(api.calls('GET', '/labels/order/o1.zpl')).toHaveLength(1)
  })

  it('Verweis von einer anderen Seite: /labels?type=article&ids=a1 startet mit dieser Auswahl', async () => {
    renderWithProviders(<LabelsPage />, { initialEntries: ['/labels?type=article&ids=a1,a2'] })

    await waitFor(() => expect(previewCodes()).toEqual(['ART-001', 'ART-002']))
    expect(screen.getByRole('combobox', { name: /^Etikettenart/ })).toHaveValue('article')
  })

  it('ohne erreichbare Daten steht ein Hinweis mit "Erneut versuchen" statt einer leeren Liste', async () => {
    api.setRoute('GET /warehouse/layout', fail(500, { detail: 'Datenbank nicht erreichbar' }))
    renderWithProviders(<LabelsPage />)

    expect(await screen.findByText('Konnte die Lagerplätze nicht laden.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Erneut versuchen' })).toBeInTheDocument()
  })
})

describe('Anmeldung der Seite (Route-Registry)', () => {
  const roleFilter = (roles: string[]) => (role: RoleName) => hasRequiredRole(roles, role)

  it('meldet /labels an, ohne eine feste Seite zu überschreiben', () => {
    const registered = collectFeatureRoutes({ '../features/labels/route.tsx': { default: routes } }, RESERVED_PATHS)

    expect(registered).toHaveLength(1)
    expect(registered[0]).toMatchObject({ path: '/labels', label: 'Etiketten', roles: ['Picker'] })
  })

  it('erscheint in der Navigation für Picker, Manager und Admin, nicht für Viewer', () => {
    const groups = buildNavGroups(STATIC_NAV_GROUPS, routes)
    const labelsFor = (roles: string[]) => visibleNavGroups(groups, roleFilter(roles)).flatMap((g) => g.items.map((i) => i.to))

    expect(labelsFor(['Picker'])).toContain('/labels')
    expect(labelsFor(['Manager'])).toContain('/labels')
    expect(labelsFor(['Admin'])).toContain('/labels')
    expect(labelsFor(['Viewer'])).not.toContain('/labels')
  })
})
