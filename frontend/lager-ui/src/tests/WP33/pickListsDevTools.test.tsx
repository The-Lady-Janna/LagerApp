import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { PickListsPage } from '../../pages/PickListsPage'
import { useAuth } from '../../state/auth'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { makePickList } from '../helpers/fixtures'
import { renderWithProviders, signInAs } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner (paralleler dotnet test, CI) reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// WP33: "Komplette DB löschen + Demo-Daten neu erzeugen" (Reseed) und "Bulk-Testdaten" standen für Admins in JEDEM Build da, der
// Server (AdminController) lehnt sie außerhalb der Umgebung Development aber mit 403 "development_only" ab. Ein Production-Build
// (import.meta.env.DEV === false) zeigt sie deshalb nicht mehr; im Entwicklungs-Build sind sie da und als solche gekennzeichnet.
describe('PickListsPage: Entwicklungs-Werkzeuge nur im Entwicklungs-Build', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /picklists': [makePickList({ id: 'pl1', pickListNumber: 'PL-0001', status: 'Pending' })],
    })
  })
  afterEach(() => {
    vi.unstubAllEnvs()
    api.restore()
    useAuth.getState().logout()
  })

  const resetButton = () => screen.queryByRole('button', { name: /Nicht gepackte Picklisten zurücksetzen/ })
  const reseedButton = () => screen.queryByRole('button', { name: /Demo-Daten neu erzeugen/ })
  const bulkCard = () => screen.queryByText('Bulk-Testdaten')

  it('Entwicklungs-Build: Admins sehen Reset, Reseed und Bulk-Testdaten; Reseed und Bulk sind als Entwicklungsfunktion gekennzeichnet', async () => {
    vi.stubEnv('DEV', true)
    signInAs(['Admin'])
    renderWithProviders(<PickListsPage />)

    expect(await screen.findByText('PL-0001')).toBeInTheDocument()
    expect(resetButton()).toBeInTheDocument()
    expect(reseedButton()).toHaveTextContent('nur Entwicklung')
    expect(reseedButton()).toHaveAttribute('title', expect.stringContaining('Development'))
    expect(bulkCard()).toBeInTheDocument()
  })

  it('Production-Build: Reseed und Bulk-Testdaten fehlen, das Zurücksetzen der Picklisten (läuft überall) bleibt', async () => {
    vi.stubEnv('DEV', false)
    signInAs(['Admin'])
    renderWithProviders(<PickListsPage />)

    expect(await screen.findByText('PL-0001')).toBeInTheDocument()
    expect(resetButton()).toBeInTheDocument()
    expect(reseedButton()).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Komplette DB/ })).not.toBeInTheDocument()
    expect(bulkCard()).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Bulk-Daten/ })).not.toBeInTheDocument()
  })

  it.each([['Manager'], ['Picker']])('Production-Build, Rolle %s: weder Reset noch Reseed noch Bulk', async (role) => {
    vi.stubEnv('DEV', false)
    signInAs([role])
    renderWithProviders(<PickListsPage />)

    expect(await screen.findByText('PL-0001')).toBeInTheDocument()
    expect(resetButton()).not.toBeInTheDocument()
    expect(reseedButton()).not.toBeInTheDocument()
    expect(bulkCard()).not.toBeInTheDocument()
  })
})
