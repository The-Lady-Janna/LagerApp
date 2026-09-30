import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../../App'
import { useAuth } from '../../state/auth'
import { useTheme } from '../../state/theme'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'

// Zwei Test-Features wie sie unter src/features/<name>/route.tsx liegen würden — die Registry ist gemockt,
// App.tsx bleibt UNVERÄNDERT: Navigation und Routing müssen sich allein aus der Registry ergeben.
vi.mock('../../routes/registry', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../routes/registry')>()
  const { createElement } = await import('react')
  const page = (title: string) => () => createElement('h2', null, title)
  return {
    ...actual,
    featureRoutes: actual.collectFeatureRoutes({
      '../features/demo/route.tsx': {
        default: [
          { path: '/demo', label: 'Demo-Etiketten', group: 'bestand', roles: ['Admin'], element: page('Demo-Feature') },
          { path: '/demo/:id', element: page('Demo-Detail') },   // ohne Label: nur Route
        ],
      },
      '../features/offen/route.tsx': {
        default: { path: '/offen', label: 'Offene Seite', group: 'werkzeuge', groupLabel: 'Werkzeuge', element: page('Offene Seite') },
      },
    }),
  }
})

describe('App-Shell mit Route-Registry', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /auth/me': () => useAuth.getState().user,   // liefert den angemeldeten Testnutzer zurück (Rollen bleiben, wie von signInAs gesetzt)
      'GET /warehouse/layout': [],
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('zeigt ein Feature in Navigation und Routing, ohne dass App.tsx davon weiß', async () => {
    signInAs(['Admin'])
    renderWithProviders(<App />, { initialEntries: ['/demo'] })

    expect(await screen.findByRole('heading', { name: 'Demo-Feature' })).toBeInTheDocument()
    const nav = screen.getByRole('navigation', { name: 'Hauptnavigation' })
    expect(within(nav).getByRole('link', { name: 'Demo-Etiketten' })).toHaveAttribute('aria-current', 'page')   // aktive Seite
    expect(within(nav).getByRole('button', { name: 'Werkzeuge' })).toBeInTheDocument()                          // neue Gruppe aus der Registry
    expect(within(nav).getByRole('link', { name: 'Offene Seite' })).not.toHaveAttribute('aria-current')
  })

  it('bedient auch Detail-Routen ohne Navigationseintrag', async () => {
    signInAs(['Admin'])
    renderWithProviders(<App />, { initialEntries: ['/demo/42'] })

    expect(await screen.findByRole('heading', { name: 'Demo-Detail' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Demo-Detail' })).not.toBeInTheDocument()
  })

  it('filtert nach Rollen: Navigation blendet den Eintrag aus, die Route zeigt "Zugriff verweigert" (RequireRole)', async () => {
    signInAs(['Picker'])
    renderWithProviders(<App />, { initialEntries: ['/demo'] })

    expect(await screen.findByRole('heading', { name: 'Zugriff verweigert' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Demo-Feature' })).not.toBeInTheDocument()
    const nav = screen.getByRole('navigation', { name: 'Hauptnavigation' })
    expect(within(nav).queryByRole('link', { name: 'Demo-Etiketten' })).not.toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Offene Seite' })).toBeInTheDocument()   // ohne roles: für jeden
  })
})

describe('App-Shell: Skip-Link, Drawer und Theme-Wahl', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /auth/me': () => useAuth.getState().user,   // liefert den angemeldeten Testnutzer zurück (Rollen bleiben, wie von signInAs gesetzt)
      'GET /warehouse/layout': [],
    })
    signInAs(['Admin'])
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
    useTheme.setState({ theme: 'auto' })
    document.documentElement.removeAttribute('data-theme')
    document.documentElement.style.removeProperty('color-scheme')
  })

  it('bietet einen Skip-Link auf den Inhalt und ein fokussierbares main-Ziel', async () => {
    renderWithProviders(<App />, { initialEntries: ['/offen'] })
    await screen.findByRole('heading', { name: 'Offene Seite' })

    const skip = screen.getByRole('link', { name: 'Zum Inhalt springen' })
    expect(skip).toHaveAttribute('href', '#main-content')
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main-content')
    expect(screen.getByRole('main')).toHaveAttribute('tabindex', '-1')
  })

  it('klappt die Sidebar per Hamburger (aria-expanded/aria-controls) auf und zu; Escape schließt und gibt den Fokus zurück', async () => {
    const user = userEvent.setup()
    const { container } = renderWithProviders(<App />, { initialEntries: ['/offen'] })
    await screen.findByRole('heading', { name: 'Offene Seite' })
    const toggle = screen.getByRole('button', { name: 'Navigation' })
    const shell = container.querySelector('.app')!

    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(toggle).toHaveAttribute('aria-controls', 'app-sidebar')
    expect(document.getElementById('app-sidebar')).not.toBeNull()

    await user.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(shell).toHaveClass('app--nav-open')

    await user.keyboard('{Escape}')
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(shell).not.toHaveClass('app--nav-open')
    expect(toggle).toHaveFocus()
  })

  it('schließt den Drawer, sobald man navigiert', async () => {
    const user = userEvent.setup()
    const { container } = renderWithProviders(<App />, { initialEntries: ['/offen'] })
    await screen.findByRole('heading', { name: 'Offene Seite' })
    await user.click(screen.getByRole('button', { name: 'Navigation' }))
    expect(container.querySelector('.app')).toHaveClass('app--nav-open')

    await user.click(within(screen.getByRole('navigation', { name: 'Hauptnavigation' })).getByRole('link', { name: 'Demo-Etiketten' }))

    expect(await screen.findByRole('heading', { name: 'Demo-Feature' })).toBeInTheDocument()
    expect(container.querySelector('.app')).not.toHaveClass('app--nav-open')
  })

  it('klappt den Drawer nicht wieder auf, wenn man auf die Seite zurückkehrt, auf der er geöffnet wurde', async () => {
    const user = userEvent.setup()
    const { container } = renderWithProviders(<App />, { initialEntries: ['/offen'] })
    await screen.findByRole('heading', { name: 'Offene Seite' })
    const nav = () => within(screen.getByRole('navigation', { name: 'Hauptnavigation' }))
    await user.click(screen.getByRole('button', { name: 'Navigation' }))            // geöffnet auf /offen
    await user.click(nav().getByRole('link', { name: 'Demo-Etiketten' }))
    await screen.findByRole('heading', { name: 'Demo-Feature' })

    await user.click(nav().getByRole('link', { name: 'Offene Seite' }))            // zurück auf /offen (wie die Zurück-Taste)

    await screen.findByRole('heading', { name: 'Offene Seite' })
    expect(container.querySelector('.app')).not.toHaveClass('app--nav-open')
    expect(screen.getByRole('button', { name: 'Navigation' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('Escape in einem Dialog aus der Sidebar schließt nur den Dialog — der Drawer bleibt offen', async () => {
    const user = userEvent.setup()
    const { container } = renderWithProviders(<App />, { initialEntries: ['/offen'] })
    await screen.findByRole('heading', { name: 'Offene Seite' })
    await user.click(screen.getByRole('button', { name: 'Navigation' }))
    await user.click(screen.getByRole('button', { name: /Passwort ändern/ }))
    expect(screen.getByRole('dialog', { name: 'Passwort ändern' })).toBeInTheDocument()

    await user.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(container.querySelector('.app')).toHaveClass('app--nav-open')
  })

  it('markiert das gewählte Theme mit aria-pressed und setzt es auf <html> (data-theme + color-scheme)', async () => {
    const user = userEvent.setup()
    renderWithProviders(<App />, { initialEntries: ['/offen'] })
    await screen.findByRole('heading', { name: 'Offene Seite' })
    const group = screen.getByRole('group', { name: 'Farbschema' })

    await user.click(within(group).getByRole('button', { name: /dunkel/ }))

    expect(within(group).getByRole('button', { name: /dunkel/ })).toHaveAttribute('aria-pressed', 'true')
    expect(within(group).getByRole('button', { name: /hell/ })).toHaveAttribute('aria-pressed', 'false')
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.documentElement.style.colorScheme).toBe('dark')
  })
})
