import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { configure, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AxiosResponse, InternalAxiosRequestConfig } from 'axios'
import { apiClient } from '../../api/client'
import type { RestoreResultDto } from '../../api/systemHooks'
import { SystemPage } from '../../features/system/SystemPage'
import { useAuth } from '../../state/auth'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'
import { makeBackup, makeSettings, NAME_A, NAME_B } from './fixtures'

// Die Seiten-Tests tippen und suchen per Rolle: bei voll ausgelasteter Maschine (parallele Testläufe) brauchen sie länger als die Standardfristen.
configure({ asyncUtilTimeout: 5000 })
vi.setConfig({ testTimeout: 30_000 })

const RESTORED: RestoreResultDto = {
  message: 'Restore erfolgreich. Bitte den API-Server jetzt neu starten — die DB-Connection muss neu aufgebaut werden.',
  restartRequired: true,
  sizeBytes: 4096,
  safetyBackup: 'lager-before-restore-20261001-090000-000.db',
}

/** Der Restore-Aufruf des Tests: das Multipart-Formular, das die Seite gesendet hat. */
function restoreForm(api: MockApi): FormData {
  const [request] = api.calls('POST', '/admin/restore')
  expect(request.body).toBeInstanceOf(FormData)
  return request.body as FormData
}

/**
 * Verzögert die Restore-Aufrufe um ein paar Millisekunden, wie ein echtes Netzwerk: die Oberfläche sieht den laufenden Aufruf
 * ("pending"), bevor die Antwort da ist. (Der Mock antwortet sonst so schnell, dass "läuft" und "fehlgeschlagen" in einem Render landen.)
 * Der Wrapper liegt über dem Mock-Adapter; `api.restore()` räumt beides ab.
 */
