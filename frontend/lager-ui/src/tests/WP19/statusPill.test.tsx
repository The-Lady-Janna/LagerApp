import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { StatusPill } from '../../components/StatusPill'
import { StatTile } from '../../components/StatTile'
import { ORDER_STATUSES } from '../../api/types'
import { STATUS_TONES, toneFor, type StatusDomain } from '../../lib/statusTones'

describe('StatusPill: zentrale Zuordnung Status → Farbton', () => {
  it.each<[StatusDomain, string, string]>([
    ['inbound', 'Draft', 'neutral'],
    ['inbound', 'Received', 'success'],
    ['inbound', 'Cancelled', 'danger'],
    ['purchaseOrder', 'Sent', 'info'],
    ['purchaseOrder', 'PartiallyReceived', 'warning'],
    ['shipment', 'Labeled', 'info'],
    ['shipment', 'Shipped', 'warning'],
    ['shipment', 'Delivered', 'success'],
    ['wave', 'Released', 'info'],
    ['wave', 'Completed', 'success'],
    ['qc', 'Sellable', 'success'],
    ['qc', 'BGrade', 'warning'],
    ['qc', 'Defect', 'danger'],
    ['qc', 'Destroy', 'special'],
    ['order', 'Picked', 'accent'],
  ])('%s/%s → %s', (domain, status, tone) => {
    render(<StatusPill domain={domain} status={status} />)

    const pill = screen.getByText(status)
    expect(pill).toHaveClass('pill', `pill--${tone}`)
    expect(pill).toHaveAttribute('data-status', status)
    expect(pill).toHaveAttribute('data-tone', tone)
  })

  it('zeigt einen unbekannten Status neutral und unverändert an, statt zu brechen (neuer Serverstatus)', () => {
    render(<StatusPill domain="shipment" status="OnHold" />)

    expect(screen.getByText('OnHold')).toHaveClass('pill--neutral')
    expect(toneFor('wave', 'toString')).toBe('neutral')   // auch Objekt-Eigenschaften sind kein Status
  })

  it('lässt Ton und Anzeigetext überschreiben (Label für Übersetzung, Ton für Klassen wie ABC)', () => {
    render(<StatusPill status="A" tone="success" label="Klasse A" small />)

    const pill = screen.getByText('Klasse A')
    expect(pill).toHaveClass('pill--success')
    expect(pill).toHaveAttribute('data-status', 'A')
  })

  it('kennt für Bestellungen genau die Status des Lebenszyklus (Abgleich mit api/types)', () => {
    expect(Object.keys(STATUS_TONES.order).sort()).toEqual([...ORDER_STATUSES].sort())
  })
})

describe('StatTile', () => {
  it('zeigt Label und Wert; der Ton färbt nur den Wert, der Tooltip erklärt die Zahl', () => {
    const { container } = render(<StatTile label="Ohne Stock" value={3} tone="danger" tooltip="Erklärung" />)

    expect(screen.getByText('Ohne Stock')).toHaveClass('stat-tile-label')
    expect(screen.getByText('3')).toHaveClass('stat-tile-value')
    expect(container.firstElementChild).toHaveClass('stat-tile', 'stat-tile--danger')
    expect(container.firstElementChild).toHaveAttribute('title', 'Erklärung')
  })
})
