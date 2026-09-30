/// <reference types="node" />
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { readdirSync, readFileSync } from 'node:fs'
import { join, resolve as resolvePath } from 'node:path'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { PASSWORD_MAX_BYTES, PASSWORD_MIN_LENGTH, type UserDto } from '../../api/types'
import { ChangePasswordDialog } from '../../components/ChangePasswordDialog'
import { UsersPage } from '../../pages/UsersPage'
import { useAuth } from '../../state/auth'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner (paralleler dotnet test, CI) reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// WP33: Die Hinweise nannten als Mindestlänge die 8, der Server (PasswordPolicy, WP01) verlangt mindestens 10 (höchstens 72 Bytes).
// Wer dem Hinweis folgte, lief in die Ablehnung des Servers. Die Oberfläche nennt und prüft jetzt dieselbe Länge.

const ten = 'abcdefghij'
const nine = 'abcdefghi'

// Der frühere Hinweis ("8" und "Zeichen" hintereinander) als Muster: kein Passwort-Hinweis der Oberfläche darf so wieder auftauchen.
const OLD_LENGTH_HINT = /\b8\s+Zeichen/

describe('Passwort-Regeln der Oberfläche = Regeln des Servers', () => {
  it('die Konstanten stehen auf 10 Zeichen und 72 Bytes (PasswordPolicy.MinLength / MaxBytes)', () => {
    expect(PASSWORD_MIN_LENGTH).toBe(10)
    expect(PASSWORD_MAX_BYTES).toBe(72)
  })

  it('kein Quelltext der Oberfläche nennt mehr die alte Mindestlänge 8', () => {
    const src = resolvePath(process.cwd(), 'src')
    const offenders: string[] = []
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name)
        if (entry.isDirectory()) {
          if (entry.name !== 'tests') walk(path)
        } else if (/\.tsx?$/.test(entry.name) && OLD_LENGTH_HINT.test(readFileSync(path, 'utf8'))) {
          offenders.push(path)
        }
      }
    }
    walk(src)
    expect(offenders).toEqual([])
  }, 30_000) // Dateizugriffe sind auf langsamen Rechnern (Virenscanner) gelegentlich träge
})

