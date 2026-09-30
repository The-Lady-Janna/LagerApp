import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClientProvider, useMutation, useQuery } from '@tanstack/react-query'
import { apiClient } from '../../api/client'
import { ErrorBanner } from '../../components/ErrorBanner'
import { ToastHost } from '../../components/Toast'
import { queryClient } from '../../lib/queryClient'
import { showToast, useToasts } from '../../state/toasts'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'

// Der echte QueryClient der App (QueryCache/MutationCache.onError → Toast) mit dem echten ToastHost.

const conflict = {
  type: 'about:blank', title: 'Conflict', status: 409,
  detail: 'Die Bestellung wurde bereits storniert.', code: 'order_not_cancellable', correlationId: 'corr-4711',
}

function CancelButton({ silent = false }: { silent?: boolean }) {
  const cancel = useMutation({
    mutationFn: async () => (await apiClient.post('/orders/o1/cancel')).data,
    meta: silent ? { silent: true } : undefined,
  })
  return <button onClick={() => cancel.mutate()}>Stornieren</button>
}

function renderApp(children: React.ReactNode) {
  return render(
    <QueryClientProvider client={queryClient}>
      {children}
      <ToastHost />
    </QueryClientProvider>,
  )
}

describe('Globale Fehler-Toasts', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({ 'POST /orders/:id/cancel': fail(409, conflict) })
  })
  afterEach(() => {
    api.restore()
    queryClient.clear()
    useToasts.getState().clear()
  })

  it('zeigt einen fehlgeschlagenen 409 als deutschen Fehlertext im Toast (aria-live, role=alert) — samt Referenz', async () => {
    const user = userEvent.setup()
    renderApp(<CancelButton />)

    await user.click(screen.getByRole('button', { name: 'Stornieren' }))

    const toast = await screen.findByRole('alert')
    expect(toast).toHaveTextContent('Aktion fehlgeschlagen')
    expect(toast).toHaveTextContent('Die Bestellung wurde bereits storniert.')
    expect(toast).toHaveTextContent('Referenz: corr-4711')
    expect(toast).not.toHaveTextContent('AxiosError')
    expect(toast.closest('[aria-live]')).not.toBeNull()
  })

  it('meta.silent unterdrückt den Toast (Aufrufer mit eigener Fehleranzeige)', async () => {
    const user = userEvent.setup()
    renderApp(<CancelButton silent />)

    await user.click(screen.getByRole('button', { name: 'Stornieren' }))

    await waitFor(() => expect(api.calls('POST', '/orders/o1/cancel')).toHaveLength(1))
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 20)) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(useToasts.getState().toasts).toHaveLength(0)
  })

  it('meldet 401 nicht per Toast (Login-Screen übernimmt) und fasst identische Fehler zusammen', async () => {
    const user = userEvent.setup()
    renderApp(<CancelButton />)

    api.setRoute('POST /orders/:id/cancel', fail(401, null))
    await user.click(screen.getByRole('button', { name: 'Stornieren' }))
    await waitFor(() => expect(api.calls('POST', '/orders/o1/cancel')).toHaveLength(1))
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 20)) })
    expect(useToasts.getState().toasts).toHaveLength(0)

    api.setRoute('POST /orders/:id/cancel', fail(409, conflict))
    await user.click(screen.getByRole('button', { name: 'Stornieren' }))
    await screen.findByRole('alert')
    await user.click(screen.getByRole('button', { name: 'Stornieren' }))
    await waitFor(() => expect(api.calls('POST', '/orders/o1/cancel')).toHaveLength(3))

    // Zweimal derselbe Fehler: EIN Toast mit Zähler statt zwei gestapelter Meldungen.
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('(×2)'))
    expect(screen.getAllByRole('alert')).toHaveLength(1)
  })

  it('meldet fehlgeschlagene Queries ("Daten konnten nicht geladen werden") und Netzwerkausfälle verständlich', async () => {
    api.setRoute('GET /suppliers', fail(404, null))
    function Suppliers() {
      const q = useQuery({ queryKey: ['wp15-suppliers'], queryFn: async () => (await apiClient.get('/suppliers')).data })
      return <p>{q.status}</p>
    }
    renderApp(<Suppliers />)

    const toast = await screen.findByRole('alert')
    expect(toast).toHaveTextContent('Daten konnten nicht geladen werden')
    expect(toast).toHaveTextContent('Der Datensatz wurde nicht gefunden.')
  })

  it('nimmt den Toast zurück, wenn die Seite denselben Fehler selbst als ErrorBanner zeigt (keine Doppelmeldung)', async () => {
    const user = userEvent.setup()
    function WithBanner() {
      const cancel = useMutation({ mutationFn: async () => (await apiClient.post('/orders/o1/cancel')).data })
      return (
        <>
          <button onClick={() => cancel.mutate()}>Stornieren</button>
          <ErrorBanner error={cancel.error} title="Stornieren fehlgeschlagen:" />
        </>
      )
    }
    renderApp(<WithBanner />)

    await user.click(screen.getByRole('button', { name: 'Stornieren' }))

    const banner = await screen.findByText(/Stornieren fehlgeschlagen:/)
    expect(banner.closest('[role="alert"]')).toHaveTextContent('Die Bestellung wurde bereits storniert.')
    await waitFor(() => expect(useToasts.getState().toasts).toHaveLength(0))
    expect(screen.getAllByRole('alert')).toHaveLength(1)
  })

  it('Toasts lassen sich schließen und verschwinden von selbst (Erfolg nach 4 s)', async () => {
    const user = userEvent.setup()
    renderApp(<CancelButton />)
    await user.click(screen.getByRole('button', { name: 'Stornieren' }))
    await screen.findByRole('alert')
    await user.click(screen.getByRole('button', { name: 'Meldung schließen' }))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()

    vi.useFakeTimers()
    try {
      act(() => { showToast('success', 'Gespeichert.') })
      expect(screen.getByRole('status')).toHaveTextContent('Gespeichert.')
      act(() => { vi.advanceTimersByTime(4100) })
      expect(screen.queryByRole('status')).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })
})
