import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { LayoutEditorPage } from '../../pages/LayoutEditorPage'
import { WarehouseSelector } from '../../components/WarehouseSelector'
import { useActiveWarehouse } from '../../state/activeWarehouse'
import { useAuth } from '../../state/auth'
import type { MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'
import { installLayoutApi, makeAisle, makeWarehouse, makeZone } from './fixtures'

// Viele Benutzeraktionen je Test: bei voller Maschinenlast (paralleler Testlauf) reichen die 5 s Standard-Timeout nicht immer.
vi.setConfig({ testTimeout: 30_000 })

// jsdom hat keine Canvas-Unterstützung (siehe WP19/layoutEditor.test.tsx): die Zeichenfläche wird durch eine leere Fläche ersetzt.
vi.mock('../../components/WarehouseCanvas', () => ({ WarehouseCanvas: () => <div data-testid="canvas" /> }))

describe('Erststart ohne Lager: der Weg zur Lagerstruktur', () => {
  let api: MockApi

  beforeEach(() => { signInAs(['Manager']) })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
    useActiveWarehouse.getState().setActive(null)
  })

  describe('WarehouseSelector', () => {
    it('zeigt bei leerer Liste einen Link zur Lagerstruktur', async () => {
      api = installLayoutApi([]).api
      renderWithProviders(<WarehouseSelector />)

      expect(await screen.findByRole('link', { name: 'Noch kein Lager – jetzt anlegen' })).toHaveAttribute('href', '/warehouses')
    })

    it('zeigt Nutzern ohne Manager-Rolle nur den Hinweis (kein Link auf eine gesperrte Seite)', async () => {
      signInAs(['Picker'])
      api = installLayoutApi([]).api
      renderWithProviders(<WarehouseSelector />)

      expect(await screen.findByText('Noch kein Lager angelegt')).toBeInTheDocument()
      expect(screen.queryByRole('link')).not.toBeInTheDocument()
    })

    it('bleibt bei einem einzigen Lager unsichtbar und wählt es als aktiv', async () => {
      api = installLayoutApi([makeWarehouse('w1', 'WH01', 'Haupt')]).api
      const { container } = renderWithProviders(<WarehouseSelector />)

      await vi.waitFor(() => expect(useActiveWarehouse.getState().activeId).toBe('w1'))
      expect(container).toBeEmptyDOMElement()
    })
  })

  describe('LayoutEditorPage', () => {
    it('zeigt statt einer leeren Zeichenfläche den Leerzustand mit Link zur Lagerstruktur', async () => {
      api = installLayoutApi([]).api
      renderWithProviders(<LayoutEditorPage />)

      expect(await screen.findByRole('heading', { name: 'Noch kein Lager – jetzt anlegen' })).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Zur Lagerstruktur' })).toHaveAttribute('href', '/warehouses')
      expect(screen.queryByTestId('canvas')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: '+ Neues Regal' })).not.toBeInTheDocument()
    })

    it('zeigt mit einem Lager (auch ohne Gänge) den Editor und den Hinweis auf die Lagerstruktur', async () => {
      api = installLayoutApi([makeWarehouse('w1', 'WH01', 'Haupt', [makeZone('z1', 'Z-A', 'Zone A', [makeAisle('a1', 'A1')])])]).api
      renderWithProviders(<LayoutEditorPage />)

      expect(await screen.findByRole('button', { name: '+ Neues Regal' })).toBeInTheDocument()
      expect(screen.getByTestId('canvas')).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Lagerstruktur' })).toHaveAttribute('href', '/warehouses')
      expect(screen.queryByRole('heading', { name: 'Noch kein Lager – jetzt anlegen' })).not.toBeInTheDocument()
    })
  })
})
