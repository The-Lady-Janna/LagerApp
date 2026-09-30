import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { configure, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AxiosResponse, InternalAxiosRequestConfig } from 'axios'
import { apiClient } from '../../api/client'
import { IMPORT_MAX_BYTES } from '../../api/importExportHooks'
import { ImportDialog } from '../../features/importexport/ImportDialog'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { renderWithProviders } from '../helpers/render'
import { csvFile, makeApplied, makeResult, makeResultWithErrors } from './fixtures'

// Die Dialog-Tests laden Dateien und tippen per Rolle: bei voll ausgelasteter Maschine brauchen sie länger als die Standardfristen.
configure({ asyncUtilTimeout: 5000 })
vi.setConfig({ testTimeout: 30_000 })

/**
 * Hält die Übernahme an, bis der Test sie freigibt: die Oberfläche sieht den laufenden Aufruf ("pending"), solange er wartet.
 * Der Wrapper liegt über dem Mock-Adapter; `api.restore()` räumt beides ab. Liefert die Freigabe.
 */
function holdApply(): () => void {
  let release: () => void = () => {}
  const gate = new Promise<void>((resolve) => { release = resolve })
  const inner = apiClient.defaults.adapter as unknown as (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>
  apiClient.defaults.adapter = async (config) => {
    if ((config.url ?? '').includes('dryRun=false')) await gate
    return inner(config)
  }
  return () => release()
}

/** Die Import-Aufrufe des Tests mit ihrem Query-String. */
const posts = (api: MockApi, kind = 'articles') => api.calls('POST', `/import/${kind}`)

describe('Import-Dialog: Prüfen, dann Übernehmen', () => {
  let api: MockApi
  const onClose = vi.fn()

  beforeEach(() => {
    onClose.mockClear()
    api = installMockApi({
      'POST /import/articles': (request) => (request.query.includes('dryRun=true') ? makeResultWithErrors() : makeApplied()),
    })
  })
  afterEach(() => api.restore())

  it('prüft zuerst (Trockenlauf, nichts wird übernommen) und zeigt pro Zeile die Fehler samt Zusammenfassung', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })

    // ohne Datei gibt es nichts zu prüfen
    const check = within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' })
    expect(check).toBeDisabled()
    expect(within(dialog).queryByRole('button', { name: 'Übernehmen' })).not.toBeInTheDocument()

    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())
    expect(check).toBeEnabled()
    await user.click(check)

    // Zusammenfassung "x neu, y aktualisiert, z Fehler" und die Fehlertabelle mit Zeile, Schlüssel und Text
    expect(await within(dialog).findByText('2 neu, 1 aktualisiert, 2 Fehler')).toBeInTheDocument()
    const table = within(dialog).getByRole('table', { name: 'Zeilenfehler' })
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows).toHaveLength(2)
    expect(rows[0]).toHaveTextContent('3SKU-XPrüfziffer der GTIN stimmt nicht (erwartet 1).')
    expect(rows[1]).toHaveTextContent('5—Die SKU fehlt.')

    // genau EIN Aufruf, und zwar der Trockenlauf mit Datei und Trennzeichen; geschrieben wurde nichts
    expect(posts(api)).toHaveLength(1)
    expect(posts(api)[0].query).toBe('dryRun=true&delimiter=semicolon')
    const form = posts(api)[0].body as FormData
    expect(form).toBeInstanceOf(FormData)
    expect((form.get('file') as File).name).toBe('artikel.csv')
    expect(within(dialog).queryByText('Import abgeschlossen.')).not.toBeInTheDocument()
  })

  it('übernimmt bei Zeilenfehlern erst, wenn die fehlerhaften Zeilen ausdrücklich ausgelassen werden', async () => {
    const user = userEvent.setup()
    const { queryClient } = renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))
    await within(dialog).findByText('2 neu, 1 aktualisiert, 2 Fehler')

    const apply = within(dialog).getByRole('button', { name: 'Übernehmen' })
    expect(apply).toBeDisabled()
    expect(apply).toHaveAttribute('title', expect.stringContaining('Zeilenfehler'))

    await user.click(within(dialog).getByRole('checkbox', { name: 'Fehlerhafte Zeilen auslassen und die übrigen 3 übernehmen' }))
    expect(apply).toBeEnabled()
    await user.click(apply)

    expect(await within(dialog).findByText('Import abgeschlossen.')).toBeInTheDocument()
    expect(dialog).toHaveTextContent('Übernommen: 2 neu, 1 aktualisiert, 0 Fehler.')
    expect(dialog).toHaveTextContent('ein Sammel-Eintrag')

    // der zweite Aufruf ist die Übernahme mit skipErrors, mit derselben Datei
    expect(posts(api)).toHaveLength(2)
    expect(posts(api)[1].query).toBe('dryRun=false&delimiter=semicolon&skipErrors=true')
    expect(((posts(api)[1].body as FormData).get('file') as File).name).toBe('artikel.csv')

    // die Artikel-Listen und der Audit-Trail laden danach neu
    const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify((filters as { queryKey: unknown }).queryKey))
    expect(keys).toEqual(expect.arrayContaining([JSON.stringify(['articles']), JSON.stringify(['audit'])]))

    await user.click(within(dialog).getByRole('button', { name: 'Schließen' }))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('ohne Fehler genügt ein Klick auf Übernehmen; ein Trockenlauf allein lädt nichts neu', async () => {
    api.setRoute('POST /import/articles', (request) => (request.query.includes('dryRun=true') ? makeResult() : makeApplied()))
    const user = userEvent.setup()
    const { queryClient } = renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })

    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))
    await within(dialog).findByText('2 neu, 1 aktualisiert, 0 Fehler')
    expect(invalidate).not.toHaveBeenCalled()                                   // der Trockenlauf hat nichts verändert
    expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument()     // keine Fehler: nichts auszulassen

    await user.click(within(dialog).getByRole('button', { name: 'Übernehmen' }))
    await within(dialog).findByText('Import abgeschlossen.')
    expect(posts(api).map((p) => p.query)).toEqual(['dryRun=true&delimiter=semicolon', 'dryRun=false&delimiter=semicolon'])
    expect(invalidate).toHaveBeenCalled()
  })

  it('übernimmt nie ohne vorherige Prüfung, und "Abbrechen" schreibt nichts', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())

    expect(within(dialog).queryByRole('button', { name: 'Übernehmen' })).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Abbrechen' }))

    expect(onClose).toHaveBeenCalledTimes(1)
    expect(posts(api)).toHaveLength(0)
  })

  it('verwirft die Prüfung, sobald Datei oder Trennzeichen wechseln: übernommen wird nur, was geprüft wurde', async () => {
    api.setRoute('POST /import/articles', makeResult())
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile('eins.csv'))
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))
    await within(dialog).findByRole('button', { name: 'Übernehmen' })

    await user.selectOptions(within(dialog).getByLabelText('Trennzeichen'), 'comma')
    expect(within(dialog).queryByRole('button', { name: 'Übernehmen' })).not.toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' })).toBeEnabled()

    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))
    await within(dialog).findByRole('button', { name: 'Übernehmen' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile('zwei.csv'))
    expect(within(dialog).queryByRole('button', { name: 'Übernehmen' })).not.toBeInTheDocument()

    // das gewählte Komma ging mit der zweiten Prüfung an den Server
    expect(posts(api).map((p) => p.query)).toEqual(['dryRun=true&delimiter=semicolon', 'dryRun=true&delimiter=comma'])
  })

  it('übernimmt die Wahl der Seite als Trennzeichen und erlaubt sie zu ändern', async () => {
    api.setRoute('POST /import/stock', makeResult({ kind: 'stock' }))
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="stock" delimiter="comma" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Bestand importieren' })

    expect(within(dialog).getByLabelText('Trennzeichen')).toHaveValue('comma')
    expect(dialog).toHaveTextContent('Pflichtspalten: Sku, Location (Lagerplatz), Quantity')
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile('bestand.csv'))
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))
    await within(dialog).findByText('2 neu, 1 aktualisiert, 0 Fehler')

    expect(posts(api, 'stock')[0].query).toBe('dryRun=true&delimiter=comma')
  })

  it('weist eine zu große Datei schon vor dem Upload ab', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    const big = csvFile('gross.csv')
    Object.defineProperty(big, 'size', { value: IMPORT_MAX_BYTES + 1 })

    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), big)

    expect(within(dialog).getByRole('alert')).toHaveTextContent('größer als erlaubt (5 MB)')
    expect(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' })).toBeDisabled()
    expect(posts(api)).toHaveLength(0)
  })

  it('zeigt einen Fehler der ganzen Datei (Server antwortet 400) als Meldung und keine Ergebnisansicht', async () => {
    api.setRoute('POST /import/articles', () => fail(400, {
      status: 400, code: 'import_missing_column', detail: "Die Pflichtspalte 'Sku' fehlt in der Kopfzeile. Gefundene Spalten: 'Artikelnummer'.",
      correlationId: 'corr-1',
    }))
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))

    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent("Die Pflichtspalte 'Sku' fehlt in der Kopfzeile.")
    expect(alert).toHaveTextContent('Referenz: corr-1')
    expect(within(dialog).queryByRole('region', { name: 'Ergebnis der Prüfung' })).not.toBeInTheDocument()
    // nach dem Fehler lässt sich erneut prüfen
    expect(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' })).toBeEnabled()
  })

  it('sperrt Übernehmen, wenn nichts zu schreiben ist, und nennt den Grund', async () => {
    api.setRoute('POST /import/articles', makeResult({
      created: 0, updated: 0, unchanged: 3, summary: '0 neu, 0 aktualisiert, 0 Fehler',
      message: 'Trockenlauf: 0 neu, 0 aktualisiert, 0 Fehler (3 unverändert). Es wurde nichts geschrieben.',
    }))
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))

    const apply = await within(dialog).findByRole('button', { name: 'Übernehmen' })
    expect(apply).toBeDisabled()
    expect(apply).toHaveAttribute('title', 'Es gibt nichts zu übernehmen: alles steht schon so in der Datenbank.')
    expect(within(dialog).getByText('Unverändert').nextElementSibling).toHaveTextContent('3')
  })

  it('zeigt Warnungen zu unbekannten Spalten und kürzt eine lange Fehlerliste mit Hinweis', async () => {
    const base = makeResultWithErrors()
    api.setRoute('POST /import/articles', makeResult({
      ...base,
      warnings: ["Die Spalte 'Mindestbestand' ist unbekannt und wird ignoriert."],
      errorCount: 1200,
      errorsTruncated: true,
    }))
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))

    expect(await within(dialog).findByText(/Mindestbestand/)).toBeInTheDocument()
    expect(dialog).toHaveTextContent('Es werden die ersten 2 von 1.200 Fehlern gezeigt.')
  })

  it('sperrt während der Übernahme alles (kein zweiter Klick, Abbrechen und Escape wirken nicht)', async () => {
    api.setRoute('POST /import/articles', (request) => (request.query.includes('dryRun=true') ? makeResult() : makeApplied()))
    const release = holdApply()
    const user = userEvent.setup()
    renderWithProviders(<ImportDialog kind="articles" delimiter="semicolon" onClose={onClose} />)
    const dialog = screen.getByRole('dialog', { name: 'Artikel importieren' })
    await user.upload(within(dialog).getByLabelText(/CSV-Datei/), csvFile())
    await user.click(within(dialog).getByRole('button', { name: 'Prüfen (Trockenlauf)' }))
    await user.click(await within(dialog).findByRole('button', { name: 'Übernehmen' }))

    // die Übernahme läuft: Knopf und Abbrechen sind gesperrt, Escape schließt nicht
    const running = await within(dialog).findByRole('button', { name: 'Übernehme…' })
    expect(running).toBeDisabled()
    expect(within(dialog).getByRole('button', { name: 'Abbrechen' })).toBeDisabled()
    await user.click(running)
    await user.keyboard('{Escape}')
    expect(onClose).not.toHaveBeenCalled()

    release()
    await within(dialog).findByText('Import abgeschlossen.')
    expect(posts(api).filter((p) => p.query.includes('dryRun=false'))).toHaveLength(1)
  })
})
