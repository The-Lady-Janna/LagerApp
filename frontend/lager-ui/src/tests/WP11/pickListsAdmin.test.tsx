import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { PickListsPage } from '../../pages/PickListsPage'
import { useAuth } from '../../state/auth'
import { installMockApi, type MockApi } from '../helpers/mockApi'
import { makePickList } from '../helpers/fixtures'
import { renderWithProviders, signInAs } from '../helpers/render'

// frontend#7: Reset-/Demo-Funktionen sind zerstörerisch und standen jedem angemeldeten Nutzer zur Verfügung.
describe('PickListsPage — Wartungs-Werkzeuge', () => {
  let api: MockApi

  beforeEach(() => {
    api = installMockApi({
      'GET /picklists': [
        makePickList({ id: 'pl1', pickListNumber: 'PL-0001', status: 'Pending' }),
        makePickList({ id: 'pl2', pickListNumber: 'PL-0002', status: 'Picked' }),
        makePickList({ id: 'pl3', pickListNumber: 'PL-0003', status: 'Completed' }),
        makePickList({ id: 'pl4', pickListNumber: 'PL-0004', status: 'Cancelled' }),
      ],
      'DELETE /picklists': { deleted: 2 },
      'POST /admin/seed-bulk': { message: 'ok', articles: 1, stockItems: 1, orders: 1 },
    })
  })
  afterEach(() => {
    api.restore()
    useAuth.getState().logout()
  })

  it.each([['Manager'], ['Picker'], ['Viewer']])('blendet die Werkzeuge für die Rolle %s aus', async (role) => {
    signInAs([role])
    renderWithProviders(<PickListsPage />)

    // Die Liste selbst bleibt für alle sichtbar.
    expect(await screen.findByText('PL-0001')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /zurücksetzen/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Demo-Daten/ })).not.toBeInTheDocument()
    expect(screen.queryByText('Bulk-Testdaten')).not.toBeInTheDocument()
  })

  it('zeigt Admins die Werkzeuge; der Reset nennt nur nicht gepackte Listen und fragt vorher nach', async () => {
    signInAs(['Admin'])
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const user = userEvent.setup()
    renderWithProviders(<PickListsPage />)

    // Nur die zwei nicht gepackten Listen (Pending, Picked) sind zurücksetzbar - weder die Completed- noch die stornierte Liste
    // (der Server lässt beide stehen).
    const reset = await screen.findByRole('button', { name: 'Nicht gepackte Picklisten zurücksetzen (2)' })
    expect(screen.getByRole('button', { name: /Demo-Daten/ })).toBeInTheDocument()

    await user.click(reset)
    expect(confirm).toHaveBeenCalledTimes(1)
    expect(confirm.mock.calls[0][0]).toContain('2 nicht gepackte Picklisten')
    expect(confirm.mock.calls[0][0]).toContain('nur Picklisten entfernt, die noch nicht gepackt sind')
    expect(api.calls('DELETE', '/picklists')).toHaveLength(0) // abgelehnt → nichts passiert

    confirm.mockReturnValue(true)
    await user.click(reset)
    expect(await screen.findByText(/2 nicht gepackte Pickliste\(n\) entfernt/)).toBeInTheDocument()
    expect(api.calls('DELETE', '/picklists')).toHaveLength(1)
  })

  it('verlangt vor Bulk-Testdaten eine Bestätigung', async () => {
    signInAs(['Admin'])
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const user = userEvent.setup()
    renderWithProviders(<PickListsPage />)

    await user.click(await screen.findByText('Bulk-Testdaten'))
    await user.click(screen.getByRole('button', { name: /Bulk-Daten/ }))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('ZUSÄTZLICH'))
    expect(api.calls('POST', '/admin/seed-bulk')).toHaveLength(0)

    confirm.mockReturnValue(true)
    await user.click(screen.getByRole('button', { name: /Bulk-Daten/ }))
    expect(api.calls('POST', '/admin/seed-bulk')).toHaveLength(1)
  })
})