function slowRestore() {
  const inner = apiClient.defaults.adapter as unknown as (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>
  apiClient.defaults.adapter = async (config) => {
    if ((config.url ?? '').endsWith('/admin/restore')) await new Promise((resolve) => setTimeout(resolve, 25))
    return inner(config)
  }
}

describe('System-Seite: Restore mit Bestätigung', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /admin/backup-settings': () => makeSettings(),
      'GET /admin/backups': () => [makeBackup(NAME_A), makeBackup(NAME_B, { kind: 'before-restore' })],
      'POST /admin/restore': RESTORED,
    })
    signInAs(['Admin'])
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('stellt ein Backup erst wieder her, wenn genau "RESTORE" eingetippt ist, und verlangt danach den Neustart', async () => {
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)

    await user.click(await screen.findByRole('button', { name: `Wiederherstellen: ${NAME_A}` }))
    const dialog = screen.getByRole('dialog', { name: 'Datenbank wiederherstellen?' })

    // Deutlicher Warnhinweis, die gewählte Sicherung, aber noch kein Restore
    expect(dialog).toHaveTextContent('Die laufende Datenbank wird ersetzt')
    expect(dialog).toHaveTextContent('Sicherheitskopie „before-restore“')
    expect(dialog).toHaveTextContent('Server neu gestartet werden')
    expect(dialog).toHaveTextContent(NAME_A)
    const confirm = within(dialog).getByRole('button', { name: 'Jetzt wiederherstellen' })
    expect(confirm).toBeDisabled()
    const input = within(dialog).getByLabelText(/Zur Bestätigung/)
    expect(input).toHaveFocus()

    // Falsche Eingaben lassen den Knopf gesperrt (auch die andere Schreibweise)
    await user.type(input, 'restore')
    expect(confirm).toBeDisabled()
    await user.clear(input)
    await user.type(input, 'RESTOR')
    expect(confirm).toBeDisabled()
    await user.type(input, 'E')
    expect(confirm).toBeEnabled()
    expect(api.calls('POST', '/admin/restore')).toHaveLength(0)

    await user.click(confirm)

    // Der Server bekommt ein Multipart-Formular mit Backup-Name und Bestätigung, keine Datei
    await waitFor(() => expect(api.calls('POST', '/admin/restore')).toHaveLength(1))
    const form = restoreForm(api)
    expect(form.get('backupName')).toBe(NAME_A)
    expect(form.get('confirm')).toBe('RESTORE')
    expect(form.has('file')).toBe(false)

    // Danach: Dialog zu, Hinweis "Neustart erforderlich" mit der Sicherheitskopie, kein zweiter Restore bis zum Neustart
    const status = await screen.findByRole('status')
    expect(status).toHaveTextContent('Restore erfolgreich. Neustart erforderlich.')
    expect(status).toHaveTextContent(RESTORED.safetyBackup)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: `Wiederherstellen: ${NAME_A}` })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Backup-Datei einspielen…' })).toBeDisabled()
  })

  it('"Abbrechen" und Escape verwerfen den Restore, ohne den Server aufzurufen; ein neuer Dialog beginnt leer', async () => {
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)

    await user.click(await screen.findByRole('button', { name: `Wiederherstellen: ${NAME_B}` }))
    await user.type(within(screen.getByRole('dialog')).getByLabelText(/Zur Bestätigung/), 'RESTORE')
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Abbrechen' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: `Wiederherstellen: ${NAME_B}` }))
    const dialog = screen.getByRole('dialog', { name: 'Datenbank wiederherstellen?' })
    expect(within(dialog).getByLabelText(/Zur Bestätigung/)).toHaveValue('')
    expect(within(dialog).getByRole('button', { name: 'Jetzt wiederherstellen' })).toBeDisabled()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    expect(api.calls('POST', '/admin/restore')).toHaveLength(0)
  })

  it('spielt eine hochgeladene Datei erst ein, wenn Datei UND Bestätigung da sind', async () => {
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)

    await user.click(await screen.findByRole('button', { name: 'Backup-Datei einspielen…' }))
    const dialog = screen.getByRole('dialog', { name: 'Datenbank wiederherstellen?' })
    const confirm = within(dialog).getByRole('button', { name: 'Jetzt wiederherstellen' })
    const phrase = within(dialog).getByLabelText(/Zur Bestätigung/)

    // Nur die Bestätigung: ohne Datei bleibt der Knopf gesperrt
    await user.type(phrase, 'RESTORE')
    expect(confirm).toBeDisabled()

    const file = new File(['SQLite format 3\0 Inhalt'], 'mein-backup.db', { type: 'application/octet-stream' })
    await user.upload(within(dialog).getByLabelText(/Backup-Datei/), file)
    expect(confirm).toBeEnabled()

    await user.click(confirm)

    await waitFor(() => expect(api.calls('POST', '/admin/restore')).toHaveLength(1))
    const form = restoreForm(api)
    const sent = form.get('file')
    expect(sent).toBeInstanceOf(File)
    expect((sent as File).name).toBe('mein-backup.db')
    expect(form.get('confirm')).toBe('RESTORE')
    expect(form.has('backupName')).toBe(false)
    expect(await screen.findByText('Restore erfolgreich. Neustart erforderlich.')).toBeInTheDocument()
  })

  it('lehnt eine zu große Datei im Dialog ab, auch mit Bestätigung', async () => {
    api.setRoute('GET /admin/backup-settings', makeSettings({ maxRestoreBytes: 10 }))
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)

    await user.click(await screen.findByRole('button', { name: 'Backup-Datei einspielen…' }))
    const dialog = screen.getByRole('dialog', { name: 'Datenbank wiederherstellen?' })
    await user.type(within(dialog).getByLabelText(/Zur Bestätigung/), 'RESTORE')
    await user.upload(within(dialog).getByLabelText(/Backup-Datei/), new File(['x'.repeat(50)], 'gross.db', { type: 'application/octet-stream' }))

    expect(within(dialog).getByRole('alert')).toHaveTextContent('größer als erlaubt')
    expect(within(dialog).getByRole('button', { name: 'Jetzt wiederherstellen' })).toBeDisabled()
    expect(api.calls('POST', '/admin/restore')).toHaveLength(0)
  })

  it('zeigt einen Serverfehler im Dialog, lässt ihn offen und erlaubt einen zweiten Versuch', async () => {
    api.setRoute('POST /admin/restore', fail(409, {
      title: 'Conflict', status: 409, code: 'database_in_use', correlationId: 'corr-7',
      detail: 'Die Datenbankdatei ist noch in Benutzung; der Restore wurde nicht durchgeführt.',
    }))
    slowRestore()
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)

    await user.click(await screen.findByRole('button', { name: `Wiederherstellen: ${NAME_A}` }))
    const dialog = screen.getByRole('dialog', { name: 'Datenbank wiederherstellen?' })
    await user.type(within(dialog).getByLabelText(/Zur Bestätigung/), 'RESTORE')
    await user.click(within(dialog).getByRole('button', { name: 'Jetzt wiederherstellen' }))

    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent('Restore fehlgeschlagen:')
    expect(alert).toHaveTextContent('noch in Benutzung')
    expect(alert).toHaveTextContent('corr-7')
    expect(screen.getByRole('dialog', { name: 'Datenbank wiederherstellen?' })).toBeInTheDocument()
    expect(screen.queryByText('Restore erfolgreich. Neustart erforderlich.')).not.toBeInTheDocument()

    // Zweiter Versuch: jetzt klappt es
    api.setRoute('POST /admin/restore', RESTORED)
    const retry = within(dialog).getByRole('button', { name: 'Jetzt wiederherstellen' })
    await waitFor(() => expect(retry).toBeEnabled())
    await user.click(retry)

    expect(await screen.findByText('Restore erfolgreich. Neustart erforderlich.')).toBeInTheDocument()
    expect(api.calls('POST', '/admin/restore')).toHaveLength(2)
  })

  it('zeigt die Ablehnung einer ungültigen Datei durch den Server (400 invalid_backup_file)', async () => {
    api.setRoute('POST /admin/restore', fail(400, {
      title: 'Ungültige Anfrage', status: 400, code: 'invalid_backup_file',
      detail: 'Die Datei ist keine SQLite-Datenbank (Header \'SQLite format 3\' fehlt).',
    }))
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)

    await user.click(await screen.findByRole('button', { name: 'Backup-Datei einspielen…' }))
    const dialog = screen.getByRole('dialog', { name: 'Datenbank wiederherstellen?' })
    await user.upload(within(dialog).getByLabelText(/Backup-Datei/), new File(['kein sqlite'], 'notizen.db', { type: 'application/octet-stream' }))
    await user.type(within(dialog).getByLabelText(/Zur Bestätigung/), 'RESTORE')
    await user.click(within(dialog).getByRole('button', { name: 'Jetzt wiederherstellen' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('keine SQLite-Datenbank')
  })
})
