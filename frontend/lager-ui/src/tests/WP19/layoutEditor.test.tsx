import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { LayoutEditorPage } from '../../pages/LayoutEditorPage'
import { useActiveWarehouse } from '../../state/activeWarehouse'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// jsdom hat keine Canvas-Unterstützung: die Zeichenfläche wird durch Schaltflächen ersetzt, die genau die Ereignisse
// auslösen, die WarehouseCanvas an die Seite meldet (Klick, Doppelklick auf die leere Fläche, Weltkoordinaten in mm).
vi.mock('../../components/WarehouseCanvas', () => ({
  WarehouseCanvas: (props: {
    onStageClick?: (x: number, y: number) => void
    onStageDoubleClick?: (x: number, y: number) => void
  }) => (
    <div>
      <button onClick={() => props.onStageClick?.(1000, 1000)}>Klick A</button>
      <button onClick={() => props.onStageClick?.(3000, 1000)}>Klick B</button>
      <button onClick={() => props.onStageDoubleClick?.(3000, 1000)}>Doppelklick</button>
    </div>
  ),
}))

const warehouse = { id: 'w1', code: 'W1', name: 'Hauptlager', zones: [] }

describe('LayoutEditorPage', () => {
  let api: MockApi

  beforeEach(() => {
    useActiveWarehouse.getState().setActive('w1')
    api = installMockApi({
      'GET /warehouse/layout': [warehouse],
      'GET /warehouse/storage-locations': [],
      'GET /warehouse/walls': [],
      'GET /warehouse/pick-points': [],
      'POST /warehouse/walls': (request) => ({
        id: 'wall-1', warehouseId: 'w1', label: null, thicknessMm: 100,
        points: (request.body as { points: unknown[] }).points,
      }),
    })
  })
  afterEach(() => {
    api.restore()
    useActiveWarehouse.getState().setActive(null)
  })

  async function startDrawing(user: ReturnType<typeof userEvent.setup>) {
    // StrictMode wie in main.tsx: React ruft dort State-Updater doppelt auf — Seiteneffekte darin hätten die Wand doppelt angelegt.
    renderWithProviders(<StrictMode><LayoutEditorPage /></StrictMode>)
    await user.click(await screen.findByRole('button', { name: '+ Wand zeichnen' }))
    await user.click(screen.getByRole('button', { name: 'Klick A' }))
    await user.click(screen.getByRole('button', { name: 'Klick B' }))
  }

  it('legt eine Wand per Doppelklick GENAU EINMAL an (auch im StrictMode) und ohne den doppelten Endpunkt', async () => {
    const user = userEvent.setup()
    await startDrawing(user)

    await user.click(screen.getByRole('button', { name: 'Doppelklick' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/walls')).toHaveLength(1))
    const [call] = api.calls('POST', '/warehouse/walls')
    expect(call.body).toMatchObject({
      warehouseId: 'w1', thicknessMm: 100,
      points: [{ xMm: 1000, yMm: 1000, zMm: 0 }, { xMm: 3000, yMm: 1000, zMm: 0 }],
    })
    expect(await screen.findByText(/Wand mit 2 Punkten \(1 Segment\) angelegt/)).toBeInTheDocument()
    await new Promise((resolve) => setTimeout(resolve, 30))
    expect(api.calls('POST', '/warehouse/walls')).toHaveLength(1)
  })

  it('bietet "Wand fertig" als Touch-Ersatz für den Doppelklick — ebenfalls genau eine Wand', async () => {
    const user = userEvent.setup()
    await startDrawing(user)

    await user.click(screen.getByRole('button', { name: '✓ Wand fertig' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/walls')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/walls')[0].body).toMatchObject({ points: [{ xMm: 1000 }, { xMm: 3000 }] })
  })

  it('verwirft eine Wand mit weniger als 2 Punkten mit Hinweis, ohne Server-Aufruf', async () => {
    const user = userEvent.setup()
    renderWithProviders(<StrictMode><LayoutEditorPage /></StrictMode>)
    await user.click(await screen.findByRole('button', { name: '+ Wand zeichnen' }))
    await user.click(screen.getByRole('button', { name: 'Klick A' }))

    await user.click(screen.getByRole('button', { name: 'Doppelklick' }))

    expect(await screen.findByText('Mindestens 2 Punkte nötig — Wand verworfen')).toBeInTheDocument()
    expect(api.calls('POST', '/warehouse/walls')).toHaveLength(0)
  })

  it('zeigt Fehler der Wände-Abfrage an, statt sie still als "keine Wände" darzustellen', async () => {
    api.setRoute('GET /warehouse/walls', fail(403, null))
    renderWithProviders(<LayoutEditorPage />)

    const alert = await screen.findByRole('alert')

    expect(alert).toHaveTextContent('Layout-Daten konnten nicht (vollständig) geladen werden')
    expect(alert).toHaveTextContent('Keine Berechtigung')
    expect(screen.getByRole('button', { name: 'Erneut versuchen' })).toBeInTheDocument()
  })

  it('zeigt die Toolbar mit Schaltern (aria-pressed) und beschrifteten Bedienelementen', async () => {
    const user = userEvent.setup()
    renderWithProviders(<LayoutEditorPage />)

    const heat = await screen.findByRole('button', { name: /Heatmap aus/ })
    expect(heat).toHaveAttribute('aria-pressed', 'false')
    await user.click(heat)

    expect(screen.getByRole('button', { name: /Heatmap an/ })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('combobox', { name: 'Zeitraum der Heatmap' })).toBeInTheDocument()
  })
})
