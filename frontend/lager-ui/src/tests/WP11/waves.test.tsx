import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { WavesPage } from '../../pages/WavesPage'
import type { OrderDto, PickWaveDto } from '../../api/types'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'

// Der Cutoff einer Welle wird in einem datetime-local-Feld (Ortszeit ohne Zone) eingegeben. Die Anzeige liest Zeitpunkte
// ohne Zone als UTC (parseServerDate) — deshalb muss die Eingabe als UTC-Zeitpunkt gesendet werden, sonst zeigt die
// Wellen-Liste die eingegebene Uhrzeit um den UTC-Versatz verschoben an.
describe('WavesPage — Cutoff-Zeit', () => {
  let api: MockApi
  let waves: PickWaveDto[]

  const order = {
    id: 'o1',
    orderNumber: 'ORD-1',
    customerReference: null,
    status: 'New',
    source: 'Manual',
    createdAt: '2025-01-01T10:00:00',
    lines: [],
    hasStockNow: true,
    hasStockAfterFifo: true,
  } satisfies OrderDto

  beforeEach(() => {
    waves = []
    api = installMockApi({
      'GET /pick-waves': () => waves,
      'GET /orders': [order],
      'POST /pick-waves': (request) => {
        const body = request.body as { description: string | null; cutoffAt: string | null; orderIds: string[] }
        const wave: PickWaveDto = {
          id: 'w1',
          waveNumber: 'W-0001',
          description: body.description,
          status: 'Open',
          // Der Server gibt Zeitpunkte ohne Zonen-Kennung und ohne Nachkommastellen zurück.
          cutoffAt: body.cutoffAt ? body.cutoffAt.replace(/\.\d+Z$/, '') : null,
          createdAt: '2025-01-01T10:00:00',
          releasedAt: null,
          completedAt: null,
          orderIds: body.orderIds,
          pickListIds: [],
        }
        waves.push(wave)
        return wave
      },
    })
  })
  afterEach(() => api.restore())

  it('sendet die Cutoff-Zeit als UTC-Zeitpunkt und zeigt danach dieselbe Uhrzeit an', async () => {
    const user = userEvent.setup()
    renderWithProviders(<WavesPage />)

    await user.click(await screen.findByRole('button', { name: '+ Neue Welle' }))
    fireEvent.change(screen.getByLabelText(/Cutoff-Zeit/), { target: { value: '2026-05-25T18:00' } })
    await user.click(await screen.findByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: 'Welle anlegen (1)' }))

    await waitFor(() => expect(api.calls('POST', '/pick-waves')).toHaveLength(1))
    const body = api.calls('POST', '/pick-waves')[0].body as { cutoffAt: string; orderIds: string[] }
    expect(body.cutoffAt).toBe(new Date(2026, 4, 25, 18, 0).toISOString())
    expect(body.orderIds).toEqual(['o1'])

    // Nach dem Anlegen zeigt die Liste die eingegebene Ortszeit, nicht die um den UTC-Versatz verschobene.
    expect(await screen.findByText('25.05.2026, 18:00')).toBeInTheDocument()
  })

  it('sendet ohne Cutoff-Zeit null', async () => {
    const user = userEvent.setup()
    renderWithProviders(<WavesPage />)

    await user.click(await screen.findByRole('button', { name: '+ Neue Welle' }))
    await user.click(await screen.findByRole('checkbox'))
    await user.click(screen.getByRole('button', { name: 'Welle anlegen (1)' }))

    await waitFor(() => expect(api.calls('POST', '/pick-waves')).toHaveLength(1))
    expect((api.calls('POST', '/pick-waves')[0].body as { cutoffAt: unknown }).cutoffAt).toBeNull()
  })
})
