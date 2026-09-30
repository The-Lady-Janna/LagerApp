import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { onlineManager } from '@tanstack/react-query'
import { ReturnsPage } from '../../pages/ReturnsPage'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// Ohne Netz pausiert React Query die Abfrage (weder isLoading noch error, keine Daten): die Seite darf dann
// nicht leer bleiben, sondern muss den Zustand benennen.
describe('LoadState ohne Netzverbindung (pausierte Abfrage)', () => {
  let api: MockApi
  beforeEach(() => {
    api = installMockApi({ 'GET /returns': [], 'GET /articles': [], 'GET /warehouse/storage-locations': [] })
    onlineManager.setOnline(false)
  })
  afterEach(() => {
    onlineManager.setOnline(true)
    api.restore()
  })

  it('zeigt statt einer leeren Seite einen Hinweis und stellt nichts als leere Tabelle dar', async () => {
    renderWithProviders(<ReturnsPage />)

    expect(await screen.findByRole('status')).toHaveTextContent('Keine Verbindung')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(api.calls('GET', '/returns')).toHaveLength(0)
  })
})
