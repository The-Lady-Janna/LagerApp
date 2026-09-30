import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { PurchaseOrdersPage } from '../../pages/PurchaseOrdersPage'
import { SuppliersPage } from '../../pages/SuppliersPage'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { makeArticle, makeSupplier, makeSuggestion } from '../helpers/fixtures'
import { renderWithProviders } from '../helpers/render'

// Regression (frontend#4): Formulare initialisieren ihren State per useState(existing?.x) und bekamen beim Wechsel des
// Datensatzes keinen frischen State (fehlender key). Ein Klick auf "Edit" bei Lieferant B zeigte die Felder von A und
// speicherte sie unter B.
describe('SuppliersPage — Formular-Zustand', () => {
  let api: MockApi
  const supplierA = makeSupplier({ id: 's1', code: 'SUP-A', name: 'Lieferant A', contactEmail: 'a@example.test', leadTimeDays: 3 })
  const supplierB = makeSupplier({ id: 's2', code: 'SUP-B', name: 'Lieferant B', contactEmail: 'b@example.test', leadTimeDays: 9, currency: 'CHF' })

  beforeEach(() => {
    api = installMockApi({
      'GET /suppliers': [supplierA, supplierB],
      'PUT /suppliers/:id': supplierB,
      'POST /suppliers': supplierA,
    })
  })
  afterEach(() => api.restore())

  it('zeigt beim Wechsel von "Edit A" zu "Edit B" die Felder von B und speichert sie unter B', async () => {
    const user = userEvent.setup()
    renderWithProviders(<SuppliersPage />)

    const [editA, editB] = await screen.findAllByRole('button', { name: 'Edit' })
    await user.click(editA)
    expect(screen.getByLabelText('Name')).toHaveValue('Lieferant A')
    expect(screen.getByLabelText('Email')).toHaveValue('a@example.test')

    // Formular bleibt offen, der Nutzer klickt in der Tabelle auf B.
    await user.click(editB)
    expect(screen.getByRole('heading', { name: 'Edit SUP-B' })).toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toHaveValue('Lieferant B')
    expect(screen.getByLabelText('Email')).toHaveValue('b@example.test')
    expect(screen.getByLabelText('Währung')).toHaveValue('CHF')

    await user.clear(screen.getByLabelText('Name'))
    await user.type(screen.getByLabelText('Name'), 'Lieferant B neu')
    await user.click(screen.getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('PUT', '/suppliers/:id')).toHaveLength(1))
    const put = api.calls('PUT', '/suppliers/:id')[0]
    expect(put.path).toBe('/suppliers/s2')
    expect(put.body).toMatchObject({ name: 'Lieferant B neu', contactEmail: 'b@example.test', leadTimeDays: 9, currency: 'CHF' })
  })

  it('startet nach "Edit" mit "+ Neuer Lieferant" ein leeres Formular', async () => {
    const user = userEvent.setup()
    renderWithProviders(<SuppliersPage />)

    await user.click((await screen.findAllByRole('button', { name: 'Edit' }))[0])
    expect(screen.getByLabelText('Code')).toHaveValue('SUP-A')

    await user.click(screen.getByRole('button', { name: '+ Neuer Lieferant' }))

    expect(screen.getByRole('heading', { name: 'Neuer Lieferant' })).toBeInTheDocument()
    expect(screen.getByLabelText('Code')).toHaveValue('')
    expect(screen.getByLabelText('Code')).toBeEnabled()
    expect(screen.getByLabelText('Name')).toHaveValue('')
    expect(screen.getByLabelText('Email')).toHaveValue('')
  })

  it('sendet beim Anlegen alle sichtbaren Felder mit (bisher nur Code und Name)', async () => {
    const user = userEvent.setup()
    renderWithProviders(<SuppliersPage />)

    await user.click(await screen.findByRole('button', { name: '+ Neuer Lieferant' }))
    await user.type(screen.getByLabelText('Code'), 'SUP-C')
    await user.type(screen.getByLabelText('Name'), 'Lieferant C')
    await user.type(screen.getByLabelText('Email'), 'c@example.test')
    await user.type(screen.getByLabelText('Telefon'), '0123')
    await user.clear(screen.getByLabelText('Lieferzeit (Tage)'))
    await user.type(screen.getByLabelText('Lieferzeit (Tage)'), '14')
    await user.type(screen.getByLabelText('Notizen'), 'nur Palette')
    await user.click(screen.getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('POST', '/suppliers')).toHaveLength(1))
    expect(api.calls('POST', '/suppliers')[0].body).toEqual({
      code: 'SUP-C',
      name: 'Lieferant C',
      contactEmail: 'c@example.test',
      contactPhone: '0123',
      notes: 'nur Palette',
      leadTimeDays: 14,
      minOrderValueCents: 0,
      currency: 'EUR',
    })
  })
})

