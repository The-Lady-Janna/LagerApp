import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfirmDialog } from '../../components/ConfirmDialog'

describe('ConfirmDialog', () => {
  const renderDialog = (props: Partial<Parameters<typeof ConfirmDialog>[0]> = {}) => {
    const onConfirm = vi.fn()
    const onCancel = vi.fn()
    const utils = render(
      <ConfirmDialog open title="Retoure verarbeiten?" confirmLabel="Verarbeiten" onConfirm={onConfirm} onCancel={onCancel} {...props}>
        <p>Der Bestand wird gebucht.</p>
      </ConfirmDialog>,
    )
    return { onConfirm, onCancel, ...utils }
  }

  it('ist ein modaler Dialog mit Titel und Beschreibung und setzt den Fokus auf die primäre Aktion', () => {
    renderDialog()

    const dialog = screen.getByRole('dialog', { name: 'Retoure verarbeiten?' })
    expect(dialog).toHaveAttribute('aria-modal', 'true')
    expect(dialog).toHaveAccessibleDescription('Der Bestand wird gebucht.')
    expect(screen.getByRole('button', { name: 'Verarbeiten' })).toHaveFocus()
  })

  it('rendert nichts, solange er geschlossen ist', () => {
    renderDialog({ open: false })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('Escape und "Abbrechen" schließen, ohne zu bestätigen', async () => {
    const user = userEvent.setup()
    const { onConfirm, onCancel } = renderDialog()

    await user.keyboard('{Escape}')
    expect(onCancel).toHaveBeenCalledTimes(1)

    await user.click(screen.getByRole('button', { name: 'Abbrechen' }))
    expect(onCancel).toHaveBeenCalledTimes(2)
    expect(onConfirm).not.toHaveBeenCalled()
  })

  it('"Bestätigen" löst onConfirm aus; ein Klick auf den Hintergrund verwirft den Dialog NICHT', async () => {
    const user = userEvent.setup()
    const { onConfirm, onCancel } = renderDialog()

    await user.click(screen.getByRole('dialog').parentElement as HTMLElement)
    expect(onCancel).not.toHaveBeenCalled()

    await user.click(screen.getByRole('button', { name: 'Verarbeiten' }))
    expect(onConfirm).toHaveBeenCalledTimes(1)
  })

  it('Doppelklick auf "Bestätigen" bucht nur einmal, auch wenn der Dialog offen bleibt', async () => {
    const user = userEvent.setup()
    const { onConfirm } = renderDialog()

    await user.dblClick(screen.getByRole('button', { name: 'Verarbeiten' }))

    expect(onConfirm).toHaveBeenCalledTimes(1)
  })

  it('sperrt Bestätigen, Abbrechen und Escape, solange die Aktion läuft (pending)', async () => {
    const user = userEvent.setup()
    const { onConfirm, onCancel } = renderDialog({ pending: true })

    expect(screen.getByRole('button', { name: 'Verarbeiten' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Abbrechen' })).toBeDisabled()
    await user.keyboard('{Escape}')
    expect(onCancel).not.toHaveBeenCalled()
    expect(onConfirm).not.toHaveBeenCalled()
  })

  it('erlaubt nach einem beendeten pending erneut zu bestätigen (Fehler → Wiederholen)', async () => {
    const user = userEvent.setup()
    const onConfirm = vi.fn()
    const onCancel = vi.fn()
    const { rerender } = render(<ConfirmDialog open title="T" confirmLabel="Ja" pending={false} onConfirm={onConfirm} onCancel={onCancel} />)

    await user.click(screen.getByRole('button', { name: 'Ja' }))
    rerender(<ConfirmDialog open title="T" confirmLabel="Ja" pending onConfirm={onConfirm} onCancel={onCancel} />)
    rerender(<ConfirmDialog open title="T" confirmLabel="Ja" pending={false} onConfirm={onConfirm} onCancel={onCancel} />)
    await user.click(screen.getByRole('button', { name: 'Ja' }))

    expect(onConfirm).toHaveBeenCalledTimes(2)
  })

  it('gibt den Fokus beim Schließen an das auslösende Element zurück', async () => {
    const user = userEvent.setup()
    function Host() {
      const [open, setOpen] = useState(false)
      return (
        <>
          <button onClick={() => setOpen(true)}>Öffnen</button>
          <ConfirmDialog open={open} title="Sicher?" onConfirm={() => setOpen(false)} onCancel={() => setOpen(false)} />
        </>
      )
    }
    render(<Host />)

    await user.click(screen.getByRole('button', { name: 'Öffnen' }))
    expect(screen.getByRole('button', { name: 'Bestätigen' })).toHaveFocus()

    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Öffnen' })).toHaveFocus()
  })

  it('Tab wandert nur durch den Dialog (Fokus-Falle)', async () => {
    const user = userEvent.setup()
    render(
      <>
        <button>Dahinter</button>
        <ConfirmDialog open title="T" confirmLabel="Ja" onConfirm={() => {}} onCancel={() => {}} />
      </>,
    )

    expect(screen.getByRole('button', { name: 'Ja' })).toHaveFocus()
    await user.tab()
    expect(screen.getByRole('button', { name: 'Abbrechen' })).toHaveFocus()
    await user.tab()
    expect(screen.getByRole('button', { name: 'Ja' })).toHaveFocus() // zurück an den Anfang, nicht zu "Dahinter"
    await user.tab({ shift: true })
    expect(screen.getByRole('button', { name: 'Abbrechen' })).toHaveFocus()
  })
})
