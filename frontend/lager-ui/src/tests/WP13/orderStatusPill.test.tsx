import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { OrderStatusPill } from '../../pages/orders/OrderStatusPill'
import { ORDER_STATUSES, ORDER_STATUS_LABEL } from '../../api/types'

// Bestellungen durchlaufen jetzt den ganzen Lebenszyklus (Neu -> ... -> Versendet, Storno): jeder Status braucht eine Anzeige.
describe('OrderStatusPill', () => {
  it.each(ORDER_STATUSES)('zeigt für %s einen deutschen Text und den Status als Datenattribut', (status) => {
    render(<OrderStatusPill status={status} />)

    const pill = screen.getByText(ORDER_STATUS_LABEL[status])
    expect(pill).toHaveAttribute('data-status', status)
  })

  it('kennt alle sechs Status mit verschiedenen Texten', () => {
    expect(ORDER_STATUSES).toEqual(['New', 'Picking', 'Picked', 'Packed', 'Shipped', 'Cancelled'])
    expect(new Set(Object.values(ORDER_STATUS_LABEL)).size).toBe(6)
    expect(ORDER_STATUS_LABEL.Cancelled).toBe('Storniert')
    expect(ORDER_STATUS_LABEL.Shipped).toBe('Versendet')
  })

  it('zeigt einen unbekannten Status des Servers unverändert an, statt zu brechen', () => {
    render(<OrderStatusPill status="OnHold" />)

    expect(screen.getByText('OnHold')).toBeInTheDocument()
  })
})