describe('PurchaseOrdersPage — Formular-Zustand', () => {
  let api: MockApi
  const supplierA = makeSupplier({ id: 's1', code: 'SUP-A', name: 'Lieferant A' })
  const supplierB = makeSupplier({ id: 's2', code: 'SUP-B', name: 'Lieferant B' })
  const articleA = makeArticle({ id: 'a1', sku: 'ART-A', name: 'Artikel A' })
  const articleB = makeArticle({ id: 'a2', sku: 'ART-B', name: 'Artikel B' })

  beforeEach(() => {
    api = installMockApi({
      'GET /purchase-orders': [],
      'GET /suppliers': [supplierA, supplierB],
      'GET /articles': [articleA, articleB],
      'GET /purchase-orders/suggestions': [
        makeSuggestion({
          supplierId: 's1',
          supplierName: 'Lieferant A',
          lines: [{ articleId: 'a1', sku: 'ART-A', name: 'Artikel A', currentStock: 1, minStock: 2, reorderPoint: 5, maxStock: 50, suggestedOrderQty: 10 }],
        }),
        makeSuggestion({
          supplierId: 's2',
          supplierName: 'Lieferant B',
          lines: [{ articleId: 'a2', sku: 'ART-B', name: 'Artikel B', currentStock: 0, minStock: 2, reorderPoint: 5, maxStock: 50, suggestedOrderQty: 30 }],
        }),
      ],
      'POST /purchase-orders': { id: 'po1' },
    })
  })
  afterEach(() => api.restore())

  it('übernimmt beim Wechsel des Bestellvorschlags Lieferant und Zeilen des neuen Vorschlags', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PurchaseOrdersPage />)

    const [forA, forB] = await screen.findAllByRole('button', { name: /PO erstellen/ })
    await user.click(forA)
    expect(screen.getByRole('heading', { name: /PO aus Vorschlag — Lieferant A/ })).toBeInTheDocument()
    expect(screen.getByLabelText('Lieferant')).toHaveValue('s1')

    // Formular ist offen; Klick auf den Vorschlag von Lieferant B.
    await user.click(forB)
    expect(screen.getByRole('heading', { name: /PO aus Vorschlag — Lieferant B/ })).toBeInTheDocument()
    expect(screen.getByLabelText('Lieferant')).toHaveValue('s2')

    // Die Zeile im Formular gehört zu B (Menge 30), nicht zu A (Menge 10).
    const formular = screen.getByRole('heading', { name: /PO aus Vorschlag/ }).closest('.card') as HTMLElement
    const zeile = within(formular).getAllByRole('row')[1]
    expect(within(zeile).getByRole('combobox')).toHaveValue('a2')
    expect(within(zeile).getAllByRole('spinbutton')[0]).toHaveValue(30)

    await user.click(within(formular).getByRole('button', { name: /Anlegen/ }))
    await waitFor(() => expect(api.calls('POST', '/purchase-orders')).toHaveLength(1))
    expect(api.calls('POST', '/purchase-orders')[0].body).toMatchObject({
      supplierId: 's2',
      lines: [{ articleId: 'a2', orderedQty: 30 }],
    })
  })
})
