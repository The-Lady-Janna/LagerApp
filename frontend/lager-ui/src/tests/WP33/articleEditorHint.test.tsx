import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import { Route, Routes } from 'react-router-dom'
import { ArticleEditorPage } from '../../pages/ArticleEditorPage'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { makeArticle, makeSupplier } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner (paralleler dotnet test, CI) reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// WP33: Der Editor versprach, Alternativ-SKUs würden "bei fehlendem Bestand vorgeschlagen". Diese Funktion gibt es nicht
// (docs/features/artikel-gtin.md): Alternativ-SKUs wirken nur bei der Code-Auflösung (GET /api/articles/by-code/{code}) beim
// Scannen und Suchen. Der Hinweistext sagt jetzt, was wirklich passiert.
describe('ArticleEditor: Hinweis zu den Alternativ-SKUs', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /suppliers': [makeSupplier()],
      'GET /articles': [makeArticle()],
    })
  })
  afterEach(() => api.restore())

  it('beschreibt die Auflösung beim Scannen und Suchen und verspricht keinen Ersatzartikel-Vorschlag', async () => {
    renderWithProviders(
      <Routes><Route path="/articles/new" element={<ArticleEditorPage />} /></Routes>,
      { initialEntries: ['/articles/new'] },
    )

    // Der Editor lädt als eigener Chunk nach; unter Last darf das länger dauern.
    const hint = await screen.findByText(/Alternativ-SKUs sind/, {}, { timeout: 15_000 })

    expect(hint).toHaveTextContent('Die Artikelsuche berücksichtigt sie, und beim Scannen gelten sie, wenn kein Artikel den Code als SKU oder GTIN führt')
    expect(hint.textContent).not.toMatch(/bei fehlendem Bestand vorgeschlagen/)
    expect(hint).toHaveTextContent('Einen Ersatzartikel bei fehlendem Bestand schlagen sie nicht vor')
  }, 30_000)
})
