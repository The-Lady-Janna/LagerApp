import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Modal } from '../../components/Modal'
import { ChangePasswordDialog } from '../../components/ChangePasswordDialog'
import { useAuth } from '../../state/auth'
import { renderWithProviders, signInAs } from '../helpers/render'

describe('Modal', () => {
  it('ist ein modaler Dialog: role, aria-modal, Name aus dem Titel (aria-labelledby) und Beschreibung (aria-describedby)', () => {
    render(
      <Modal title="Regal anlegen" describedBy="hilfe" onClose={() => {}}>
        <p id="hilfe">Legt ein Regal mit Bins an.</p>
        <button>OK</button>
      </Modal>,
    )

    const dialog = screen.getByRole('dialog', { name: 'Regal anlegen' })
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(dialog).toHaveAccessibleDescription('Legt ein Regal mit Bins an.')
  })

  it('setzt den Fokus beim Öffnen in den Dialog (data-autofocus vor dem ersten Element) und sperrt das Scrollen der Seite', () => {
    const { unmount } = render(
      <Modal title="T" onClose={() => {}}>
        <button>Erster</button>
        <input aria-label="Feld" data-autofocus />
      </Modal>,
    )

    expect(screen.getByLabelText('Feld')).toHaveFocus()
    expect(document.body).toHaveClass('modal-open')
    unmount()
    expect(document.body).not.toHaveClass('modal-open')
  })

  it('Tab bleibt im Dialog (Fokusfalle in beide Richtungen)', async () => {
    const user = userEvent.setup()
    render(
      <>
        <button>Dahinter</button>
        <Modal title="T" onClose={() => {}}>
          <button>Eins</button>
          <button>Zwei</button>
        </Modal>
      </>,
    )

    expect(screen.getByRole('button', { name: 'Eins' })).toHaveFocus()
    await user.tab()
    expect(screen.getByRole('button', { name: 'Zwei' })).toHaveFocus()
    await user.tab()
    expect(screen.getByRole('button', { name: 'Eins' })).toHaveFocus()   // nicht zu "Dahinter"
    await user.tab({ shift: true })
    expect(screen.getByRole('button', { name: 'Zwei' })).toHaveFocus()   // rückwärts vom ersten zum letzten
  })

  it('Escape schließt; der Fokus kehrt zum auslösenden Element zurück', async () => {
    const user = userEvent.setup()
    function Host() {
      const [open, setOpen] = useState(false)
      return (
        <>
          <button onClick={() => setOpen(true)}>Öffnen</button>
          {open && <Modal title="T" onClose={() => setOpen(false)}><button>Im Dialog</button></Modal>}
        </>
      )
    }
    render(<Host />)

    await user.click(screen.getByRole('button', { name: 'Öffnen' }))
    expect(screen.getByRole('button', { name: 'Im Dialog' })).toHaveFocus()

    await user.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Öffnen' })).toHaveFocus()
  })

  it('merkt sich das auslösende Element auch, wenn ein Kind per autoFocus den Fokus sofort an sich zieht', async () => {
    const user = userEvent.setup()
    function Host() {
      const [open, setOpen] = useState(false)
      return (
        <>
          <button onClick={() => setOpen(true)}>Öffnen</button>
          {open && <Modal title="T" onClose={() => setOpen(false)}><input aria-label="Feld" autoFocus /></Modal>}
        </>
      )
    }
    render(<Host />)

    await user.click(screen.getByRole('button', { name: 'Öffnen' }))
    expect(screen.getByLabelText('Feld')).toHaveFocus()
    await user.keyboard('{Escape}')

    expect(screen.getByRole('button', { name: 'Öffnen' })).toHaveFocus()
  })

  it('schließt bei Escape nicht, wenn closeOnEscape=false (Pflicht-Dialog, laufende Aktion), ruft sonst onClose', async () => {
    const user = userEvent.setup()
    const onClose = vi.fn()
    const { rerender } = render(<Modal title="T" onClose={onClose} closeOnEscape={false}><button>OK</button></Modal>)

    await user.keyboard('{Escape}')
    expect(onClose).not.toHaveBeenCalled()

    rerender(<Modal title="T" onClose={onClose}><button>OK</button></Modal>)
    await user.keyboard('{Escape}')
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('schließt per Klick auf den Hintergrund nur mit closeOnBackdrop — ein Klick im Dialog nie', async () => {
    const user = userEvent.setup()
    const onClose = vi.fn()
    const { rerender } = render(<Modal title="T" onClose={onClose}><button>OK</button></Modal>)

    await user.click(screen.getByRole('dialog').parentElement!)
    expect(onClose).not.toHaveBeenCalled()

    rerender(<Modal title="T" onClose={onClose} closeOnBackdrop><button>OK</button></Modal>)
    await user.click(screen.getByRole('button', { name: 'OK' }))
    expect(onClose).not.toHaveBeenCalled()
    await user.click(screen.getByRole('dialog').parentElement!)
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('reagiert bei gestapelten Dialogen nur im obersten auf Escape', async () => {
    const user = userEvent.setup()
    const closeBottom = vi.fn()
    const closeTop = vi.fn()
    render(
      <>
        <Modal title="Unten" onClose={closeBottom}><button>Unten-OK</button></Modal>
        <Modal title="Oben" onClose={closeTop}><button>Oben-OK</button></Modal>
      </>,
    )

    await user.keyboard('{Escape}')

    expect(closeTop).toHaveBeenCalledTimes(1)
    expect(closeBottom).not.toHaveBeenCalled()
  })

  it('ist auf schmalen Bildschirmen begrenzt: die Breite ist ein Maximum (--modal-width), keine feste Breite', () => {
    render(<Modal title="T" width={560} onClose={() => {}}>x</Modal>)

    const dialog = screen.getByRole('dialog')
    expect(dialog.style.getPropertyValue('--modal-width')).toBe('560px')
    expect(dialog.style.width).toBe('')
  })
})

describe('ChangePasswordDialog auf Basis von Modal', () => {
  it('ist ein benannter Dialog mit beschrifteten Feldern und lässt sich (freiwillig) per Escape schließen', async () => {
    signInAs(['Viewer'])
    const user = userEvent.setup()
    const onClose = vi.fn()
    renderWithProviders(<ChangePasswordDialog onClose={onClose} />)

    expect(screen.getByRole('dialog', { name: 'Passwort ändern' })).toBeInTheDocument()
    expect(screen.getByLabelText('Aktuelles Passwort')).toHaveFocus()
    await user.keyboard('{Escape}')

    expect(onClose).toHaveBeenCalledTimes(1)
    useAuth.getState().logout()
  })

  it('im Pflichtwechsel (mustChangePassword) schließt weder Escape noch ein Klick daneben — nur Ändern oder Abmelden', async () => {
    signInAs(['Viewer'], { mustChangePassword: true })
    const user = userEvent.setup()
    const onClose = vi.fn()
    renderWithProviders(<ChangePasswordDialog onClose={onClose} />)

    await user.keyboard('{Escape}')
    await user.click(screen.getByRole('dialog').parentElement!)

    expect(onClose).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'Abbrechen' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Abmelden' })).toBeInTheDocument()
    useAuth.getState().logout()
  })
})
