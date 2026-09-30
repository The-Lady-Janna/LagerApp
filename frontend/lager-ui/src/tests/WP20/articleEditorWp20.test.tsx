import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Link, Route, Routes } from 'react-router-dom'
import { ArticleEditorPage } from '../../pages/ArticleEditorPage'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makeArticle, makeSupplier } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'

// Der ausgebaute Artikel-Editor (WP20): Bundle-Komponenten, Alternativ-SKUs, Saison-Fenster, GTIN mit Live-Prüfziffer-Hinweis,
// Konfliktmeldungen und die Warnung bei ungespeicherten Änderungen. Speichern sendet alle Felder unverändert oder geändert im PUT.
describe('ArticleEditorPage — vollständiger Editor', () => {
  let api: MockApi

  const self = makeArticle({ id: 'a1', sku: 'ART-001', name: 'Schraube M8' })
  const nut = makeArticle({ id: 'b1', sku: 'ART-002', name: 'Mutter M8' })
  const washer = makeArticle({ id: 'c1', sku: 'ART-003', name: 'Scheibe M8' })

  beforeEach(() => {
    api = installMockApi({
      'GET /suppliers': [makeSupplier()],
      'GET /articles': [self, nut, washer],
    })
  })
  afterEach(() => api.restore())

  const renderEditor = (path: string) =>
    renderWithProviders(
      <>
        <Link to="/articles">Seitenleiste: Artikel</Link>
        <Routes>
          <Route path="/articles" element={<p>Artikelliste</p>} />
          <Route path="/articles/new" element={<ArticleEditorPage />} />
          <Route path="/articles/:id" element={<ArticleEditorPage />} />
        </Routes>
      </>,
      { initialEntries: [path] },
    )

  const putBody = () => api.calls('PUT', '/articles/a1')[0].body as Record<string, unknown>
  const saveButton = () => screen.getByRole('button', { name: 'Speichern' })

  async function openArticle(article = self) {
    api.setRoute('GET /articles/a1', article)
    api.setRoute('PUT /articles/a1', article)
    const user = userEvent.setup()
    renderEditor('/articles/a1')
    const name = await screen.findByLabelText('Name')
    await waitFor(() => expect(name).toHaveValue(article.name))
    return user
  }

  // ---- Speichern sendet alles ------------------------------------------

  it('sendet Alt-SKUs, Saison-Fenster, Bundle-Komponenten und GTIN unverändert im PUT', async () => {
    const user = await openArticle(makeArticle({
      id: 'a1',
      sku: 'ART-001',
      name: 'Schraube M8',
      gtin: '4006381333931',
      alternativeSkus: ['ART-002'],
      validFrom: '2026-03-01T00:00:00',
      validUntil: '2026-09-30T00:00:00',
      isBundle: true,
      bundleComponents: [{ componentArticleId: 'c1', componentSku: 'ART-003', quantity: 2 }],
    }))

    await user.clear(screen.getByLabelText('Name'))
    await user.type(screen.getByLabelText('Name'), 'Schraube M8 verzinkt') // nur der Name ändert sich
    await user.click(saveButton())

    await waitFor(() => expect(api.calls('PUT', '/articles/a1')).toHaveLength(1))
    expect(putBody()).toMatchObject({
      name: 'Schraube M8 verzinkt',
      gtin: '4006381333931',
      alternativeSkus: ['ART-002'],
      validFrom: '2026-03-01T00:00:00',
      validUntil: '2026-09-30T00:00:00',
      bundleComponents: [{ componentArticleId: 'c1', quantity: 2 }],
    })
    expect(await screen.findByText('Artikelliste')).toBeInTheDocument()
  })

  it('sendet geänderte Werte: neue Alt-SKU, neue GTIN, neues Saison-Fenster, andere Komponenten', async () => {
    const user = await openArticle(makeArticle({
      id: 'a1',
      sku: 'ART-001',
      alternativeSkus: ['ART-002'],
      gtin: '4006381333931',
      validFrom: '2026-03-01T00:00:00',
      validUntil: '2026-09-30T00:00:00',
      isBundle: true,
      bundleComponents: [{ componentArticleId: 'c1', componentSku: 'ART-003', quantity: 2 }],
    }))

    // Alternativ-SKU: eine hinzufügen (Schreibweise egal), die alte entfernen
    await user.type(screen.getByLabelText('Alternativ-SKU hinzufügen'), 'art-003')
    await user.click(screen.getByRole('button', { name: 'Hinzufügen' }))
    await user.click(screen.getByRole('button', { name: 'ART-002 entfernen' }))
    // GTIN ändern (mit Leerraum getippt)
    const gtin = screen.getByLabelText('GTIN / EAN')
    await user.clear(gtin)
    await user.type(gtin, '7351 3537')
    // Saison
    fireEvent.change(screen.getByLabelText('Gültig ab'), { target: { value: '2026-04-15' } })
    fireEvent.change(screen.getByLabelText('Gültig bis (letzter Tag)'), { target: { value: '2026-10-31' } })
    // Bundle: Menge ändern, weitere Komponente über die Suche aufnehmen
    const quantity = screen.getByLabelText('Menge ART-003')
    await user.clear(quantity)
    await user.type(quantity, '5')
    await user.type(screen.getByLabelText('Komponente suchen (SKU, Name oder GTIN)'), 'mutter')
    await user.click(screen.getByRole('button', { name: 'ART-002 hinzufügen' }))

    await user.click(saveButton())

    await waitFor(() => expect(api.calls('PUT', '/articles/a1')).toHaveLength(1))
    expect(putBody()).toMatchObject({
      alternativeSkus: ['ART-003'],
      gtin: '73513537',
      validFrom: '2026-04-15T00:00:00',
      validUntil: '2026-10-31T00:00:00',
      bundleComponents: [
        { componentArticleId: 'c1', quantity: 5 },
        { componentArticleId: 'b1', quantity: 1 },
      ],
    })
  })

  it('löst ein Bundle auf: alle Komponenten entfernt = leere Liste im PUT, nicht "unverändert"', async () => {
    const user = await openArticle(makeArticle({
      id: 'a1',
      isBundle: true,
      bundleComponents: [{ componentArticleId: 'c1', componentSku: 'ART-003', quantity: 2 }],
    }))

    await user.click(screen.getByRole('button', { name: 'ART-003 entfernen' }))
    await user.click(saveButton())

    await waitFor(() => expect(api.calls('PUT', '/articles/a1')).toHaveLength(1))
    expect(putBody().bundleComponents).toEqual([])
  })

  it('entfernt die GTIN mit einem leeren Text im PUT (fehlend hieße "unverändert")', async () => {
    const user = await openArticle(makeArticle({ id: 'a1', gtin: '4006381333931' }))

    await user.clear(screen.getByLabelText('GTIN / EAN'))
    await user.click(saveButton())

    await waitFor(() => expect(api.calls('PUT', '/articles/a1')).toHaveLength(1))
    expect(putBody().gtin).toBe('')
  })

  it('legt einen neuen Artikel mit normalisierter GTIN, Alt-SKU und Bundle-Komponenten per POST an', async () => {
    api.setRoute('POST /articles', makeArticle({ id: 'neu', sku: 'NEU-1' }))
    const user = userEvent.setup()
    renderEditor('/articles/new')

    await user.type(await screen.findByLabelText('SKU'), 'NEU-1')
    await user.type(screen.getByLabelText('Name'), 'Set')
    await user.type(screen.getByLabelText('GTIN / EAN'), '4 006381 333931')
    await waitFor(() => expect(screen.getByLabelText('Komponente suchen (SKU, Name oder GTIN)')).toBeEnabled())
    await user.type(screen.getByLabelText('Alternativ-SKU hinzufügen'), 'ART-002')
    await user.click(screen.getByRole('button', { name: 'Hinzufügen' }))
    await user.type(screen.getByLabelText('Komponente suchen (SKU, Name oder GTIN)'), 'ART-003')
    await user.click(screen.getByRole('button', { name: 'ART-003 hinzufügen' }))
    await user.click(saveButton())

    await waitFor(() => expect(api.calls('POST', '/articles')).toHaveLength(1))
    expect(api.calls('POST', '/articles')[0].body).toMatchObject({
      sku: 'NEU-1',
      gtin: '4006381333931',
      alternativeSkus: ['ART-002'],
      bundleComponents: [{ componentArticleId: 'c1', quantity: 1 }],
    })
  })

  it('sendet beim Neuanlegen ohne GTIN und ohne Komponenten gtin: null und keine Komponentenliste', async () => {
    api.setRoute('POST /articles', makeArticle({ id: 'neu', sku: 'NEU-2' }))
    const user = userEvent.setup()
    renderEditor('/articles/new')

    await user.type(await screen.findByLabelText('SKU'), 'NEU-2')
    await user.type(screen.getByLabelText('Name'), 'Einzelteil')
    await user.click(saveButton())

    await waitFor(() => expect(api.calls('POST', '/articles')).toHaveLength(1))
    const body = api.calls('POST', '/articles')[0].body as Record<string, unknown>
    expect(body.gtin).toBeNull()
    expect(body).not.toHaveProperty('bundleComponents')
  })

  // ---- GTIN: Live-Hinweis ---------------------------------------------

  it('meldet eine falsche Prüfziffer beim Tippen und sperrt das Speichern, bis sie stimmt', async () => {
    const user = await openArticle()
    const gtin = screen.getByLabelText('GTIN / EAN')

    await user.type(gtin, '4006381333932')
    expect(screen.getByText('Prüfziffer der GTIN stimmt nicht (erwartet 1).')).toBeInTheDocument()
    expect(saveButton()).toBeDisabled()
    expect(gtin).toHaveAttribute('aria-invalid', 'true')

    await user.clear(gtin)
    await user.type(gtin, '4006381333931')
    expect(screen.getByText('Gültige GTIN (EAN-13, Prüfziffer stimmt).')).toBeInTheDocument()
    expect(saveButton()).toBeEnabled()
  })

  it('zeigt einen Konflikt des Servers (dieselbe GTIN) am Feld und im Fehlerbanner und bleibt im Editor', async () => {
    const user = await openArticle()
    api.setRoute('PUT /articles/a1', fail(409, {
      status: 409,
      code: 'duplicate_gtin',
      detail: "Die GTIN 4006381333931 ist bereits dem Artikel 'ART-002' zugeordnet",
    }))

    await user.type(screen.getByLabelText('GTIN / EAN'), '4006381333931')
    await user.click(saveButton())

    expect(await screen.findByRole('alert')).toHaveTextContent("bereits dem Artikel 'ART-002' zugeordnet")
    expect(screen.getByText('Diese GTIN ist bereits einem anderen Artikel zugeordnet.')).toBeInTheDocument()
    expect(screen.queryByText('Artikelliste')).not.toBeInTheDocument()
  })

  it('zeigt beim Anlegen die doppelte SKU (409) am Feld und im Fehlerbanner', async () => {
    api.setRoute('POST /articles', fail(409, { status: 409, code: 'conflict', detail: "Ein Artikel mit der SKU 'NEU-1' existiert bereits" }))
    const user = userEvent.setup()
    renderEditor('/articles/new')

    await user.type(await screen.findByLabelText('SKU'), 'NEU-1')
    await user.type(screen.getByLabelText('Name'), 'Doppelt')
    await user.click(saveButton())

    expect(await screen.findByRole('alert')).toHaveTextContent("Ein Artikel mit der SKU 'NEU-1' existiert bereits")
    expect(screen.getByText('Diese SKU ist bereits vergeben.')).toBeInTheDocument()
    expect(screen.getByLabelText('SKU')).toHaveAttribute('aria-invalid', 'true')
  })

  // ---- Alternativ-SKUs -------------------------------------------------

  it('prüft Alternativ-SKUs: unbekannte, eigene und doppelte werden mit Grund abgelehnt', async () => {
    const user = await openArticle(makeArticle({ id: 'a1', sku: 'ART-001', alternativeSkus: ['ART-002'] }))
    const input = screen.getByLabelText('Alternativ-SKU hinzufügen')
    const add = screen.getByRole('button', { name: 'Hinzufügen' })

    await user.type(input, 'GIBT-ES-NICHT')
    await user.click(add)
    expect(screen.getByRole('alert')).toHaveTextContent('Kein Artikel mit der SKU GIBT-ES-NICHT gefunden.')

    await user.clear(input)
    await user.type(input, 'art-001')
    await user.click(add)
    expect(screen.getByRole('alert')).toHaveTextContent('eigener Ersatz')

    await user.clear(input)
    await user.type(input, 'art-002')
    await user.click(add)
    expect(screen.getByRole('alert')).toHaveTextContent('schon eingetragen')

    const chips = within(screen.getByRole('list', { name: 'Alternativ-SKUs' })).getAllByRole('listitem')
    expect(chips).toHaveLength(1)
    // Nichts davon hat etwas geändert: kein ungespeicherter Zustand.
    expect(screen.queryByText('Ungespeicherte Änderungen')).not.toBeInTheDocument()
  })

  it('Enter im Feld nimmt die SKU auf und sendet das Formular nicht ab', async () => {
    const user = await openArticle()

    await user.type(screen.getByLabelText('Alternativ-SKU hinzufügen'), 'ART-002{Enter}')

    expect(within(screen.getByRole('list', { name: 'Alternativ-SKUs' })).getByText('ART-002')).toBeInTheDocument()
    expect(api.calls('PUT', '/articles/a1')).toHaveLength(0)
  })

  it('kennzeichnet eine gespeicherte Alternativ-SKU, zu der es keinen Artikel mehr gibt', async () => {
    await openArticle(makeArticle({ id: 'a1', alternativeSkus: ['ART-002', 'VERSCHWUNDEN'] }))

    const chips = within(screen.getByRole('list', { name: 'Alternativ-SKUs' })).getAllByRole('listitem')
    expect(within(chips[0]).queryByLabelText('unbekannte SKU')).not.toBeInTheDocument()
    expect(within(chips[1]).getByLabelText('unbekannte SKU')).toBeInTheDocument()
    expect(within(chips[1]).getByRole('button', { name: 'VERSCHWUNDEN entfernen' })).toBeEnabled()
  })

  // ---- Bundle: Guards --------------------------------------------------

  it('bietet den Artikel selbst und Komponenten, die einen Zyklus bilden würden, nicht an (mit Begründung)', async () => {
    // ART-002 (Mutter) ist ein Bundle, das ART-001 enthält: ART-002 in ART-001 aufzunehmen wäre ein Zyklus.
    api.setRoute('GET /articles', [
      self,
      makeArticle({ id: 'b1', sku: 'ART-002', name: 'Mutter M8', isBundle: true, bundleComponents: [{ componentArticleId: 'a1', componentSku: 'ART-001', quantity: 1 }] }),
      washer,
    ])
    const user = await openArticle()
    await waitFor(() => expect(screen.getByLabelText('Komponente suchen (SKU, Name oder GTIN)')).toBeEnabled())
    const search = screen.getByLabelText('Komponente suchen (SKU, Name oder GTIN)')

    await user.type(search, 'ART-00')

    expect(screen.getByRole('button', { name: 'ART-001 hinzufügen' })).toBeDisabled()
    expect(screen.getByText(/nicht selbst enthalten/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'ART-002 hinzufügen' })).toBeDisabled()
    expect(screen.getByText(/Zyklus bilden: ART-001 → ART-002 → ART-001/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'ART-003 hinzufügen' })).toBeEnabled()
  })

  it('eine schon aufgenommene Komponente lässt sich nicht noch einmal hinzufügen', async () => {
    const user = await openArticle(makeArticle({
      id: 'a1',
      isBundle: true,
      bundleComponents: [{ componentArticleId: 'c1', componentSku: 'ART-003', quantity: 1 }],
    }))
    await waitFor(() => expect(screen.getByLabelText('Komponente suchen (SKU, Name oder GTIN)')).toBeEnabled())

    await user.type(screen.getByLabelText('Komponente suchen (SKU, Name oder GTIN)'), 'scheibe')

    expect(screen.getByRole('button', { name: 'ART-003 hinzufügen' })).toBeDisabled()
    expect(screen.getByText(/schon Komponente/)).toBeInTheDocument()
  })

  it('sperrt das Speichern bei einer ungültigen Komponentenmenge', async () => {
    const user = await openArticle(makeArticle({
      id: 'a1',
      isBundle: true,
      bundleComponents: [{ componentArticleId: 'c1', componentSku: 'ART-003', quantity: 2 }],
    }))

    const quantity = screen.getByLabelText('Menge ART-003')
    await user.clear(quantity)
    await user.type(quantity, '0')

    expect(screen.getByRole('alert')).toHaveTextContent('ganze Zahl zwischen 1 und')
    expect(saveButton()).toBeDisabled()
  })

  // ---- Saison ------------------------------------------------------------

  it('prüft das Saison-Fenster: Ende vor Beginn sperrt das Speichern, ein Ein-Tages-Fenster ist erlaubt', async () => {
    const user = await openArticle()
    const from = screen.getByLabelText('Gültig ab')
    const until = screen.getByLabelText('Gültig bis (letzter Tag)')

    fireEvent.change(from, { target: { value: '2026-05-05' } })
    fireEvent.change(until, { target: { value: '2026-05-04' } })
    expect(screen.getByRole('alert')).toHaveTextContent('Saison-Ende darf nicht vor dem Beginn liegen')
    expect(saveButton()).toBeDisabled()

    fireEvent.change(until, { target: { value: '2026-05-05' } })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    await user.click(saveButton())
    await waitFor(() => expect(api.calls('PUT', '/articles/a1')).toHaveLength(1))
    expect(putBody()).toMatchObject({ validFrom: '2026-05-05T00:00:00', validUntil: '2026-05-05T00:00:00' })
  })

  it('kennzeichnet den Saison-Status oben: außerhalb der Saison, wenn das Ende vergangen ist', async () => {
    await openArticle(makeArticle({ id: 'a1', validFrom: '2020-01-01T00:00:00', validUntil: '2020-12-31T00:00:00' }))

    expect(screen.getByText('Außerhalb der Saison')).toBeInTheDocument()
  })

  // ---- Ungespeicherte Änderungen -------------------------------------------

  it('warnt beim Abbrechen mit ungespeicherten Änderungen und verlässt die Seite erst nach Bestätigung', async () => {
    const user = await openArticle()
    await user.type(screen.getByLabelText('Name'), ' neu')
    expect(screen.getByText('Ungespeicherte Änderungen')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Abbrechen' }))
    const dialog = await screen.findByRole('dialog', { name: 'Änderungen verwerfen?' })
    await user.click(within(dialog).getByRole('button', { name: 'Weiter bearbeiten' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toHaveValue('Schraube M8 neu') // nichts ging verloren

    await user.click(screen.getByRole('button', { name: 'Abbrechen' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Verwerfen und verlassen' }))
    expect(await screen.findByText('Artikelliste')).toBeInTheDocument()
  })

  it('ohne Änderung verlässt "Abbrechen" die Seite sofort, ohne Rückfrage', async () => {
    const user = await openArticle()

    await user.click(screen.getByRole('button', { name: 'Abbrechen' }))

    expect(await screen.findByText('Artikelliste')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('navigiert bei einem Klick auf einen internen Link ohne Änderungen sofort', async () => {
    const user = await openArticle()
    await user.click(screen.getByRole('link', { name: 'Seitenleiste: Artikel' }))
    expect(await screen.findByText('Artikelliste')).toBeInTheDocument()
  })

  it('fragt beim Klick auf einen Link nach, wenn Änderungen vorliegen', async () => {
    const user = await openArticle()
    await user.type(screen.getByLabelText('Name'), 'x')

    await user.click(screen.getByRole('link', { name: 'Seitenleiste: Artikel' }))

    const dialog = await screen.findByRole('dialog', { name: 'Änderungen verwerfen?' })
    expect(screen.queryByText('Artikelliste')).not.toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Verwerfen und verlassen' }))
    expect(await screen.findByText('Artikelliste')).toBeInTheDocument()
  })

  it('bittet den Browser beim Schließen des Tabs um Bestätigung, aber nur bei Änderungen', async () => {
    const user = await openArticle()
    const clean = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(clean)
    expect(clean.defaultPrevented).toBe(false)

    await user.type(screen.getByLabelText('Name'), 'x')
    const dirty = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(dirty)
    expect(dirty.defaultPrevented).toBe(true)
  })
})
