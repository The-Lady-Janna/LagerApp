import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { GridCellDto, GridLayoutDto, SaveGridLayoutRequest } from '../../api/gridLayoutHooks'
import { LayoutPage } from '../../pages/LayoutPage'
import { useActiveWarehouse } from '../../state/activeWarehouse'
import { useAuth } from '../../state/auth'
import { useToasts } from '../../state/toasts'
import { fail, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'
import { installLayoutApi, makeAisle, makeShelf, makeWarehouse, makeZone } from '../WP22/fixtures'

vi.setConfig({ testTimeout: 30_000 })

// jsdom hat keine Canvas-Unterstützung: der erweiterte Editor zeichnet nur eine leere Fläche.
vi.mock('../../components/WarehouseCanvas', () => ({ WarehouseCanvas: () => <div data-testid="canvas" /> }))

/** Ein Feld liegt in den Tests bei clientX = Spalte * 100 + 50, clientY = Zeile * 100 + 50 (jsdom kennt kein Layout). */
const at = (col: number, row: number) => ({ clientX: col * 100 + 50, clientY: row * 100 + 50 })

function gridDto(cells: GridCellDto[] = [], overrides: Partial<GridLayoutDto> = {}): GridLayoutDto {
  return {
    warehouseId: 'w1', exists: cells.length > 0, version: cells.length > 0 ? 'v1' : null,
    cellSizeMm: 1_000, columns: 6, rows: 4,
    containerWidthMm: 600, containerDepthMm: 600, containerHeightMm: 400, containerMaxWeightGrams: 50_000,
    cells, managedShelfIds: [], managedWallIds: [], managedPickPointIds: [],
    ...overrides,
  }
}

const cell = (col: number, row: number) => document.querySelector<HTMLElement>(`[data-col="${col}"][data-row="${row}"]`)!

describe('Raster-Layout (einfacher Editor)', () => {
  let api: MockApi
  let server: GridLayoutDto
  let originalElementFromPoint: typeof document.elementFromPoint

  beforeEach(() => {
    signInAs(['Manager'])
    useActiveWarehouse.getState().setActive('w1')
    originalElementFromPoint = document.elementFromPoint
    document.elementFromPoint = (x: number, y: number) =>
      document.querySelector(`[data-col="${Math.floor(x / 100)}"][data-row="${Math.floor(y / 100)}"]`)
    server = gridDto()
    api = installLayoutApi([makeWarehouse('w1', 'WH01', 'Hauptlager')], {
      'GET /warehouse/:id/grid': () => server,
      'PUT /warehouse/:id/grid': (request) => {
        const body = request.body as SaveGridLayoutRequest
        server = gridDto(body.cells.map((c) => ({
          ...c, ref: '', type: c.type as GridCellDto['type'], shelfId: null, shelfCode: c.type === 'Shelf' ? `A01-0${c.col}` : null,
          occupiedContainers: 0,
        })), { version: 'v2', exists: true })
        return { layout: server, summary: { shelvesCreated: 3, shelvesRemoved: 0, containersCreated: 12, containersRemoved: 0, walls: 0, pickPoints: 0 } }
      },
    }).api
  })

  afterEach(() => {
    api.restore()
    document.elementFromPoint = originalElementFromPoint
    useAuth.getState().logout()
    useActiveWarehouse.getState().setActive(null)
  })

  it('öffnet für ein leeres Lager das Raster mit Spalten A, B, C … und Zeilen 1, 2, 3 …', async () => {
    renderWithProviders(<LayoutPage />, { initialEntries: ['/layout'] })

    const grid = await screen.findByRole('grid', { name: 'Lager-Raster mit 6 Spalten und 4 Zeilen' })
    expect(within(grid).getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['A', 'B', 'C', 'D', 'E', 'F'])
    expect(within(grid).getAllByRole('rowheader').map((h) => h.textContent)).toEqual(['1', '2', '3', '4'])
    expect(screen.getByRole('button', { name: 'Raster (einfach)' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByText('Noch kein Raster gespeichert.')).toBeInTheDocument()
  })

  it('malt mit gewählter Feldart ein Rechteck und speichert das Raster', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LayoutPage />, { initialEntries: ['/layout'] })
    const grid = await screen.findByRole('grid')

    await user.click(screen.getByRole('radio', { name: 'Regal' }))
    fireEvent.pointerDown(grid, { ...at(1, 1), button: 0, pointerId: 1 })
    fireEvent.pointerMove(grid, { ...at(3, 1), pointerId: 1 })
    fireEvent.pointerUp(grid, { ...at(3, 1), pointerId: 1 })

    for (const col of [1, 2, 3]) {
      expect(cell(col, 1)).toHaveClass('grid-cell--shelf')
      expect(cell(col, 1)).toHaveTextContent('4')
    }
    expect(cell(4, 1)).toHaveClass('grid-cell--walkway')
    expect(screen.getByText('3 Felder geändert – noch nicht gespeichert.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('PUT', '/warehouse/w1/grid')).toHaveLength(1))
    const body = api.calls('PUT', '/warehouse/w1/grid')[0].body as SaveGridLayoutRequest
    expect(body.expectedVersion).toBeNull()
    expect(body.cells).toEqual([
      { col: 1, row: 1, type: 'Shelf', containers: 4 },
      { col: 2, row: 1, type: 'Shelf', containers: 4 },
      { col: 3, row: 1, type: 'Shelf', containers: 4 },
    ])
    expect(await screen.findByText('Alle Änderungen sind gespeichert.')).toBeInTheDocument()
    expect(useToasts.getState().toasts.map((toast) => toast.message)).toContainEqual(expect.stringMatching(/^Gespeichert: 3 Regale neu/))
    expect(cell(2, 1)).toHaveAttribute('title', expect.stringContaining('A01-02'))
  })

  it('wählt die Feldart per Rechtsklick direkt im Raster, Entf macht Laufweg daraus, Strg+Z macht rückgängig', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LayoutPage />, { initialEntries: ['/layout'] })
    const grid = await screen.findByRole('grid')

    // Auswahl B1:C1 aufziehen, dann Rechtsklick in die Auswahl.
    fireEvent.pointerDown(grid, { ...at(1, 0), button: 0, pointerId: 1 })
    fireEvent.pointerMove(grid, { ...at(2, 0), pointerId: 1 })
    fireEvent.pointerUp(grid, { ...at(2, 0), pointerId: 1 })
    expect(screen.getByRole('heading', { name: /B1:C1/ })).toBeInTheDocument()
    fireEvent.contextMenu(grid, at(2, 0))

    const menu = await screen.findByRole('dialog', { name: 'Feldart für B1:C1' })
    await user.click(within(menu).getByRole('button', { name: /Wand/ }))
    expect(screen.queryByRole('dialog', { name: 'Feldart für B1:C1' })).not.toBeInTheDocument()
    expect(cell(1, 0)).toHaveClass('grid-cell--wall')
    expect(cell(2, 0)).toHaveClass('grid-cell--wall')

    grid.focus()
    await user.keyboard('{Delete}')
    expect(cell(1, 0)).toHaveClass('grid-cell--walkway')
    await user.keyboard('{Control>}z{/Control}')
    expect(cell(1, 0)).toHaveClass('grid-cell--wall')

    // Pfeiltaste bewegt die Auswahl, Enter öffnet die Auswahl der Feldart für das neue Feld.
    await user.keyboard('{ArrowDown}')
    expect(screen.getByRole('heading', { name: /C2/ })).toBeInTheDocument()
    await user.keyboard('{Enter}')
    expect(await screen.findByRole('dialog', { name: 'Feldart für C2' })).toBeInTheDocument()
  })

  it('markiert Felder, deren Behälter noch Bestand haben, wenn der Server das Speichern ablehnt', async () => {
    const user = userEvent.setup()
    server = gridDto([
      { col: 0, row: 0, ref: 'A1', type: 'Shelf', containers: 2, shelfId: 's1', shelfCode: 'A01-01', occupiedContainers: 1 },
    ])
    api.setRoute('PUT /warehouse/:id/grid', fail(409, {
      code: 'grid_cells_in_use', detail: 'Das Raster wurde nicht gespeichert: In den Feldern A1 liegen Behälter, die noch verwendet werden.',
      cells: [{ col: 0, row: 0, ref: 'A1', containerCodes: ['A01-01-1'] }],
    }))
    renderWithProviders(<LayoutPage />, { initialEntries: ['/layout'] })
    const grid = await screen.findByRole('grid')
    expect(cell(0, 0)).toHaveClass('is-occupied')

    fireEvent.pointerDown(grid, { ...at(0, 0), button: 0, pointerId: 1 })
    fireEvent.pointerUp(grid, { ...at(0, 0), pointerId: 1 })
    grid.focus()
    await user.keyboard('{Delete}')
    await user.click(screen.getByRole('button', { name: 'Speichern' }))

    expect(await screen.findByText(/In den Feldern A1 liegen Behälter/)).toBeInTheDocument()
    expect(cell(0, 0)).toHaveClass('is-blocked')
    expect(api.calls('PUT', '/warehouse/w1/grid')[0].body).toMatchObject({ expectedVersion: 'v1', cells: [] })
  })

  it('zeigt Nutzern ohne Manager-Rolle das Raster nur an', async () => {
    signInAs(['Picker'])
    server = gridDto([{ col: 1, row: 1, ref: 'B2', type: 'Wall', containers: 0, shelfId: null, shelfCode: null, occupiedContainers: 0 }])
    renderWithProviders(<LayoutPage />, { initialEntries: ['/layout'] })

    await screen.findByRole('grid')
    expect(cell(1, 1)).toHaveClass('grid-cell--wall')
    expect(screen.queryByRole('toolbar')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Speichern' })).not.toBeInTheDocument()
  })

  it('öffnet ein frei gezeichnetes Lager ohne Raster im erweiterten Editor und wechselt auf Wunsch', async () => {
    const user = userEvent.setup()
    api.restore()
    api = installLayoutApi([
      makeWarehouse('w1', 'WH01', 'Hauptlager', [makeZone('z1', 'Z-A', 'Zone A', [makeAisle('a1', 'A1', [makeShelf('s1', 'S1')])])]),
    ], { 'GET /warehouse/:id/grid': () => gridDto() }).api
    renderWithProviders(<LayoutPage />, { initialEntries: ['/layout'] })

    expect(await screen.findByTestId('canvas')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Erweitert (frei)' })).toHaveAttribute('aria-pressed', 'true')

    await user.click(screen.getByRole('button', { name: 'Raster (einfach)' }))
    expect(await screen.findByRole('grid')).toBeInTheDocument()
    // Das Regal aus dem erweiterten Editor wird zur Orientierung eingeblendet.
    expect(screen.getByRole('checkbox', { name: '1 Objekt aus dem erweiterten Editor einblenden' })).toBeChecked()
  })
})
