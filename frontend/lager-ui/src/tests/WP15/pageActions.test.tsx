import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import type { AxiosAdapter, AxiosResponse, InternalAxiosRequestConfig } from 'axios'
import { apiClient } from '../../api/client'
import type { InboundShipmentDto, InventoryCountDto, ReturnShipmentDto, UserDto } from '../../api/types'
import { InboundPage } from '../../pages/InboundPage'
import { InventoryPage } from '../../pages/InventoryPage'
import { PackPickListPage } from '../../pages/PackPickListPage'
import { PickListsPage } from '../../pages/PickListsPage'
import { ReturnsPage } from '../../pages/ReturnsPage'
import { UsersPage } from '../../pages/UsersPage'
import { useAuth } from '../../state/auth'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'
import { makePickItem, makePickList, makeReturn } from '../helpers/fixtures'
import { renderWithProviders, signInAs } from '../helpers/render'

/**
 * Hält Requests, deren URL passt, zurück, bis release() aufgerufen wird — so bleibt eine Mutation "pending" und der
 * Test kann prüfen, was die Oberfläche währenddessen tut. `started()` zählt die tatsächlich abgeschickten Requests.
 */
function holdRequests(match: RegExp) {
  const inner = apiClient.defaults.adapter as unknown as (config: InternalAxiosRequestConfig) => Promise<AxiosResponse>
  let release = () => {}
  const gate = new Promise<void>((resolve) => { release = resolve })
  let started = 0
  const held: AxiosAdapter = async (config) => {
    if (match.test(config.url ?? '')) {
      started += 1
      await gate
    }
    return inner(config)
  }
  apiClient.defaults.adapter = held
  return { release: () => release(), started: () => started }
}

const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 30))

