import type { PickItemDto } from '../api/types'

/// <summary>
/// Gezählte Ist-Mengen des Mobile-Pickers und ihre Übergabe an die Pack-Seite.
///
/// Der Server kennt beim Picken keine Mengen (mark-picked hat keinen Body); die
/// Ist-Menge wird erst beim Packen (POST /picklists/{id}/pack) gebucht. Damit
/// die am Regal gezählte Menge nicht verloren geht, reicht der Mobile-Picker sie
/// beim Abschluss als Router-State an die Pack-Seite weiter, die sie als
/// Vorbelegung der Ist-Menge verwendet (statt der Soll-Menge). Gebucht wird
/// weiterhin erst, wenn der Packer dort bestätigt.
/// </summary>

/** PickItem-ID → gezählte Menge. Eine Position ohne Eintrag gilt als "wie geplant bestätigt". */
export type PickCounts = Record<string, number>

/** Router-State, mit dem der Mobile-Picker zur Pack-Seite navigiert. */
export interface PickedHandoverState {
  pickedQuantities: PickCounts
}

type ItemRef = Pick<PickItemDto, 'id' | 'quantity'>

/** Ist-Menge einer Position: die gezählte Menge, sonst gilt die Soll-Menge als bestätigt. */
export function actualQuantity(counts: PickCounts, item: ItemRef): number {
  return counts[item.id] ?? item.quantity
}

/**
 * Menge nach einem Scan (+1). Ein Scan zählt AB 0: noch nicht gezählte Positionen
 * ("gilt als Soll") beginnen beim ersten Scan bei 1 — wer scannt, zählt tatsächlich
 * nach und übernimmt nicht blind die Soll-Menge.
 */
export function nextScanCount(current: number | undefined): number {
  return (current ?? 0) + 1
}

/** Positionen, deren gezählte Menge vom Soll abweicht (mit der gezählten Menge). */
export function findDeviations<T extends ItemRef>(items: readonly T[], counts: PickCounts): { item: T; actual: number }[] {
  const result: { item: T; actual: number }[] = []
  for (const item of items) {
    const counted = counts[item.id]
    if (counted !== undefined && counted !== item.quantity) result.push({ item, actual: counted })
  }
  return result
}

/** Nur die Zählstände der Positionen dieser Pickliste (keine fremden IDs in den Router-State). */
export function pickCountsFor(items: readonly ItemRef[], counts: PickCounts): PickCounts {
  const result: PickCounts = {}
  for (const item of items) {
    const counted = counts[item.id]
    if (counted !== undefined) result[item.id] = counted
  }
  return result
}

/**
 * Liest die übergebenen Zählstände aus dem Router-State der Pack-Seite. Der State
 * kommt aus dem Browser-Verlauf und wird deshalb wie Fremdeingaben geprüft:
 * nur bekannte Positionen, nur ganze Zahlen >= 0, höchstens die Soll-Menge (der Server
 * lehnt beim Packen alles außerhalb von 0..Soll ab, eine höhere Vorbelegung würde nur scheitern).
 */
export function readPickedQuantities(state: unknown, items: readonly { id: string; quantity?: number }[]): PickCounts {
  if (typeof state !== 'object' || state === null) return {}
  const raw = (state as { pickedQuantities?: unknown }).pickedQuantities
  if (typeof raw !== 'object' || raw === null) return {}

  const planned = new Map(items.map((i) => [i.id, i.quantity]))
  const result: PickCounts = {}
  for (const [id, value] of Object.entries(raw)) {
    if (!planned.has(id) || typeof value !== 'number' || !Number.isInteger(value) || value < 0) continue
    const max = planned.get(id)
    result[id] = max === undefined ? value : Math.min(value, max)
  }
  return result
}
