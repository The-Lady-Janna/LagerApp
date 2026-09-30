import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ArticlesPage } from '../../pages/ArticlesPage'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { makeArticle } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'

// Artikelliste (WP20): GTIN-Spalte, Suche über alle Kennungen, Filter "inaktiv", Kennzeichnung von Bundles und Saison.
describe('ArticlesPage — GTIN, Suche, Filter, Kennzeichen', () => {
  let api: MockApi

  const plain = makeArticle({ id: 'a1', sku: 'ART-001', name: 'Schraube M8', gtin: '4006381333931' })
  const bundle = makeArticle({
    id: 'a2',
    sku: 'KIT-001',
    name: 'Starterset',
    alternativeSkus: ['ERSATZ-9'],
    isBundle: true,
    bundleComponents: [
      { componentArticleId: 'a1', componentSku: 'ART-001', quantity: 2 },
      { componentArticleId: 'a4', componentSku: '', quantity: 1 }, // SKU fehlt (älterer Server): wird aus der Liste ergänzt
    ],
  })
  const over = makeArticle({ id: 'a3', sku: 'WINTER-1', name: 'Streusalz', validFrom: '2020-10-01T00:00:00', validUntil: '2020-12-31T00:00:00', isCurrentlyActive: false })
  const inSeason = makeArticle({ id: 'a4', sku: 'SOMMER-1', name: 'Sonnencreme', validFrom: '2020-01-01T00:00:00', validUntil: '2999-12-31T00:00:00', isCurrentlyActive: true })

  beforeEach(() => {
    api = installMockApi({ 'GET /articles': [plain, bundle, over, inSeason] })
  })
  afterEach(() => api.restore())

  const skusInTable = () =>
    within(screen.getByRole('table')).getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[0].textContent ?? '')

  async function renderPage() {
    const user = userEvent.setup()
    renderWithProviders(<ArticlesPage />)
    await screen.findByText('Artikel (4)')
    return user
  }

  it('zeigt die GTIN in einer eigenen Spalte', async () => {
    await renderPage()

    expect(screen.getByRole('columnheader', { name: 'GTIN' })).toBeInTheDocument()
    const row = screen.getByText('ART-001').closest('tr')!
    expect(within(row).getByText('4006381333931')).toBeInTheDocument()
    expect(within(screen.getByText('WINTER-1').closest('tr')!).getByText('—')).toBeInTheDocument() // Artikel ohne GTIN
  })

  it('sucht über Name, SKU, Alternativ-SKU und GTIN (Schreibweise und Leerraum egal)', async () => {
    const user = await renderPage()
    const search = screen.getByLabelText('Artikel suchen')

    await user.type(search, 'streusalz')
    expect(skusInTable()).toEqual(['WINTER-1'])

    await user.clear(search)
    await user.type(search, 'sommer')
    expect(skusInTable()).toEqual(['SOMMER-1'])

    await user.clear(search)
    await user.type(search, 'ersatz-9') // Alternativ-SKU
    expect(skusInTable()).toEqual(['KIT-001'])

    await user.clear(search)
    await user.type(search, '4006 3813 33931') // GTIN, mit Leerraum getippt
    expect(skusInTable()).toEqual(['ART-001'])
    expect(screen.getByText('Artikel (1 von 4)')).toBeInTheDocument()

    await user.clear(search)
    await user.type(search, 'gibt-es-nicht')
    expect(screen.getByText('Keine Artikel passen zu Suche und Filter.')).toBeInTheDocument()
  })

  it('filtert nach Status: inaktiv (außerhalb der Saison) und aktuell bestellbar', async () => {
    const user = await renderPage()
    const filter = screen.getByLabelText('Status')

    await user.selectOptions(filter, 'inactive')
    expect(skusInTable()).toEqual(['WINTER-1'])

    await user.selectOptions(filter, 'active')
    expect(skusInTable()).toEqual(['ART-001', 'KIT-001', 'SOMMER-1'])

    await user.selectOptions(filter, 'all')
    expect(skusInTable()).toHaveLength(4)
  })

  it('kombiniert Suche und Filter', async () => {
    const user = await renderPage()

    await user.selectOptions(screen.getByLabelText('Status'), 'active')
    await user.type(screen.getByLabelText('Artikel suchen'), 'salz')

    expect(screen.getByText('Keine Artikel passen zu Suche und Filter.')).toBeInTheDocument() // Streusalz ist inaktiv
  })

  it('kennzeichnet Bundles (Komponenten im Tooltip) und Artikel außerhalb der Saison', async () => {
    await renderPage()

    const bundleRow = screen.getByText('KIT-001').closest('tr')!
    // Der Tooltip hängt an dem span um die StatusPill (die Pill selbst kennt keinen title).
    const badge = within(bundleRow).getByText('Bundle')
    expect(badge).toHaveClass('pill', 'pill--special')
    expect(badge.closest('[title]')).toHaveAttribute('title', 'Besteht aus: 2× ART-001, 1× SOMMER-1')

    const overRow = screen.getByText('WINTER-1').closest('tr')!
    const overBadge = within(overRow).getByText('Außerhalb der Saison')
    expect(overBadge).toHaveClass('pill--danger')
    expect(overBadge.closest('[title]')).toHaveAttribute('title', expect.stringContaining('01.10.2020 bis 31.12.2020'))
    expect(within(screen.getByText('SOMMER-1').closest('tr')!).getByText('In Saison')).toHaveClass('pill--success')
    // Artikel ohne Bundle und ohne Saison: keine Kennzeichen
    const plainRow = screen.getByText('ART-001').closest('tr')!
    expect(within(plainRow).queryByText('Bundle')).not.toBeInTheDocument()
    expect(within(plainRow).queryByText(/Saison/)).not.toBeInTheDocument()
  })

  it('rechnet die Saison selbst nach, wenn der Server isCurrentlyActive nicht liefert', async () => {
    api.setRoute('GET /articles', [
      makeArticle({ id: 'x1', sku: 'ALT-SERVER-1', validFrom: '2020-01-01T00:00:00', validUntil: '2020-06-30T00:00:00' }), // ohne isCurrentlyActive
    ])
    renderWithProviders(<ArticlesPage />)

    expect(await screen.findByText('Außerhalb der Saison')).toBeInTheDocument()
  })
})
