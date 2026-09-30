import { useTranslation } from 'react-i18next'
import { ORDER_STATUS_LABEL, type OrderStatus } from '../../api/types'

/// <summary>
/// Farbige Statusanzeige einer Bestellung. Deckt den kompletten Lebenszyklus ab (Neu, In Kommissionierung,
/// Kommissioniert, Gepackt, Versendet, Storniert); ein unbekannter Status des Servers erscheint unverändert, statt die
/// Anzeige zu brechen. Die Farbe steht nur als Rand und Hintergrundton, der Text nimmt die Themenfarbe (hell/dunkel).
/// </summary>
const COLORS: Record<OrderStatus, string> = {
  New: '#64748b',
  Picking: '#2563eb',
  Picked: '#7c3aed',
  Packed: '#d97706',
  Shipped: '#16a34a',
  Cancelled: '#dc2626',
}

function isKnown(status: string): status is OrderStatus {
  return Object.hasOwn(ORDER_STATUS_LABEL, status)
}

export function OrderStatusPill({ status }: { status: string }) {
  const { t } = useTranslation()
  const known = isKnown(status)
  const color = known ? COLORS[status] : '#64748b'
  return (
    <span
      data-status={status}
      style={{
        display: 'inline-block',
        padding: '2px 8px',
        borderRadius: 999,
        fontSize: 12,
        fontWeight: 600,
        whiteSpace: 'nowrap',
        color: 'var(--c-text)',
        border: `1px solid ${color}`,
        background: `color-mix(in srgb, ${color} 16%, transparent)`,
      }}
    >
      {known ? t(`status:order.${status}`) : status}
    </span>
  )
}
