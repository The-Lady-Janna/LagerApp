import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { ambiguousArticleCandidates, fetchArticleByCode, useArticleByCode, useUpdateArticle } from '../../api/hooks'
import { parseApiError } from '../../api/errors'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makeArticle } from '../helpers/fixtures'
import { createTestQueryClient } from '../helpers/render'

// GET /api/articles/by-code/{code} (Scan-Auflösung): der Hook, die Kodierung des Codes, 404/409 als erwartete Ergebnisse.
describe('useArticleByCode / fetchArticleByCode', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi()
  })
  afterEach(() => api.restore())

  const setup = () => {
    const queryClient = createTestQueryClient()
    const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    return { queryClient, wrapper }
  }

  it('löst einen Code auf den Artikel auf', async () => {
    api.setRoute('GET /articles/by-code/:code', makeArticle({ id: 'a1', sku: 'ART-001', gtin: '4006381333931' }))
    const { wrapper } = setup()

    const { result } = renderHook(() => useArticleByCode('4006381333931'), { wrapper })

    await waitFor(() => expect(result.current.isSuccess).toBe(true))
    expect(result.current.data?.sku).toBe('ART-001')
    expect(api.calls('GET', '/articles/by-code/:code')[0].path).toBe('/articles/by-code/4006381333931')
  })

  it('kodiert den Code für die URL (Leerzeichen, Schrägstrich) und trimmt ihn', async () => {
    api.setRoute('GET /articles/by-code/:code', makeArticle())

    await fetchArticleByCode('  A/B 1 ')

    expect(api.calls('GET', '/articles/by-code/:code')[0].path).toBe('/articles/by-code/A%2FB%201')
  })

  it('ohne Code ruht die Abfrage', async () => {
    const { wrapper } = setup()

    const { result } = renderHook(() => useArticleByCode('   '), { wrapper })

    expect(result.current.fetchStatus).toBe('idle')
    expect(api.requests).toHaveLength(0)
  })

  it('ein unbekannter Code ist ein 404 mit verständlicher Meldung', async () => {
    api.setRoute('GET /articles/by-code/:code', fail(404, { status: 404, code: 'not_found', detail: "Kein Artikel zum Code 'X' gefunden." }))
    const { wrapper } = setup()

    const { result } = renderHook(() => useArticleByCode('X'), { wrapper })

    await waitFor(() => expect(result.current.isError).toBe(true))
    expect(parseApiError(result.current.error)).toMatchObject({ status: 404, code: 'not_found', message: "Kein Artikel zum Code 'X' gefunden." })
    expect(ambiguousArticleCandidates(result.current.error)).toEqual([])
  })

  it('ein mehrdeutiger Code ist ein 409 mit der Liste der Kandidaten', async () => {
    const first = makeArticle({ id: 'a1', sku: 'ART-001' })
    const second = makeArticle({ id: 'a2', sku: 'ART-002' })
    api.setRoute('GET /articles/by-code/:code', fail(409, { status: 409, code: 'ambiguous_code', detail: 'Der Code passt auf 2 Artikel', candidates: [first, second] }))
    const { wrapper } = setup()

    const { result } = renderHook(() => useArticleByCode('ERSATZ'), { wrapper })

    await waitFor(() => expect(result.current.isError).toBe(true))
    expect(parseApiError(result.current.error).code).toBe('ambiguous_code')
    expect(ambiguousArticleCandidates(result.current.error).map((a) => a.sku)).toEqual(['ART-001', 'ART-002'])
  })

  it('das Speichern eines Artikels lädt eine geöffnete Code-Auflösung neu (Schlüssel unter ["articles"])', async () => {
    api.setRoute('GET /articles/by-code/:code', makeArticle({ id: 'a1', sku: 'ART-001', gtin: null }))
    api.setRoute('PUT /articles/a1', makeArticle({ id: 'a1', sku: 'ART-001', gtin: '4006381333931' }))
    const { wrapper } = setup()
    const { result } = renderHook(() => ({ lookup: useArticleByCode('ART-001'), update: useUpdateArticle('a1') }), { wrapper })
    await waitFor(() => expect(result.current.lookup.isSuccess).toBe(true))
    expect(api.calls('GET', '/articles/by-code/:code')).toHaveLength(1)

    api.setRoute('GET /articles/by-code/:code', makeArticle({ id: 'a1', sku: 'ART-001', gtin: '4006381333931' }))
    await result.current.update.mutateAsync({
      name: 'Schraube',
      description: null,
      dimensions: { lengthMm: 1, widthMm: 1, heightMm: 1 },
      weightGrams: 1,
      stacking: { isStackable: false, stackingAxis: 'Z', stackingIncrementMm: 0, maxStackCount: null },
      gtin: '4006381333931',
    })

    await waitFor(() => expect(result.current.lookup.data?.gtin).toBe('4006381333931'))
    expect(api.calls('GET', '/articles/by-code/:code')).toHaveLength(2)
  })
})
