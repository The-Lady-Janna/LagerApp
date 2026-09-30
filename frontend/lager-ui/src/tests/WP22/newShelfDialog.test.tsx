import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { NewShelfDialog } from '../../components/NewShelfDialog'
import { useAuth } from '../../state/auth'
import { fail, type MockApi } from '../helpers/mockApi'
import { renderWithProviders, signInAs } from '../helpers/render'
import { installLayoutApi, makeAisle, makeShelf, makeWarehouse, makeZone, type LayoutApi } from './fixtures'

// Viele Benutzeraktionen je Test: bei voller Maschinenlast (paralleler Testlauf) reichen die 5 s Standard-Timeout nicht immer.
vi.setConfig({ testTimeout: 30_000 })

type Body = Record<string, unknown>

/// <summary>
/// Regal anlegen ohne Vorwissen über Gang-Ids: ein einziger Gang ist vorgewählt, fehlt jeder Gang, legt der Dialog Zone und Gang
/// mit an, und ohne Lager verweist er auf die Lagerstruktur.
/// </summary>
describe('NewShelfDialog', () => {
  let server: LayoutApi
  let api: MockApi
  const onClose = vi.fn()
  const onCreated = vi.fn()

  function setup(layout: ReturnType<typeof makeWarehouse>[]) {
    server = installLayoutApi(layout, {
      'POST /warehouse/zones': (r) => {
        const body = r.body as Body
        const zone = makeZone('z-new', String(body.code), String(body.name))
        server.state.layout.find((w) => w.id === body.warehouseId)!.zones.push(zone)
        return zone
      },
      'POST /warehouse/aisles': (r) => {
        const body = r.body as Body
        const aisle = makeAisle('a-new', String(body.code), [], { zoneId: String(body.zoneId) })
        server.state.layout.flatMap((w) => w.zones).find((z) => z.id === body.zoneId)!.aisles.push(aisle)
        return aisle
      },
      'POST /warehouse/shelves': (r) => {
        const body = r.body as Body
        return makeShelf('s-new', String(body.code), [], { aisleId: String(body.aisleId) })
      },
    })
    api = server.api
    return userEvent.setup()
  }

  beforeEach(() => { signInAs(['Manager']); onClose.mockClear(); onCreated.mockClear() })
  afterEach(() => { api.restore(); useAuth.getState().logout() })

  const render = () => renderWithProviders(<NewShelfDialog open onClose={onClose} onCreated={onCreated} />)

  it('wählt den einzigen vorhandenen Gang vor und legt das Regal dort an', async () => {
    const user = setup([makeWarehouse('w1', 'WH01', 'Haupt', [makeZone('z1', 'Z-A', 'Zone A', [makeAisle('a1', 'A1')])])])
    render()

    const dialog = await screen.findByRole('dialog', { name: 'Neues Regal anlegen' })
    await waitFor(() => expect(within(dialog).getByLabelText('Gang')).toHaveValue('a1'))
    await user.type(within(dialog).getByLabelText('Code'), 'R1')
    await user.click(within(dialog).getByRole('button', { name: 'Regal anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/shelves')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/shelves')[0].body).toMatchObject({ aisleId: 'a1', code: 'R1', initialBinCount: 3, binMaxWeightGrams: 50_000 })
    expect(api.calls('POST', '/warehouse/zones')).toHaveLength(0)
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
    expect(onCreated).toHaveBeenCalledWith('Regal "R1" angelegt')
  })

  it('verlangt bei mehreren Gängen eine Auswahl', async () => {
    const user = setup([makeWarehouse('w1', 'WH01', 'Haupt', [makeZone('z1', 'Z-A', 'Zone A', [makeAisle('a1', 'A1'), makeAisle('a2', 'A2')])])])
    render()

    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText('Code'), 'R1')
    expect(within(dialog).getByRole('button', { name: 'Regal anlegen' })).toBeDisabled()   // noch kein Gang gewählt
    await user.selectOptions(within(dialog).getByLabelText('Gang'), 'a2')
    await user.click(within(dialog).getByRole('button', { name: 'Regal anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/shelves')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/shelves')[0].body).toMatchObject({ aisleId: 'a2' })
  })

  it('legt ohne jeden Gang zuerst Zone und Gang an und dann das Regal im neuen Gang', async () => {
    const user = setup([makeWarehouse('w1', 'WH01', 'Haupt')])
    render()

    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText(/Es gibt noch keinen Gang/)).toHaveTextContent('die Zone „Z1“ und der Gang „A1“')
    await user.type(within(dialog).getByLabelText('Code'), 'R1')
    await user.click(within(dialog).getByRole('button', { name: 'Regal anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/shelves')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/zones')[0].body).toEqual({ warehouseId: 'w1', code: 'Z1', name: 'Zone 1' })
    expect(api.calls('POST', '/warehouse/aisles')[0].body).toEqual({ zoneId: 'z-new', code: 'A1' })
    expect(api.calls('POST', '/warehouse/shelves')[0].body).toMatchObject({ aisleId: 'a-new', code: 'R1' })
    // Reihenfolge: Zone, Gang, Regal
    expect(api.requests.filter((r) => r.method === 'POST').map((r) => r.path)).toEqual(['/warehouse/zones', '/warehouse/aisles', '/warehouse/shelves'])
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
  })

  it('nutzt eine vorhandene Zone ohne Gang, statt eine zweite anzulegen', async () => {
    const user = setup([makeWarehouse('w1', 'WH01', 'Haupt', [makeZone('z1', 'Z1', 'Zone 1')])])
    render()

    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText(/Es gibt noch keinen Gang/)).not.toHaveTextContent('Zone')
    await user.type(within(dialog).getByLabelText('Code'), 'R1')
    await user.click(within(dialog).getByRole('button', { name: 'Regal anlegen' }))

    await waitFor(() => expect(api.calls('POST', '/warehouse/shelves')).toHaveLength(1))
    expect(api.calls('POST', '/warehouse/zones')).toHaveLength(0)
    expect(api.calls('POST', '/warehouse/aisles')[0].body).toEqual({ zoneId: 'z1', code: 'A1' })
  })

  it('bleibt bei einem Fehler offen und zeigt ihn an (z. B. doppelte Lagerplatz-Codes)', async () => {
    const user = setup([makeWarehouse('w1', 'WH01', 'Haupt', [makeZone('z1', 'Z-A', 'Zone A', [makeAisle('a1', 'A1')])])])
    api.setRoute('POST /warehouse/shelves', fail(409, {
      code: 'duplicate_code', title: 'Konflikt', status: 409, detail: "Die Lagerplatz-Codes 'R1-01', 'R1-02' sind schon vergeben.",
    }))
    render()

    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText('Code'), 'R1')
    await user.click(within(dialog).getByRole('button', { name: 'Regal anlegen' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("Die Lagerplatz-Codes 'R1-01', 'R1-02' sind schon vergeben.")
    expect(onClose).not.toHaveBeenCalled()
    expect(onCreated).not.toHaveBeenCalled()
  })

  it('verweist ohne Lager auf die Lagerstruktur (Link für Manager, Hinweis für alle anderen)', async () => {
    setup([])
    const { unmount } = render()

    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText(/Es gibt noch kein Lager/)).toBeInTheDocument()
    expect(within(dialog).getByRole('link', { name: 'Lagerstruktur' })).toHaveAttribute('href', '/warehouses')
    expect(within(dialog).getByRole('button', { name: 'Regal anlegen' })).toBeDisabled()
    unmount()

    signInAs(['Viewer'])
    render()
    const viewerDialog = await screen.findByRole('dialog')
    expect(await within(viewerDialog).findByText(/Ein Manager muss zuerst ein Lager anlegen/)).toBeInTheDocument()
    expect(within(viewerDialog).queryByRole('link', { name: 'Lagerstruktur' })).not.toBeInTheDocument()
  })
})
