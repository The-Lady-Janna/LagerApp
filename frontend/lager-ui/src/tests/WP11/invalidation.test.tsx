import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import {
  useAdjustStock,
  useCancelWave,
  useGeneratePickList,
  usePackPickList,
  useProcessReturn,
  useReceiveInbound,
  useReconcileInventory,
  useReleaseWave,
  useStockSummary,
} from '../../api/hooks'
import { queryKeys } from '../../lib/queryKeys'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { makePickList, makeReturn } from '../helpers/fixtures'
import { createTestQueryClient } from '../helpers/render'

// Regression (frontend#10): Mit staleTime 30 s und ohne Refetch-on-Focus blieben Bestand, Picklisten und Reports nach
// Mutationen bis zu 30 s bzw. bis zum Remount veraltet. Die Hooks invalidieren jetzt gezielt alles, was die Mutation ändert.
type Wrapper = (props: { children: ReactNode }) => ReactNode

describe('Query-Invalidierung nach Mutationen', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'POST /picklists/pl1/pack': makePickList({ status: 'Completed' }),
      'POST /picklists/generate': makePickList(),
      'POST /stock/adjust': { id: 'st1' },
      'POST /inbound/in1/receive': { id: 'in1' },
      'POST /inventory/inv1/reconcile': { id: 'inv1' },
      'POST /returns/r1/process': makeReturn({ status: 'Processed' }),
      'POST /pick-waves/w1/release': { id: 'w1' },
      'POST /pick-waves/w1/cancel': {},
      'GET /stock/summary': [],
    })
  })
  afterEach(() => api.restore())

  function setup() {
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    )
    const invalidatedKeys = () => invalidate.mock.calls.map(([filter]) => filter?.queryKey)
    return { queryClient, wrapper, invalidatedKeys }
  }

  it('Packen invalidiert Picklisten, Bestellungen, Bestand UND Reports (Packen bucht den Bestand ab)', async () => {
    const { wrapper, invalidatedKeys } = setup()
    const { result } = renderHook(() => usePackPickList(), { wrapper })

    await result.current.mutateAsync({ id: 'pl1', req: { items: [{ pickItemId: 'i1', actualQuantity: 3 }] } })

    expect(invalidatedKeys()).toEqual(
      expect.arrayContaining([queryKeys.pickLists, queryKeys.orders, queryKeys.stock, queryKeys.reports]),
    )
  })

  it('Packen lädt eine geöffnete Bestandsübersicht neu (Präfix ["stock"] trifft auch die Summary)', async () => {
    const { wrapper } = setup()
    const { result } = renderHook(() => ({ summary: useStockSummary(), pack: usePackPickList() }), { wrapper })
    await waitFor(() => expect(result.current.summary.isSuccess).toBe(true))
    expect(api.calls('GET', '/stock/summary')).toHaveLength(1)

    await result.current.pack.mutateAsync({ id: 'pl1', req: { items: [] } })

    await waitFor(() => expect(api.calls('GET', '/stock/summary')).toHaveLength(2))
  })

  it('Pickliste erzeugen invalidiert die Liste der Picklisten (die neue Liste fehlte bisher) und die Bestellungen', async () => {
    const { queryClient, wrapper, invalidatedKeys } = setup()
    const { result } = renderHook(() => useGeneratePickList(), { wrapper })

    await result.current.mutateAsync({ orderIds: ['o1'] })

    expect(invalidatedKeys()).toEqual(expect.arrayContaining([queryKeys.pickLists, queryKeys.orders]))
    // Die neue Liste ist außerdem direkt im Detail-Cache verfügbar.
    expect(queryClient.getQueryData(queryKeys.pickList('pl1'))).toMatchObject({ id: 'pl1' })
  })

  // Wellen-Freigabe erzeugt eine Pickliste und setzt Bestellungen auf Picking; ein Abbruch storniert die Listen und gibt
  // die Bestellungen wieder frei. Beides muss Picklisten UND Bestellungen neu laden (die Wellen-Seite bietet sonst
  // bereits freigegebene Bestellungen weiter als Kandidaten an).
  it('Welle freigeben lädt Wellen, Picklisten und Bestellungen neu', async () => {
    const { wrapper, invalidatedKeys } = setup()
    const { result } = renderHook(() => useReleaseWave(), { wrapper })

    await result.current.mutateAsync({ id: 'w1', req: { splitByZone: false } })

    expect(invalidatedKeys()).toEqual(expect.arrayContaining([queryKeys.waves, queryKeys.pickLists, queryKeys.orders]))
  })

  it('Welle abbrechen lädt Wellen, Picklisten und Bestellungen neu', async () => {
    const { wrapper, invalidatedKeys } = setup()
    const { result } = renderHook(() => useCancelWave(), { wrapper })

    await result.current.mutateAsync('w1')

    expect(invalidatedKeys()).toEqual(expect.arrayContaining([queryKeys.waves, queryKeys.pickLists, queryKeys.orders]))
  })

  // Alles, was Bestand bucht, muss auch die Reports (Lagerwert, Dead-Stock, Dashboard) neu laden.
  const stockChanges: [string, (wrapper: Wrapper) => Promise<unknown>][] = [
    [
      'Bestandskorrektur',
      async (wrapper) => {
        const { result } = renderHook(() => useAdjustStock(), { wrapper })
        await result.current.mutateAsync({ articleId: 'a1', storageLocationId: 'b1', delta: 1, lotNumber: null, expiryDate: null })
      },
    ],
    [
      'Wareneingang buchen',
      async (wrapper) => {
        const { result } = renderHook(() => useReceiveInbound(), { wrapper })
        await result.current.mutateAsync('in1')
      },
    ],
    [
      'Inventurabgleich',
      async (wrapper) => {
        const { result } = renderHook(() => useReconcileInventory(), { wrapper })
        await result.current.mutateAsync('inv1')
      },
    ],
    [
      'Retoure verarbeiten',
      async (wrapper) => {
        const { result } = renderHook(() => useProcessReturn(), { wrapper })
        await result.current.mutateAsync('r1')
      },
    ],
  ]

  it.each(stockChanges)('%s invalidiert Bestand und Reports', async (_name, run) => {
    const { wrapper, invalidatedKeys } = setup()

    await run(wrapper)

    expect(invalidatedKeys()).toEqual(expect.arrayContaining([queryKeys.stock, queryKeys.reports]))
  })
})
