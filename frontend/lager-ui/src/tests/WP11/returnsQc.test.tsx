import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderHook, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { useReturns, useSetReturnQc } from '../../api/hooks'
import type { ReturnShipmentDto } from '../../api/types'
import { ReturnsPage } from '../../pages/ReturnsPage'
import { queryKeys } from '../../lib/queryKeys'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makeReturn } from '../helpers/fixtures'
import { createTestQueryClient, renderWithProviders } from '../helpers/render'

// Regression (frontend#1): useSetReturnQc invalidierte ['returns', id] — einen Key, den es nie gab. Die Zeilen der
// Retouren-Seite kommen aus der Listen-Query ['returns'], deshalb blieb der QC-Status auf "Pending" stehen.
describe('Retouren-QC', () => {
  let api: MockApi
  let server: ReturnShipmentDto

  beforeEach(() => {
    server = makeReturn()
    api = installMockApi({
      'GET /returns': () => [server],
      'GET /articles': [],
      'GET /warehouse/storage-locations': [],
      'PUT /returns/:id/lines/:lineId/qc': (req) => {
        const { result } = req.body as { result: ReturnShipmentDto['lines'][number]['qcResult'] }
        server = { ...server, lines: server.lines.map((l) => ({ ...l, qcResult: result })) }
        return server
      },
    })
  })
  afterEach(() => api.restore())

  it('invalidiert den Retouren-Query-Key (Liste), nicht einen nicht existierenden Detail-Key', async () => {
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    )
    const { result } = renderHook(() => useSetReturnQc(), { wrapper })

    await result.current.mutateAsync({ id: 'r1', lineId: 'rl1', req: { result: 'Defect' } })

    expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.returns })
    expect(queryKeys.returns).toEqual(['returns'])
    // Der Key muss die tatsächlich verwendete Listen-Query treffen: Filter ist Präfix des Query-Keys.
    expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['returns', 'r1'] })
  })

  it('lädt die Liste nach dem Setzen des QC-Ergebnisses neu', async () => {
    const queryClient = createTestQueryClient()
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    )
    const { result } = renderHook(() => ({ list: useReturns(), setQc: useSetReturnQc() }), { wrapper })
    await waitFor(() => expect(result.current.list.data?.[0].lines[0].qcResult).toBe('Pending'))

    await result.current.setQc.mutateAsync({ id: 'r1', lineId: 'rl1', req: { result: 'Defect' } })

    await waitFor(() => expect(result.current.list.data?.[0].lines[0].qcResult).toBe('Defect'))
  })

  it('zeigt den neuen QC-Status und den Button "Verarbeiten" ohne Seiten-Reload', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ReturnsPage />)

    await user.click(await screen.findByRole('button', { name: 'Details' }))
    expect(screen.queryByRole('button', { name: /Verarbeiten/ })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Defect' }))

    // Alle Zeilen geprüft → "Verarbeiten" erscheint; vorher blieb die Seite auf "Pending" stehen.
    expect(await screen.findByRole('button', { name: /Verarbeiten/ })).toBeInTheDocument()
    expect(api.calls('PUT', '/returns/r1/lines/rl1/qc')[0].body).toMatchObject({ result: 'Defect' })
  })

  it('zeigt Fehler der QC-Aktion an, statt sie zu verschlucken', async () => {
    api.setRoute('PUT /returns/:id/lines/:lineId/qc', fail(400, { error: 'Retoure ist bereits verarbeitet' }))
    const user = userEvent.setup()
    renderWithProviders(<ReturnsPage />)

    await user.click(await screen.findByRole('button', { name: 'Details' }))
    await user.click(screen.getByRole('button', { name: 'Defect' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Retoure ist bereits verarbeitet')
  })
})
