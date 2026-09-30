import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { WarehousesPage } from '../../features/warehouses/WarehousesPage'
import { fail, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'
import { installLayoutApi, makeAisle, makeBin, makeShelf, makeTree, makeWarehouse, makeZone, type LayoutApi } from './fixtures'

// Viele Benutzeraktionen je Test: bei voller Maschinenlast (paralleler Testlauf) reichen die 5 s Standard-Timeout nicht immer.
vi.setConfig({ testTimeout: 30_000 })

type Body = Record<string, unknown>

describe('Lagerstruktur-Seite: Baum, Anlegen, Bearbeiten, Löschen', () => {
  let server: LayoutApi
  let api: MockApi

  /** Baut die Seite gegen den Baum auf; die Routen ändern den "Server"-Zustand wie ein echter Server. */
  function setup(initial = makeTree()) {
    server = installLayoutApi(initial, {
      'POST /warehouse': (r) => {
        const body = r.body as Body
        const created = makeWarehouse('w-new', String(body.code), String(body.name))
        server.state.layout.push(created)
        return created
      },
      'POST /warehouse/zones': (r) => {
        const body = r.body as Body
        const created = makeZone('z-new', String(body.code), String(body.name))
        server.state.layout[0].zones.push(created)
        return created
      },
      'DELETE /warehouse/shelves/:id': (_r, params) => {
        for (const aisle of server.state.layout.flatMap((w) => w.zones).flatMap((z) => z.aisles)) {
          aisle.shelves = aisle.shelves.filter((s) => s.id !== params.id)
        }
        return undefined
      },
      'POST /warehouse/shelves/:id/bins': (r, params) => {
        const body = r.body as Body
        const bin = makeBin('b-new', String(body.code), { shelfId: params.id })
        server.state.layout[0].zones[0].aisles[0].shelves.find((s) => s.id === params.id)!.locations.push(bin)
        return bin
      },
      'PUT /warehouse/storage-locations/:id/bin-type': (r, params) => {
        const body = r.body as Body
        const bin = server.state.layout[0].zones[0].aisles[0].shelves[0].locations.find((b) => b.id === params.id)!
        Object.assign(bin, { binType: body.binType, replenishmentThreshold: body.replenishmentThreshold })
        return bin
      },
    })
    api = server.api
    signInAs(['Manager'])
    return userEvent.setup()
  }

  afterEach(() => api.restore())
  beforeEach(() => { signInAs(['Manager']) })

  it('zeigt den Baum: Lager, Zone und Gang aufgeklappt, Regale zugeklappt; ein Klick zeigt die Lagerplätze mit Bin-Typ und Schwelle', async () => {
    const user = setup()
    renderWithProviders(<WarehousesPage />)

    expect(await screen.findByText('Hauptlager')).toBeInTheDocument()
    expect(screen.getByText('Zone A')).toBeInTheDocument()
    expect(screen.getByText('A1')).toBeInTheDocument()
    expect(screen.getByText('S1')).toBeInTheDocument()
    expect(screen.queryByText('S1-01')).not.toBeInTheDocument()   // Regal zugeklappt

    await user.click(screen.getByRole('button', { name: 'Regal S1 aufklappen' }))

    expect(screen.getByText('S1-01')).toBeInTheDocument()
    expect(screen.getByText('S1-02')).toBeInTheDocument()
    expect(screen.getByText('Hot-Pick')).toHaveAttribute('data-tone', 'warning')
    expect(screen.getByText('Schwelle 10')).toBeInTheDocument()
    // Zusammenfassung je Knoten: was darunter hängt
    expect(screen.getByText('1 Zone, 1 Gang, 1 Regal, 2 Lagerplätze')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Zone Z-A einklappen' }))
    expect(screen.queryByText('A1')).not.toBeInTheDocument()
  })

  it('zeigt beim Erststart den Leerzustand und legt darüber das erste Lager an', async () => {
    const user = setup([])
    renderWithProviders(<WarehousesPage />)

    expect(await screen.findByRole('heading', { name: 'Noch kein Lager – jetzt anlegen' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Lager anlegen' }))
    const dialog = await screen.findByRole('dialog', { name: 'Neues Lager' })
    await user.type(within(dialog).getByLabelText('Code'), ' WH01 ')
    await user.type(within(dialog).getByLabelText('Name'), 'Hauptlager')
    await user.click(within(dialog).getByRole('button', { name: 'Lager anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse')[0].body).toEqual({ code: 'WH01', name: 'Hauptlager' })
    // Nach dem Anlegen ist der Leerzustand weg, der Baum zeigt das neue Lager, und die Meldung bestätigt es.
    expect(await screen.findByText('Hauptlager')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Noch kein Lager – jetzt anlegen' })).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Lager "WH01" angelegt')
  })

  it('legt eine Zone im Lager an (Code und Name vorbelegt) und zeigt sie im Baum', async () => {
    const user = setup()
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: '+ Zone hinzufügen: Lager WH01' }))
    const dialog = await screen.findByRole('dialog', { name: 'Neue Zone in WH01' })
    expect(within(dialog).getByLabelText('Code')).toHaveValue('Z1')   // Z-A existiert, Z1 ist der nächste freie Vorschlag
    await user.clear(within(dialog).getByLabelText('Code'))
    await user.type(within(dialog).getByLabelText('Code'), 'Z-B')
    await user.clear(within(dialog).getByLabelText('Name'))
    await user.type(within(dialog).getByLabelText('Name'), 'Zone B')
    await user.click(within(dialog).getByRole('button', { name: 'Zone anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/zones')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/zones')[0].body).toEqual({ warehouseId: 'w1', code: 'Z-B', name: 'Zone B' })
    expect(await screen.findByText('Zone B')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('zeigt den Fehler des Servers (doppelter Code) im Dialog und lässt ihn zum Korrigieren offen', async () => {
    const user = setup()
    api.setRoute('POST /warehouse/zones', fail(409, {
      code: 'duplicate_code', title: 'Konflikt', status: 409, detail: "Der Zonen-Code 'Z-A' ist in diesem Lager schon vergeben.",
    }))
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: '+ Zone hinzufügen: Lager WH01' }))
    const dialog = await screen.findByRole('dialog', { name: 'Neue Zone in WH01' })
    await user.clear(within(dialog).getByLabelText('Code'))
    await user.type(within(dialog).getByLabelText('Code'), 'Z-A')
    await user.click(within(dialog).getByRole('button', { name: 'Zone anlegen' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("Der Zonen-Code 'Z-A' ist in diesem Lager schon vergeben.")
    expect(screen.getByRole('dialog', { name: 'Neue Zone in WH01' })).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Zone anlegen' })).toBeEnabled()
  })

  it('fragt vor dem Löschen nach (mit dem, was mitgelöscht wird) und entfernt das Regal erst nach der Bestätigung', async () => {
    const user = setup()
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: 'Löschen: Regal S1' }))
    const dialog = await screen.findByRole('dialog', { name: 'Regal "S1" löschen?' })
    expect(dialog).toHaveTextContent('2 Lagerplätze')
    expect(dialog).toHaveTextContent('nicht rückgängig')
    expect(api.calls('DELETE', '/warehouse/shelves/:id')).toHaveLength(0)   // noch nichts passiert

    await user.click(within(dialog).getByRole('button', { name: 'Löschen' }))

    await waitFor(() => expect(api.calls('DELETE', '/warehouse/shelves/s1')).toHaveLength(1))
    await waitFor(() => expect(screen.queryByText('S1')).not.toBeInTheDocument())
    expect(screen.getByRole('status')).toHaveTextContent('Regal "S1" gelöscht')
  })

  it('bricht das Löschen mit "Abbrechen" ohne Aufruf ab; lehnt der Server ab (warehouse_not_empty), bleibt alles stehen und der Grund steht im Dialog', async () => {
    const user = setup()
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: 'Löschen: Regal S1' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Abbrechen' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(api.calls('DELETE', '/warehouse/shelves/:id')).toHaveLength(0)

    api.setRoute('DELETE /warehouse/shelves/:id', fail(409, {
      code: 'warehouse_not_empty', title: 'Konflikt', status: 409,
      detail: "Das Regal 'S1' kann nicht gelöscht werden: der Lagerplatz 'S1-01' hat noch Bestand oder offene Aufgaben.",
    }))
    await user.click(screen.getByRole('button', { name: 'Löschen: Regal S1' }))
    const dialog = await screen.findByRole('dialog', { name: 'Regal "S1" löschen?' })
    await user.click(within(dialog).getByRole('button', { name: 'Löschen' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("der Lagerplatz 'S1-01' hat noch Bestand")
    expect(screen.getByText('S1')).toBeInTheDocument()   // nichts wurde entfernt
    // Ein erneutes Öffnen startet ohne den alten Fehler.
    await user.click(within(dialog).getByRole('button', { name: 'Abbrechen' }))
    await user.click(screen.getByRole('button', { name: 'Löschen: Regal S1' }))
    expect(within(await screen.findByRole('dialog')).queryByRole('alert')).not.toBeInTheDocument()
  })

  it('stellt Bin-Typ und Nachschub-Schwelle eines Lagerplatzes ein (Schwelle nur für Hot-Pick)', async () => {
    const user = setup()
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: 'Regal S1 aufklappen' }))
    await user.click(screen.getByRole('button', { name: 'Bin-Typ: Lagerplatz S1-01' }))
    const dialog = await screen.findByRole('dialog', { name: 'Bin-Typ von S1-01' })
    expect(within(dialog).getByLabelText('Bin-Typ')).toHaveValue('Standard')
    await user.selectOptions(within(dialog).getByLabelText('Bin-Typ'), 'HotPick')
    await user.clear(within(dialog).getByLabelText('Nachschub-Schwelle (Stück)'))
    await user.type(within(dialog).getByLabelText('Nachschub-Schwelle (Stück)'), '15')
    await user.click(within(dialog).getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('PUT', '/warehouse/storage-locations/b1/bin-type')).toHaveLength(1))
    expect(api.calls('PUT', '/warehouse/storage-locations/b1/bin-type')[0].body).toEqual({ binType: 'HotPick', replenishmentThreshold: 15 })
    expect(await screen.findByText('Schwelle 15')).toBeInTheDocument()

    // Reserve: die Schwelle gilt nicht und wird als 0 gesendet.
    await user.click(screen.getByRole('button', { name: 'Bin-Typ: Lagerplatz S1-02' }))
    const second = await screen.findByRole('dialog', { name: 'Bin-Typ von S1-02' })
    expect(within(second).getByLabelText('Nachschub-Schwelle (Stück)')).toHaveValue(10)
    await user.selectOptions(within(second).getByLabelText('Bin-Typ'), 'Reserve')
    await user.click(within(second).getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('PUT', '/warehouse/storage-locations/b2/bin-type')).toHaveLength(1))
    expect(api.calls('PUT', '/warehouse/storage-locations/b2/bin-type')[0].body).toEqual({ binType: 'Reserve', replenishmentThreshold: 0 })
  })

  it('legt einen Lagerplatz mit dem nächsten freien Code an', async () => {
    const user = setup()
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: '+ Lagerplatz hinzufügen: Regal S1' }))
    const dialog = await screen.findByRole('dialog', { name: 'Neuer Lagerplatz in Regal S1' })
    expect(within(dialog).getByLabelText('Code')).toHaveValue('S1-03')
    await user.click(within(dialog).getByRole('button', { name: 'Lagerplatz anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/shelves/s1/bins')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/shelves/s1/bins')[0].body).toEqual({ code: 'S1-03', widthMm: 600, depthMm: 600, heightMm: 500, maxWeightGrams: 50_000 })
  })

  it('legt ein Regal im gewählten Gang an, ohne dass man eine Gang-Id kennen muss', async () => {
    const user = setup()
    server.state.layout[0].zones[0].aisles.push(makeAisle('a2', 'A2', [], { zoneId: 'z1' }))
    api.setRoute('POST /warehouse/shelves', (r) => {
      const body = r.body as Body
      return makeShelf('s-new', String(body.code), [], { aisleId: String(body.aisleId) })
    })
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: '+ Regal hinzufügen: Gang A2' }))
    const dialog = await screen.findByRole('dialog', { name: 'Neues Regal anlegen' })
    expect(within(dialog).getByText('WH01 / Z-A / A2')).toBeInTheDocument()   // fester Gang, keine Auswahl
    expect(within(dialog).queryByRole('combobox')).not.toBeInTheDocument()
    await user.type(within(dialog).getByLabelText('Code'), 'S2')
    await user.click(within(dialog).getByRole('button', { name: 'Regal anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/shelves')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/shelves')[0].body).toMatchObject({ aisleId: 'a2', code: 'S2', initialBinCount: 3 })
  })

  it('benennt ein Lager um (Bearbeiten) mit den bisherigen Werten als Vorgabe', async () => {
    const user = setup()
    api.setRoute('PUT /warehouse/:id', (r) => ({ ...makeWarehouse('w1', 'x', 'x'), ...(r.body as Body) }))
    renderWithProviders(<WarehousesPage />)

    await user.click(await screen.findByRole('button', { name: 'Bearbeiten: Lager WH01' }))
    const dialog = await screen.findByRole('dialog', { name: 'Lager WH01 bearbeiten' })
    expect(within(dialog).getByLabelText('Code')).toHaveValue('WH01')
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Hauptlager')
    await user.clear(within(dialog).getByLabelText('Name'))
    await user.type(within(dialog).getByLabelText('Name'), 'Zentrallager')
    await user.click(within(dialog).getByRole('button', { name: 'Speichern' }))

    await waitFor(() => expect(api.calls('PUT', '/warehouse/w1')).toHaveLength(1))
    expect(api.calls('PUT', '/warehouse/w1')[0].body).toEqual({ code: 'WH01', name: 'Zentrallager' })
  })
})
