/// <summary>
/// Zentrale Zuordnung Status → Farbton für alle Status-Pills. Vorher hatte jede Seite ihre eigene Farbtabelle
/// (Wareneingang, Bestellungen an Lieferanten, Versand, Wellen, Retouren-QC) mit festen Hex-Werten; jetzt gibt es
/// eine Tabelle, und die Farbe je Ton steht als Token in index.css (.pill--<ton>, hell/dunkel mit Kontrast ≥ 4,5:1).
/// </summary>
export type PillTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger' | 'accent' | 'special'

/** Bereiche, für die es eine Zuordnung gibt. */
export type StatusDomain = 'inbound' | 'purchaseOrder' | 'shipment' | 'wave' | 'qc' | 'order'

export const STATUS_TONES: Readonly<Record<StatusDomain, Readonly<Record<string, PillTone>>>> = {
  inbound: { Draft: 'neutral', Received: 'success', Cancelled: 'danger' },
  purchaseOrder: { Draft: 'neutral', Sent: 'info', PartiallyReceived: 'warning', Received: 'success', Cancelled: 'danger' },
  shipment: { Ready: 'neutral', Labeled: 'info', Shipped: 'warning', Delivered: 'success', Cancelled: 'danger' },
  wave: { Open: 'neutral', Released: 'info', Completed: 'success', Cancelled: 'danger' },
  qc: { Pending: 'neutral', Sellable: 'success', BGrade: 'warning', Defect: 'danger', Destroy: 'special' },
  order: { New: 'neutral', Picking: 'info', Picked: 'accent', Packed: 'warning', Shipped: 'success', Cancelled: 'danger' },
}

/** Ton eines Status; ein unbekannter Status (z. B. neu vom Server) wird neutral dargestellt statt die Anzeige zu brechen. */
export function toneFor(domain: StatusDomain, status: string): PillTone {
  const table = STATUS_TONES[domain]
  return Object.hasOwn(table, status) ? table[status] : 'neutral'
}
