import type { ReactElement } from 'react'
import { render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { useAuth, type AuthUser } from '../../state/auth'

/** QueryClient für Tests: keine Wiederholungen, damit Fehler sofort sichtbar sind. */
export function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 30_000, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  })
}

/** Rendert eine Seite/Route mit QueryClient und MemoryRouter (Startpfad per initialEntries). */
export function renderWithProviders(
  ui: ReactElement,
  options: { initialEntries?: Parameters<typeof MemoryRouter>[0]['initialEntries']; queryClient?: QueryClient } = {},
) {
  const queryClient = options.queryClient ?? createTestQueryClient()
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={options.initialEntries ?? ['/']}>{ui}</MemoryRouter>
    </QueryClientProvider>,
  )
  return { queryClient, ...utils }
}

/** Meldet einen Testnutzer mit den Rollen im Auth-Store an (kein Netzwerk, kein Token-Parsing nötig). */
export function signInAs(roles: string[], overrides: Partial<AuthUser> = {}): void {
  useAuth.getState().setSession('test-token', new Date(Date.now() + 60 * 60 * 1000).toISOString(), {
    id: 'u-test',
    username: 'tester',
    email: null,
    displayName: null,
    roles,
    isActive: true,
    mustChangePassword: false,
    ...overrides,
  })
}