describe('ChangePasswordDialog: Mindestlänge 10', () => {
  let api: MockApi

  beforeEach(() => {
    signInAs(['Viewer'])
    // Alter Vertrag (204 ohne Body): der Dialog meldet danach ab.
    api = installMockApi({ 'POST /auth/change-password': null })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  async function fill(current: string, next: string) {
    const user = userEvent.setup()
    renderWithProviders(<ChangePasswordDialog onClose={() => {}} />)
    // Einfügen statt Tippen: Zeichen für Zeichen (40 Zeichen x 3 Felder) dauert unter Last länger als das Test-Timeout.
    const fields = [
      [screen.getByLabelText('Aktuelles Passwort'), current],
      [screen.getByLabelText(/^Neues Passwort \(≥/), next],
      [screen.getByLabelText('Neues Passwort (wiederholen)'), next],
    ] as const
    for (const [field, text] of fields) {
      await user.click(field)
      await user.paste(text)
    }
    await user.click(screen.getByRole('button', { name: 'Ändern' }))
  }

  it('nennt 10 Zeichen im Feld und die übrigen Regeln darunter', () => {
    renderWithProviders(<ChangePasswordDialog onClose={() => {}} />)

    expect(screen.getByLabelText('Neues Passwort (≥ 10 Zeichen)')).toBeInTheDocument()
    const dialog = screen.getByRole('dialog', { name: 'Passwort ändern' })
    expect(dialog).toHaveTextContent('Mindestens 10 Zeichen, höchstens 72 Bytes')
    expect(dialog).toHaveTextContent('nicht gleich dem Benutzernamen')
    expect(dialog.textContent).not.toMatch(OLD_LENGTH_HINT)
  })

  it('lehnt 9 Zeichen ab, ohne den Server zu fragen', async () => {
    await fill('altes-Passwort-1', nine)

    expect(await screen.findByRole('alert')).toHaveTextContent('Neues Passwort braucht mindestens 10 Zeichen.')
    expect(api.calls('POST', '/auth/change-password')).toHaveLength(0)
  })

  it('schickt genau 10 Zeichen an den Server', async () => {
    await fill('altes-Passwort-1', ten)

    await waitFor(() => expect(api.calls('POST', '/auth/change-password')).toHaveLength(1))
    expect(api.calls('POST', '/auth/change-password')[0].body).toEqual({ currentPassword: 'altes-Passwort-1', newPassword: ten })
  })

  it('lehnt mehr als 72 Bytes ab (40 Umlaute sind 40 Zeichen, aber 80 Bytes)', async () => {
    await fill('altes-Passwort-1', 'ä'.repeat(40))

    expect(await screen.findByRole('alert')).toHaveTextContent('höchstens 72 Bytes')
    expect(api.calls('POST', '/auth/change-password')).toHaveLength(0)
  })
})

describe('UsersPage: Passwort-Hinweise und Sperre bei weniger als 10 Zeichen', () => {
  let api: MockApi
  const user = (id: string, username: string): UserDto => ({
    id, username, email: null, displayName: null, roles: ['Viewer'], isActive: true, mustChangePassword: false, lastLoginAt: null, createdAt: '2025-01-01T10:00:00',
  })

  beforeEach(() => {
    signInAs(['Admin'], { id: 'u-me', username: 'chef' })
    api = installMockApi({
      'GET /users': [user('u-me', 'chef'), user('u-2', 'anna')],
      'POST /users': user('u-3', 'neuer'),
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('Neuer Benutzer: "Anlegen" bleibt bei 9 Zeichen gesperrt und wird bei 10 frei', async () => {
    const userEv = userEvent.setup()
    renderWithProviders(<UsersPage />)
    await userEv.click(await screen.findByRole('button', { name: '+ Neuer Benutzer' }))

    const dialog = screen.getByRole('dialog', { name: 'Neuer Benutzer' })
    expect(within(dialog).getByLabelText('Initial-Passwort (≥ 10 Zeichen)')).toBeInTheDocument()
    await userEv.type(within(dialog).getByLabelText('Username'), 'neuer')

    await userEv.type(within(dialog).getByLabelText(/^Initial-Passwort/), nine)
    expect(within(dialog).getByRole('button', { name: 'Anlegen' })).toBeDisabled()

    await userEv.type(within(dialog).getByLabelText(/^Initial-Passwort/), 'j')
    expect(within(dialog).getByRole('button', { name: 'Anlegen' })).toBeEnabled()

    await userEv.click(within(dialog).getByRole('button', { name: 'Anlegen' }))
    await waitFor(() => expect(api.calls('POST', '/users')).toHaveLength(1))
    expect(api.calls('POST', '/users')[0].body).toMatchObject({ username: 'neuer', password: ten })
  })

  it('Passwort zurücksetzen: "Zurücksetzen" bleibt bei 9 Zeichen gesperrt und wird bei 10 frei', async () => {
    api.setRoute('POST /users/:id/reset-password', {})
    const userEv = userEvent.setup()
    renderWithProviders(<UsersPage />)
    await screen.findByText('anna')
    await userEv.click(within(screen.getByText('anna').closest('tr') as HTMLElement).getByRole('button', { name: 'PW reset' }))

    const dialog = screen.getByRole('dialog', { name: /Passwort zurücksetzen/ })
    expect(within(dialog).getByLabelText('Neues Passwort (≥ 10 Zeichen)')).toBeInTheDocument()

    await userEv.type(within(dialog).getByLabelText(/^Neues Passwort/), nine)
    expect(within(dialog).getByRole('button', { name: 'Zurücksetzen' })).toBeDisabled()

    await userEv.type(within(dialog).getByLabelText(/^Neues Passwort/), 'j')
    expect(within(dialog).getByRole('button', { name: 'Zurücksetzen' })).toBeEnabled()
  })
})
