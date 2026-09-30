import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AxiosError } from 'axios'
import { configure, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { downloadBackup, type BackupFileDto } from '../../api/systemHooks'
import { SystemPage } from '../../features/system/SystemPage'
import { downloadFile } from '../../lib/download'
import { formatDateTime } from '../../lib/format'
import { useAuth } from '../../state/auth'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'
import { makeBackup, makeSettings, NAME_A, NAME_B } from './fixtures'

// Die Seiten-Tests tippen und suchen per Rolle: bei voll ausgelasteter Maschine (parallele Testläufe) brauchen sie länger als die Standardfristen.
configure({ asyncUtilTimeout: 5000 })
vi.setConfig({ testTimeout: 30_000 })

// Der Download geht über den Blob-Weg des Browsers (URL.createObjectURL, Link-Klick): in jsdom ersetzt, geprüft wird der Aufruf.
vi.mock('../../lib/download', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../lib/download')>()),
  downloadFile: vi.fn(),
}))

describe('System-Seite: Backup-Liste', () => {
  let api: MockApi
  let files: BackupFileDto[]

  beforeEach(() => {
    files = [makeBackup(NAME_A), makeBackup(NAME_B, { kind: 'before-restore', sizeBytes: 3 * 1024 * 1024, createdUtc: '2026-09-29T10:15:00.250Z' })]
    api = installMockApi({
      'GET /admin/backup-settings': () => makeSettings(),
      'GET /admin/backups': () => files,
    })
    signInAs(['Admin'])
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('listet die Backups mit Name, Art, Größe und Zeit und zeigt Zeitplan, nächsten Lauf, Aufbewahrung und Restore-Status', async () => {
    renderWithProviders(<SystemPage />)

    const rowA = (await screen.findByText(NAME_A)).closest('tr')!
    expect(within(rowA).getByText('Backup')).toBeInTheDocument()
    expect(within(rowA).getByText('1,5 KB')).toBeInTheDocument()
    expect(within(rowA).getByText(formatDateTime('2026-09-30T02:00:00Z'))).toBeInTheDocument()

    const rowB = screen.getByText(NAME_B).closest('tr')!
    expect(within(rowB).getByText('Vor Restore')).toBeInTheDocument()
    expect(within(rowB).getByText('3 MB')).toBeInTheDocument()

    // Einstellungen (nur lesen)
    expect(screen.getByText('täglich 02:00 UTC')).toBeInTheDocument()
    expect(screen.getByText(formatDateTime('2026-10-01T02:00:00Z'))).toBeInTheDocument()
    expect(screen.getByText('die letzten 14 Backups')).toBeInTheDocument()
    expect(screen.getByText('freigegeben')).toBeInTheDocument()
    // Hinweis auf die Unverschlüsseltheit und das Kopieren auf ein anderes Medium
    expect(screen.getByRole('note')).toBeInTheDocument()
    expect(screen.getByText(/unverschlüsselt/)).toBeInTheDocument()
  })

  it('zeigt bei leerer Liste einen Hinweis statt einer leeren Tabelle', async () => {
    files = []
    renderWithProviders(<SystemPage />)

    expect(await screen.findByText(/Noch keine Backups vorhanden/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('"Backup jetzt erstellen" legt ein Backup an und die Liste zeigt es danach', async () => {
    const created = 'lager-backup-20261001-080000-000.db'
    api.setRoute('POST /admin/backups', () => {
      const file = makeBackup(created, { createdUtc: '2026-10-01T08:00:00Z' })
      files = [file, ...files]
      return file
    })
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)
    await screen.findByText(NAME_A)

    await user.click(screen.getByRole('button', { name: 'Backup jetzt erstellen' }))

    expect(await screen.findByText(created)).toBeInTheDocument()
    expect(api.calls('POST', '/admin/backups')).toHaveLength(1)
  })

  it('zeigt den Fehler eines fehlgeschlagenen Backups (z. B. Platte voll) als Meldung', async () => {
    api.setRoute('POST /admin/backups', fail(500, { title: 'Internal Server Error', status: 500, detail: 'Kein Speicherplatz mehr.', code: 'internal_error' }))
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)
    await screen.findByText(NAME_A)

    await user.click(screen.getByRole('button', { name: 'Backup jetzt erstellen' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Backup fehlgeschlagen:')
    expect(alert).toHaveTextContent('Kein Speicherplatz mehr.')
  })

  it('lädt ein Backup über den authentifizierten Download herunter und meldet Fehler', async () => {
    vi.mocked(downloadFile).mockResolvedValueOnce(undefined)
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)
    await screen.findByText(NAME_A)

    await user.click(screen.getByRole('button', { name: `Herunterladen: ${NAME_A}` }))
    await waitFor(() => expect(downloadFile).toHaveBeenCalledWith(`/admin/backups/${NAME_A}`, NAME_A))

    vi.mocked(downloadFile).mockRejectedValueOnce(Object.assign(new AxiosError('nicht gefunden'), { response: { status: 404 } }))
    await user.click(screen.getByRole('button', { name: `Herunterladen: ${NAME_B}` }))
    expect(await screen.findByText('Datei nicht gefunden.')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Download fehlgeschlagen:')
  })

  it('kodiert den Backup-Namen im Download-Pfad', async () => {
    vi.mocked(downloadFile).mockResolvedValueOnce(undefined)

    await downloadBackup('a b/c.db')

    expect(downloadFile).toHaveBeenCalledWith('/admin/backups/a%20b%2Fc.db', 'a b/c.db')
  })

  it('löscht erst nach der Bestätigung; "Abbrechen" löscht nichts', async () => {
    api.setRoute('DELETE /admin/backups/:name', (request) => {
      files = files.filter((f) => request.path !== `/admin/backups/${f.name}`)
      return undefined
    })
    const user = userEvent.setup()
    renderWithProviders(<SystemPage />)
    await screen.findByText(NAME_A)

    await user.click(screen.getByRole('button', { name: `Löschen: ${NAME_A}` }))
    let dialog = screen.getByRole('dialog', { name: 'Backup löschen?' })
    expect(dialog).toHaveTextContent(NAME_A)
    await user.click(within(dialog).getByRole('button', { name: 'Abbrechen' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(api.calls('DELETE', '/admin/backups/:name')).toHaveLength(0)

    // Die Sicherheitskopie aus einem Restore trägt einen zusätzlichen Hinweis.
    await user.click(screen.getByRole('button', { name: `Löschen: ${NAME_B}` }))
    dialog = screen.getByRole('dialog', { name: 'Backup löschen?' })
    expect(dialog).toHaveTextContent('Sicherheitskopie aus einem früheren Restore')
    await user.click(within(dialog).getByRole('button', { name: 'Löschen' }))

    await waitFor(() => expect(screen.queryByText(NAME_B)).not.toBeInTheDocument())
    expect(api.calls('DELETE', '/admin/backups/:name').map((r) => r.path)).toEqual([`/admin/backups/${NAME_B}`])
    expect(screen.getByText(NAME_A)).toBeInTheDocument()
  })

  it('sperrt den Restore mit Hinweis, solange er nicht freigegeben ist (Produktion ohne Backup:AllowRestore)', async () => {
    api.setRoute('GET /admin/backup-settings', makeSettings({ allowRestore: false, restoreAllowed: false }))
    renderWithProviders(<SystemPage />)
    await screen.findByText(NAME_A)

    expect(screen.getByText('gesperrt')).toBeInTheDocument()
    expect(screen.getByText(/Backup__AllowRestore=true/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: `Wiederherstellen: ${NAME_A}` })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Backup-Datei einspielen…' })).toBeDisabled()
    // Backups erstellen, herunterladen und löschen bleiben möglich
    expect(screen.getByRole('button', { name: 'Backup jetzt erstellen' })).toBeEnabled()
    expect(screen.getByRole('button', { name: `Herunterladen: ${NAME_A}` })).toBeEnabled()
  })

  it('meldet einen ungültigen Zeitplan und zeigt ohne Zeitplan den Hinweis auf Backup__Schedule', async () => {
    api.setRoute('GET /admin/backup-settings', makeSettings({ schedule: '25:99', scheduleValid: false, nextRunUtc: null }))
    const { unmount } = renderWithProviders(<SystemPage />)

    expect(await screen.findByText('ungültig')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Der Zeitplan „25:99“ ist ungültig')
    unmount()

    api.setRoute('GET /admin/backup-settings', makeSettings({ schedule: null, nextRunUtc: null, retentionCount: 0 }))
    renderWithProviders(<SystemPage />)
    expect(await screen.findByText('aus')).toBeInTheDocument()
    expect(screen.getByText(/Kein Zeitplan eingestellt/)).toBeInTheDocument()
    expect(screen.getByText('unbegrenzt')).toBeInTheDocument()
  })

  it('zeigt bei einem Ladefehler der Einstellungen eine Meldung mit "Erneut versuchen" statt einer leeren Seite', async () => {
    api.setRoute('GET /admin/backup-settings', fail(500, { title: 'Internal Server Error', status: 500 }))
    renderWithProviders(<SystemPage />)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Konnte die Backup-Einstellungen nicht laden.')
    expect(within(alert).getByRole('button', { name: 'Erneut versuchen' })).toBeInTheDocument()
  })
})

describe('System-Seite: MySQL', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /admin/backup-settings': () => makeSettings({
        provider: 'MySql',
        supported: false,
        unsupportedReason: 'mysql',
        mysqlDumpCommand: 'mysqldump --single-transaction -h <host> -u <benutzer> -p <datenbank> > lager-backup.sql',
        mysqlRestoreCommand: 'mysql -h <host> -u <benutzer> -p <datenbank> < lager-backup.sql',
        schedule: null,
        nextRunUtc: null,
      }),
      'GET /admin/backups': fail(400, { code: 'sqlite_only', detail: 'Backup-Endpoint ist aktuell nur für SQLite implementiert.' }),
    })
    signInAs(['Admin'])
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('zeigt statt eines 400-Fehlers den mysqldump-Aufruf, ohne Liste, Erstellen-Knopf oder Backup-Abruf', async () => {
    renderWithProviders(<SystemPage />)

    expect(await screen.findByRole('heading', { name: 'MySQL: Sicherung mit mysqldump' })).toBeInTheDocument()
    expect(screen.getByText(/^mysqldump --single-transaction/)).toBeInTheDocument()
    expect(screen.getByText(/^mysql -h/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Backup jetzt erstellen' })).not.toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(api.calls('GET', '/admin/backups')).toHaveLength(0)
  })
})
