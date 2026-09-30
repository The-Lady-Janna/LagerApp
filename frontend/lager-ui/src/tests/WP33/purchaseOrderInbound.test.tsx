import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { renderHook, screen, waitFor, within } from '@testing-library/react'
import { QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import userEvent from '@testing-library/user-event'
import { useCreateInboundFromPurchaseOrder, useReceiveInbound } from '../../api/hooks'
import type { InboundShipmentDto, PurchaseOrderDto, StorageLocationDto } from '../../api/types'
import { PurchaseOrdersPage } from '../../pages/PurchaseOrdersPage'
import { queryKeys } from '../../lib/queryKeys'
import { useAuth } from '../../state/auth'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makePurchaseOrder } from '../helpers/fixtures'
import { createTestQueryClient, renderWithProviders, signInAs } from '../helpers/render'

// Viele userEvent-Schritte je Test: auf einem ausgelasteten Rechner (paralleler dotnet test, CI) reichen die 5 s des Standards nicht.
vi.setConfig({ testTimeout: 30_000 })

// WP33: Der Dialog "Wareneingang buchen?" der Bestellungen bat um ein Buchen, tat aber nur eine Statuskorrektur an der Bestellzeile
// (POST …/lines/{id}/receive, WP14: "bucht KEINEN Bestand"). Der Weg ist POST /api/purchase-orders/{id}/create-inbound: er legt aus
// den offenen Mengen einen Wareneingang (Entwurf) an; gebucht wird erst dort. Die Seite macht das jetzt ehrlich.

const line = (id: string, sku: string, orderedQty: number, receivedQty: number): PurchaseOrderDto['lines'][number] =>
  ({ id, articleId: `art-${id}`, articleSku: sku, orderedQty, receivedQty, unitPriceCents: 250 })

const bin = (id: string, code: string): StorageLocationDto => ({
  id, shelfId: 's1', code, position: { xMm: 0, yMm: 0, zMm: 0 }, widthMm: 600, depthMm: 600, heightMm: 400, maxWeightGrams: 50_000,
})

const inboundDraft = (overrides: Partial<InboundShipmentDto> = {}): InboundShipmentDto => ({
  id: 'in1', shipmentNumber: 'WE-PO-0001', supplierReference: 'PO-0001', notes: null, status: 'Draft',
  createdAt: '2025-01-01T10:00:00', receivedAt: null,
  lines: [{ id: 'il1', articleId: 'art-l1', articleSku: 'ART-001', targetBinId: 'b1', targetBinCode: 'A-01-01', quantity: 6, lotNumber: null, expiryDate: null }],
  ...overrides,
})

/** Bestellung mit offener Menge: ART-001 6 von 10 offen, ART-002 vollständig empfangen. */
const openOrder = (overrides: Partial<PurchaseOrderDto> = {}) =>
  makePurchaseOrder({
    status: 'PartiallyReceived',
    lines: [line('l1', 'ART-001', 10, 4), line('l2', 'ART-002', 5, 5)],
    ...overrides,
  })

