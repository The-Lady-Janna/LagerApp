import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { ArticleEditorPage } from '../../pages/ArticleEditorPage'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makeArticle, makeSupplier } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'

// Regression (frontend#2): Der Server setzt Alternativ-SKUs und Saison-Fenster bei jedem PUT auf leer/null zurück,
// wenn der Request sie nicht enthält. Der Editor muss die geladenen Werte deshalb unverändert mitsenden.
describe('ArticleEditorPage — Datenverlust beim Speichern', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /suppliers': [makeSupplier()],
      'GET /articles': [],
    })
  })
  afterEach(() => api.restore())

  const renderEditor = (path: string) =>
    renderWithProviders(
      <Routes>
        <Route path="/articles" element={<p>Artikelliste</p>} />
        <Route path="/articles/new" element={<ArticleEditorPage />} />
        <Route path="/articles/:id" element={<ArticleEditorPage />} />
      </Routes>,
      { initialEntries: [path] },
    )

  it('sendet nach dem Laden Alternativ-SKUs und Saison-Fenster unverändert im PUT', async () => {
    const article = makeArticle({
      alternativeSkus: ['ALT-1', 'ALT-2'],
      validFrom: '2026-03-01T00:00:00',
      validUntil: '2026-09-30T00:00:00',
    })
    api.setRoute('GET /articles/a1', article)
    api.setRoute('PUT /articles/a1', article)
    const user = userEvent.setup()
    renderEditor('/articles/a1')

    const name = await screen.findByLabelText('Name')
    await waitFor(() => expect(name).toHaveValue('Schraube M8'))
    // Nur den Namen korrigieren — genau der Fall, der bisher still Daten gelöscht hat.
    await user.clear(name)
    await user.type(name, 'Schraube M8 verzinkt')
    await user.click(screen.getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('PUT', '/articles/a1')).toHaveLength(1))
    const body = api.calls('PUT', '/articles/a1')[0].body as Record<string, unknown>
    expect(body.name).toBe('Schraube M8 verzinkt')
    expect(body.alternativeSkus).toEqual(['ALT-1', 'ALT-2'])
    expect(body.validFrom).toBe('2026-03-01T00:00:00')
    expect(body.validUntil).toBe('2026-09-30T00:00:00')
    // Bundle-Komponenten werden bewusst nicht gesendet (der Server lässt sie bei fehlendem Wert unverändert).
    expect(body).not.toHaveProperty('bundleComponents')
    // Nach dem Speichern geht es zurück zur Liste.
    expect(await screen.findByText('Artikelliste')).toBeInTheDocument()
  })

  it('sendet für einen Artikel ohne Alternativen/Saison leere Werte (kein undefined-Feld, das der Server als Reset deutet)', async () => {
    const article = makeArticle({ alternativeSkus: null, validFrom: null, validUntil: null })
    api.setRoute('GET /articles/a1', article)
    api.setRoute('PUT /articles/a1', article)
    const user = userEvent.setup()
    renderEditor('/articles/a1')

    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Schraube M8'))
    await user.click(screen.getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('PUT', '/articles/a1')).toHaveLength(1))
    const body = api.calls('PUT', '/articles/a1')[0].body as Record<string, unknown>
    expect(body.alternativeSkus).toEqual([])
    expect(body.validFrom).toBeNull()
    expect(body.validUntil).toBeNull()
  })

  it('erlaubt das Speichern erst, wenn der Artikel geladen ist (kein leeres Formular über den echten Artikel)', async () => {
    api.setRoute('GET /articles/a1', fail(500, { error: 'Datenbank nicht erreichbar' }))
    renderEditor('/articles/a1')

    expect(await screen.findByText(/Artikel konnte nicht geladen werden/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Speichern' })).not.toBeInTheDocument()
    expect(api.calls('PUT', '/articles/a1')).toHaveLength(0)
  })

  it('legt einen neuen Artikel per POST an', async () => {
    api.setRoute('POST /articles', makeArticle({ id: 'neu', sku: 'NEU-1' }))
    const user = userEvent.setup()
    renderEditor('/articles/new')

    await user.type(await screen.findByLabelText('SKU'), 'NEU-1')
    await user.type(screen.getByLabelText('Name'), 'Neuer Artikel')
    await user.click(screen.getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('POST', '/articles')).toHaveLength(1))
    expect(api.calls('POST', '/articles')[0].body).toMatchObject({ sku: 'NEU-1', name: 'Neuer Artikel', alternativeSkus: [] })
  })
})