describe('Wareneingang buchen (InboundPage)', () => {
  let api: MockApi
  let shipment: InboundShipmentDto

  beforeEach(() => {
    shipment = {
      id: 'in1', shipmentNumber: 'WE-0001', supplierReference: null, notes: null, status: 'Draft',
      createdAt: '2025-01-01T10:00:00', receivedAt: null,
      lines: [{ id: 'l1', articleId: 'a1', articleSku: 'ART-001', targetBinId: 'b1', targetBinCode: 'A-01-01', quantity: 5, lotNumber: null, expiryDate: null }],
    }
    api = installMockApi({
      'GET /inbound': () => [shipment],
      'GET /articles': [],
      'GET /warehouse/storage-locations': [],
      'POST /inbound/:id/receive': () => {
        shipment = { ...shipment, status: 'Received', receivedAt: '2025-01-02T08:00:00' }
        return shipment
      },
    })
  })
  afterEach(() => api.restore())

  it('bucht erst nach der Bestätigung im Dialog; "Abbrechen" bucht nichts', async () => {
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)

    await user.click(await screen.findByRole('button', { name: /Empfangen/ }))
    const dialog = screen.getByRole('dialog', { name: 'Wareneingang buchen?' })
    expect(dialog).toHaveTextContent('WE-0001')
    expect(api.calls('POST', '/inbound/in1/receive')).toHaveLength(0)

    await user.click(within(dialog).getByRole('button', { name: 'Abbrechen' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(api.calls('POST', '/inbound/in1/receive')).toHaveLength(0)

    await user.click(screen.getByRole('button', { name: /Empfangen/ }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Wareneingang buchen' }))
    await waitFor(() => expect(api.calls('POST', '/inbound/in1/receive')).toHaveLength(1))
  })

  it('Doppelklick auf "Bestätigen" löst die Buchung nur einmal aus; der Button ist gesperrt, solange sie läuft', async () => {
    const hold = holdRequests(/\/receive$/)
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)

    await user.click(await screen.findByRole('button', { name: /Empfangen/ }))
    await user.dblClick(within(screen.getByRole('dialog')).getByRole('button', { name: 'Wareneingang buchen' }))

    await waitFor(() => expect(hold.started()).toBeGreaterThanOrEqual(1))
    await settle()
    expect(hold.started()).toBe(1)
    // Buchung läuft: der Zeilen-Button ist gesperrt, ein weiterer Klick öffnet nicht einmal einen Dialog.
    await waitFor(() => expect(screen.getByRole('button', { name: /Empfangen/ })).toBeDisabled())

    hold.release()
    await waitFor(() => expect(api.calls('POST', '/inbound/in1/receive')).toHaveLength(1))
    // Danach ist die Lieferung gebucht (Status "Received"), der Button verschwindet.
    await waitFor(() => expect(screen.queryByRole('button', { name: /Empfangen/ })).not.toBeInTheDocument())
  })

  it('zeigt einen 409-ProblemDetails-Fehler der Buchung als deutschen Text mit Referenz und gibt den Button wieder frei', async () => {
    api.setRoute('POST /inbound/:id/receive', fail(409, {
      title: 'Conflict', status: 409, detail: 'Der Wareneingang wurde bereits gebucht.', code: 'conflict', correlationId: 'corr-42',
    }))
    const user = userEvent.setup()
    renderWithProviders(<InboundPage />)

    await user.click(await screen.findByRole('button', { name: /Empfangen/ }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Wareneingang buchen' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Wareneingang buchen fehlgeschlagen:')
    expect(alert).toHaveTextContent('Der Wareneingang wurde bereits gebucht.')
    expect(alert).toHaveTextContent('Referenz: corr-42')
    expect(alert).not.toHaveTextContent('AxiosError')
    expect(screen.getByRole('button', { name: /Empfangen/ })).toBeEnabled()
  })
})

describe('Retoure verarbeiten (ReturnsPage)', () => {
  let api: MockApi
  let ret: ReturnShipmentDto

  beforeEach(() => {
    ret = makeReturn({
      lines: [{ id: 'rl1', articleId: 'a1', articleSku: 'ART-001', quantity: 2, lotNumber: null, qcResult: 'Sellable', targetBinId: 'b1', qcNotes: null }],
    })
    api = installMockApi({
      'GET /returns': () => [ret],
      'GET /articles': [],
      'GET /warehouse/storage-locations': [],
      'POST /returns/:id/process': () => {
        ret = { ...ret, status: 'Processed', processedAt: '2025-01-02T08:00:00' }
        return ret
      },
    })
  })
  afterEach(() => api.restore())

  it('öffnet einen Bestätigungsdialog, der den Bestand nennt; Doppelklick bucht nur einmal', async () => {
    const hold = holdRequests(/\/process$/)
    const user = userEvent.setup()
    renderWithProviders(<ReturnsPage />)

    await user.click(await screen.findByRole('button', { name: /Verarbeiten/ }))
    const dialog = screen.getByRole('dialog', { name: 'Retoure verarbeiten?' })
    expect(dialog).toHaveTextContent('RMA-0001')
    expect(dialog).toHaveTextContent('2 verkaufsfähige(s) Stück')
    expect(hold.started()).toBe(0)

    await user.dblClick(within(dialog).getByRole('button', { name: 'Retoure verarbeiten' }))
    await waitFor(() => expect(hold.started()).toBeGreaterThanOrEqual(1))
    await settle()
    expect(hold.started()).toBe(1)

    hold.release()
    await waitFor(() => expect(api.calls('POST', '/returns/r1/process')).toHaveLength(1))
  })

  it('zeigt einen Fehler beim Verarbeiten an', async () => {
    api.setRoute('POST /returns/:id/process', fail(400, { error: 'Retoure ist bereits verarbeitet' }))
    const user = userEvent.setup()
    renderWithProviders(<ReturnsPage />)

    await user.click(await screen.findByRole('button', { name: /Verarbeiten/ }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Retoure verarbeiten' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Retoure ist bereits verarbeitet')
  })
})

describe('Ladezustand statt leerer Liste (LoadState)', () => {
  let api: MockApi
  beforeEach(() => {
    api = installMockApi({ 'GET /returns': fail(500, null), 'GET /articles': [], 'GET /warehouse/storage-locations': [] })
  })
  afterEach(() => api.restore())

  it('zeigt beim Laden "Lade …" und bei einem Fehler Fehlertext plus "Erneut versuchen" — nie eine leere Tabelle', async () => {
    const user = userEvent.setup()
    renderWithProviders(<ReturnsPage />)

    expect(screen.getByRole('status')).toHaveTextContent('Lade die Retouren')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Konnte die Retouren nicht laden.')
    expect(alert).toHaveTextContent('Serverfehler (HTTP 500)')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()

    api.setRoute('GET /returns', [makeReturn()])
    await user.click(screen.getByRole('button', { name: 'Erneut versuchen' }))
    expect(await screen.findByText('RMA-0001')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('Inventur abschließen (InventoryPage)', () => {
  let api: MockApi
  const line = (id: string, countedQty: number | null) => ({
    id, binId: 'b1', binCode: 'A-01-01', articleId: 'a1', articleSku: `ART-${id}`, expectedQty: 5, countedQty, diff: countedQty === null ? 0 : countedQty - 5, reason: null,
  })
  const count = (lines: InventoryCountDto['lines']): InventoryCountDto => ({
    id: 'inv1', name: 'Jahresinventur', status: 'Open', createdAt: '2025-01-01T10:00:00', reconciledAt: null, lines,
  })
  const renderDetail = () =>
    renderWithProviders(
      <Routes><Route path="/inventory/:id" element={<InventoryPage />} /></Routes>,
      { initialEntries: ['/inventory/inv1'] },
    )

  beforeEach(() => {
    api = installMockApi({ 'POST /inventory/:id/reconcile': count([]) })
  })
  afterEach(() => api.restore())

  it('sperrt den Abschluss, solange Positionen ungezählt sind (der Server lehnt ihn sonst ab)', async () => {
    api.setRoute('GET /inventory/inv1', count([line('1', 5), line('2', null)]))
    renderDetail()

    const button = await screen.findByRole('button', { name: /Reconcile/ })
    expect(button).toBeDisabled()
    expect(screen.getByText('Noch 1 Position(en) ohne Zählung.')).toBeInTheDocument()
  })

  it('fragt vor dem Buchen der Differenzen nach und zeigt Serverfehler an', async () => {
    api.setRoute('GET /inventory/inv1', count([line('1', 4), line('2', 5)]))
    api.setRoute('POST /inventory/:id/reconcile', fail(409, { detail: 'Die Inventur wurde bereits abgeschlossen.', status: 409 }))
    const user = userEvent.setup()
    renderDetail()

    await user.click(await screen.findByRole('button', { name: /Reconcile/ }))
    const dialog = screen.getByRole('dialog', { name: 'Inventur abschließen?' })
    expect(api.calls('POST', '/inventory/inv1/reconcile')).toHaveLength(0)
    await user.click(within(dialog).getByRole('button', { name: 'Inventur abschließen' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Die Inventur wurde bereits abgeschlossen.')
    expect(api.calls('POST', '/inventory/inv1/reconcile')).toHaveLength(1)
  })
})

describe('Benutzer (UsersPage): Aussperr-Schutz', () => {
  let api: MockApi
  const user = (id: string, username: string, roles: string[]): UserDto => ({
    id, username, email: null, displayName: null, roles, isActive: true, mustChangePassword: false, lastLoginAt: null, createdAt: '2025-01-01T10:00:00',
  })

  beforeEach(() => {
    signInAs(['Admin'], { id: 'u-me', username: 'chef' })
    api = installMockApi({
      'GET /users': [user('u-me', 'chef', ['Admin']), user('u-2', 'anna', ['Admin', 'Picker'])],
      'POST /users/:id/deactivate': user('u-2', 'anna', ['Admin', 'Picker']),
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  const rowOf = (username: string) => screen.getByText(username).closest('tr') as HTMLElement

  it('sperrt Deaktivieren und den Entzug der Admin-Rolle für den eigenen Account', async () => {
    renderWithProviders(<UsersPage />)
    await screen.findByText('chef')

    const own = within(rowOf('chef'))
    expect(own.getByRole('button', { name: 'Deaktivieren' })).toBeDisabled()
    expect(own.getByRole('checkbox', { name: 'Admin' })).toBeDisabled()
    expect(own.getByRole('checkbox', { name: 'Admin' })).toBeChecked()

    const other = within(rowOf('anna'))
    expect(other.getByRole('button', { name: 'Deaktivieren' })).toBeEnabled()
    expect(other.getByRole('checkbox', { name: 'Admin' })).toBeEnabled()
  })

  it('fragt vor dem Deaktivieren eines anderen Benutzers nach', async () => {
    const userEv = userEvent.setup()
    renderWithProviders(<UsersPage />)
    await screen.findByText('anna')

    await userEv.click(within(rowOf('anna')).getByRole('button', { name: 'Deaktivieren' }))
    const dialog = screen.getByRole('dialog', { name: 'Benutzer deaktivieren?' })
    expect(dialog).toHaveTextContent('anna')
    expect(api.calls('POST', '/users/u-2/deactivate')).toHaveLength(0)

    await userEv.click(within(dialog).getByRole('button', { name: 'Deaktivieren' }))
    await waitFor(() => expect(api.calls('POST', '/users/u-2/deactivate')).toHaveLength(1))
  })

  it('zeigt Fehler beim Deaktivieren an (vorher verschwanden sie still)', async () => {
    api.setRoute('POST /users/:id/deactivate', fail(403, null))
    const userEv = userEvent.setup()
    renderWithProviders(<UsersPage />)
    await screen.findByText('anna')

    await userEv.click(within(rowOf('anna')).getByRole('button', { name: 'Deaktivieren' }))
    await userEv.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Deaktivieren' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Keine Berechtigung für diese Aktion.')
  })
})

describe('Packen (PackPickListPage)', () => {
  let api: MockApi
  const renderPack = () =>
    renderWithProviders(
      <Routes><Route path="/picklists/:id/pack" element={<PackPickListPage />} /></Routes>,
      { initialEntries: ['/picklists/pl1/pack'] },
    )

  beforeEach(() => {
    api = installMockApi({
      'GET /picklists/pl1': makePickList({ status: 'Picked', items: [makePickItem({ id: 'i1', articleSku: 'ART-001', quantity: 5 })] }),
      'POST /picklists/pl1/pack': makePickList({ status: 'Completed' }),
    })
  })
  afterEach(() => api.restore())

  it('begrenzt die Ist-Menge auf 0..Soll (max = Planmenge), damit der Server sie nicht ablehnen muss', async () => {
    renderPack()
    const input = await screen.findByLabelText('Ist-Menge ART-001')
    expect(input).toHaveAttribute('max', '5')
    expect(input).toHaveAttribute('min', '0')

    fireEvent.change(input, { target: { value: '9' } })
    expect(input).toHaveValue(5)
    fireEvent.change(input, { target: { value: '-3' } })
    expect(input).toHaveValue(0)
  })

  it('zeigt die Ablehnung des Servers (409 Fehlbestand) lesbar an, statt still zu scheitern', async () => {
    api.setRoute('POST /picklists/pl1/pack', fail(409, { title: 'Conflict', status: 409, detail: 'Fehlbestand bei ART-001.', code: 'insufficient_stock' }))
    const user = userEvent.setup()
    renderPack()

    await user.click(await screen.findByRole('button', { name: 'Packen abschließen' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Packen fehlgeschlagen:')
    expect(alert).toHaveTextContent('Fehlbestand bei ART-001.')
    expect(screen.getByRole('button', { name: 'Packen abschließen' })).toBeEnabled()
  })
})

describe('Picklisten zurücksetzen (PickListsPage)', () => {
  let api: MockApi
  beforeEach(() => {
    signInAs(['Admin'])
    api = installMockApi({
      'GET /picklists': [makePickList({ id: 'pl1', status: 'Pending' })],
      'DELETE /picklists': { deleted: 1, skippedCompleted: 2 },
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it('zeigt neben den entfernten auch die übersprungenen (bereits gepackten) Picklisten an', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    renderWithProviders(<PickListsPage />)

    await user.click(await screen.findByRole('button', { name: /Nicht gepackte Picklisten zurücksetzen/ }))

    expect(await screen.findByText(/1 nicht gepackte Pickliste\(n\) entfernt/)).toHaveTextContent('2 bereits gepackte Pickliste(n) blieben erhalten')
  })
})