describe('PurchaseOrdersPage: Wareneingang anlegen statt Vorab-Buchen', () => {
  let api: MockApi

  beforeEach(() => {
    signInAs(['Receiver'])
    api = installMockApi({
      'GET /purchase-orders': [openOrder()],
      'GET /purchase-orders/suggestions': [],
      'GET /suppliers': [],
      'GET /articles': [],
      'GET /warehouse/storage-locations': [bin('b1', 'A-01-01'), bin('b2', 'A-01-02')],
      'POST /purchase-orders/:id/create-inbound': () => inboundDraft(),
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  const openDialog = async (user: ReturnType<typeof userEvent.setup>) => {
    await user.click(await screen.findByRole('button', { name: /Wareneingang anlegen/ }))
    return screen.getByRole('dialog', { name: 'Wareneingang anlegen?' })
  }

  it('legt per create-inbound einen Wareneingang an und verweist dorthin; gebucht wird dabei nichts', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PurchaseOrdersPage />)

    const dialog = await openDialog(user)
    // Ehrlich: nennt die offene Menge (nicht die bestellte), sagt, dass noch nichts gebucht wird, und verlangt den Lagerplatz.
    expect(dialog).toHaveTextContent('6 × ART-001')
    expect(dialog).not.toHaveTextContent('ART-002') // vollständig empfangen: keine offene Menge
    expect(dialog).toHaveTextContent('Es wird noch nichts gebucht')
    const confirm = within(dialog).getByRole('button', { name: 'Wareneingang anlegen' })
    expect(confirm).toBeDisabled()
    expect(api.calls('POST', '/purchase-orders/:id/create-inbound')).toHaveLength(0)

    await user.selectOptions(within(dialog).getByLabelText('Ziel-Lagerplatz'), 'b1')
    expect(confirm).toBeEnabled()
    await user.click(confirm)

    await waitFor(() => expect(api.calls('POST', '/purchase-orders/po1/create-inbound')).toHaveLength(1))
    expect(api.calls('POST', '/purchase-orders/po1/create-inbound')[0].body).toEqual({ targetBinId: 'b1' })

    // Erfolg: Dialog zu, Hinweis mit Nummer und Link in den Wareneingang.
    const notice = await screen.findByRole('status')
    expect(notice).toHaveTextContent('Wareneingang WE-PO-0001 zur Bestellung PO-0001 mit 1 Position(en) als Entwurf angelegt')
    expect(notice).toHaveTextContent('Gebucht ist noch nichts')
    expect(within(notice).getByRole('link', { name: 'Zum Wareneingang' })).toHaveAttribute('href', '/inbound')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    // Es ging genau EIN schreibender Aufruf raus: weder die Statuskorrektur an der Zeile noch das Buchen der Lieferung.
    const writes = api.requests.filter((r) => r.method !== 'GET')
    expect(writes.map((r) => `${r.method} ${r.path}`)).toEqual(['POST /purchase-orders/po1/create-inbound'])
    expect(api.calls('POST', '/purchase-orders/:id/lines/:lineId/receive')).toHaveLength(0)
    expect(api.calls('POST', '/inbound/:id/receive')).toHaveLength(0)
  })

  it('bietet keine Zeilen-Buchung mehr an: kein "Buchen"-Knopf, keine Mengenfelder in den Details', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PurchaseOrdersPage />)

    await user.click(await screen.findByRole('button', { name: 'Details' }))

    expect(screen.getByText('ART-001', { selector: 'td code' })).toBeInTheDocument() // die Details sind offen
    expect(screen.queryByRole('button', { name: /Buchen/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument()
    expect(screen.queryByText(/als\s+empfangen buchen/)).not.toBeInTheDocument()
  })

  it('Abbrechen legt nichts an', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PurchaseOrdersPage />)

    const dialog = await openDialog(user)
    await user.selectOptions(within(dialog).getByLabelText('Ziel-Lagerplatz'), 'b2')
    await user.click(within(dialog).getByRole('button', { name: 'Abbrechen' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(api.requests.filter((r) => r.method !== 'GET')).toEqual([])
  })

  it('ein Fehler des Servers (schon ein offener Wareneingang) bleibt im Dialog; der Dialog bleibt offen', async () => {
    api.setRoute('POST /purchase-orders/:id/create-inbound', fail(409, {
      title: 'Conflict', status: 409, detail: 'Zu Bestellung PO-0001 gibt es bereits einen offenen Wareneingang (WE-PO-0001).', code: 'inbound_draft_exists',
    }))
    const user = userEvent.setup()
    renderWithProviders(<PurchaseOrdersPage />)

    const dialog = await openDialog(user)
    await user.selectOptions(within(dialog).getByLabelText('Ziel-Lagerplatz'), 'b1')
    await user.click(within(dialog).getByRole('button', { name: 'Wareneingang anlegen' }))

    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent('Anlegen fehlgeschlagen:')
    expect(alert).toHaveTextContent('gibt es bereits einen offenen Wareneingang')
    expect(screen.getByRole('dialog', { name: 'Wareneingang anlegen?' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Zum Wareneingang' })).not.toBeInTheDocument()
    // Nach dem Fehler ist der Knopf wieder frei (anderer Platz oder erneut versuchen).
    expect(within(dialog).getByRole('button', { name: 'Wareneingang anlegen' })).toBeEnabled()
  })

  it('zeigt den Knopf nur für Bestellungen, die noch Ware erwarten und offene Mengen haben', async () => {
    api.setRoute('GET /purchase-orders', [
      openOrder({ id: 'p-sent', poNumber: 'PO-SENT', status: 'Sent' }),
      openOrder({ id: 'p-part', poNumber: 'PO-PART', status: 'PartiallyReceived' }),
      openOrder({ id: 'p-draft', poNumber: 'PO-DRAFT', status: 'Draft' }),
      openOrder({ id: 'p-recv', poNumber: 'PO-RECV', status: 'Received' }),
      openOrder({ id: 'p-canc', poNumber: 'PO-CANC', status: 'Cancelled' }),
      // versendet, aber jede Zeile vollständig empfangen: nichts mehr offen
      openOrder({ id: 'p-done', poNumber: 'PO-DONE', status: 'Sent', lines: [line('d1', 'ART-009', 3, 3)] }),
    ])
    renderWithProviders(<PurchaseOrdersPage />)

    await screen.findByText('PO-SENT')
    const rowOf = (number: string) => within(screen.getByText(number).closest('tr') as HTMLElement)
    expect(rowOf('PO-SENT').getByRole('button', { name: /Wareneingang anlegen/ })).toBeInTheDocument()
    expect(rowOf('PO-PART').getByRole('button', { name: /Wareneingang anlegen/ })).toBeInTheDocument()
    for (const number of ['PO-DRAFT', 'PO-RECV', 'PO-CANC', 'PO-DONE'])
      expect(rowOf(number).queryByRole('button', { name: /Wareneingang anlegen/ })).not.toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /Wareneingang anlegen/ })).toHaveLength(2)
  })

  it.each([['Viewer'], ['Picker'], ['Packer']])('blendet den Knopf für die Rolle %s aus (der Server verlangt Receiver)', async (role) => {
    useAuth.getState().logout()
    signInAs([role])
    renderWithProviders(<PurchaseOrdersPage />)

    expect(await screen.findByText('PO-0001')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Wareneingang anlegen/ })).not.toBeInTheDocument()
  })

  it.each([['Manager'], ['Admin']])('zeigt den Knopf für die Rolle %s (Manager und Admin schließen Receiver ein)', async (role) => {
    useAuth.getState().logout()
    signInAs([role])
    renderWithProviders(<PurchaseOrdersPage />)

    expect(await screen.findByRole('button', { name: /Wareneingang anlegen/ })).toBeInTheDocument()
  })
})

describe('Hooks der Bestell-Wareneingangs-Brücke', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'POST /purchase-orders/po1/create-inbound': inboundDraft(),
      'POST /inbound/in1/receive': inboundDraft({ status: 'Received' }),
    })
  })
  afterEach(() => api.restore())

  function setup() {
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    return { wrapper, invalidatedKeys: () => invalidate.mock.calls.map(([filter]) => filter?.queryKey) }
  }

  it('useCreateInboundFromPurchaseOrder ruft create-inbound mit dem Ziel-Lagerplatz auf und lädt den Wareneingang neu', async () => {
    const { wrapper, invalidatedKeys } = setup()
    const { result } = renderHook(() => useCreateInboundFromPurchaseOrder(), { wrapper })

    const created = await result.current.mutateAsync({ id: 'po1', req: { targetBinId: 'b1' } })

    expect(created.shipmentNumber).toBe('WE-PO-0001')
    expect(api.calls('POST', '/purchase-orders/po1/create-inbound')[0].body).toEqual({ targetBinId: 'b1' })
    expect(invalidatedKeys()).toEqual(expect.arrayContaining([queryKeys.inbound]))
  })

  it('das Buchen eines Wareneingangs lädt auch die Bestellungen neu (es schreibt den Empfang in die Bestellung fort)', async () => {
    const { wrapper, invalidatedKeys } = setup()
    const { result } = renderHook(() => useReceiveInbound(), { wrapper })

    await result.current.mutateAsync('in1')

    expect(invalidatedKeys()).toEqual(expect.arrayContaining([queryKeys.purchaseOrders, queryKeys.inbound, queryKeys.stock]))
  })
})
