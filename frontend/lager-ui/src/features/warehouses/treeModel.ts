import type { AisleDto, ShelfDto, WarehouseDto, ZoneDto } from '../../api/types'
import { t } from '../../i18n'

/// <summary>
/// Reine Hilfen der Lagerstruktur-Seite (ohne React): Zählungen für die Lösch-Rückfrage, Sortierung nach Code und die Vorschläge
/// für neue Codes. Getrennt von der Seite, damit sie sich ohne Rendern testen lassen.
/// </summary>

/** Was unter einem Knoten des Baums hängt. */
export interface SubtreeCounts {
  zones: number
  aisles: number
  shelves: number
  bins: number
}

const ZERO: SubtreeCounts = { zones: 0, aisles: 0, shelves: 0, bins: 0 }

function add(a: SubtreeCounts, b: SubtreeCounts): SubtreeCounts {
  return { zones: a.zones + b.zones, aisles: a.aisles + b.aisles, shelves: a.shelves + b.shelves, bins: a.bins + b.bins }
}

export function countInShelf(shelf: ShelfDto): SubtreeCounts {
  return { ...ZERO, bins: shelf.locations.length }
}

export function countInAisle(aisle: AisleDto): SubtreeCounts {
  return aisle.shelves.reduce((sum, shelf) => add(sum, add({ ...ZERO, shelves: 1 }, countInShelf(shelf))), ZERO)
}

export function countInZone(zone: ZoneDto): SubtreeCounts {
  return zone.aisles.reduce((sum, aisle) => add(sum, add({ ...ZERO, aisles: 1 }, countInAisle(aisle))), ZERO)
}

export function countInWarehouse(warehouse: WarehouseDto): SubtreeCounts {
  return warehouse.zones.reduce((sum, zone) => add(sum, add({ ...ZERO, zones: 1 }, countInZone(zone))), ZERO)
}

/** "2 Zonen, 3 Gänge, 8 Regale, 40 Lagerplätze" — nur was vorhanden ist; leer, wenn nichts darunter hängt. */
export function describeCounts(counts: SubtreeCounts): string {
  const parts: string[] = []
  if (counts.zones > 0) parts.push(t('warehouse:counts.zones', { count: counts.zones }))
  if (counts.aisles > 0) parts.push(t('warehouse:counts.aisles', { count: counts.aisles }))
  if (counts.shelves > 0) parts.push(t('warehouse:counts.shelves', { count: counts.shelves }))
  if (counts.bins > 0) parts.push(t('warehouse:counts.bins', { count: counts.bins }))
  return parts.join(', ')
}

/** Codes wie "A1-02" vor "A1-10": Zahlen im Code werden als Zahl verglichen, Groß-/Kleinschreibung ignoriert. */
export function compareCodes(a: string, b: string): number {
  return a.localeCompare(b, 'de', { numeric: true, sensitivity: 'base' })
}

export function sortByCode<T extends { code: string }>(items: readonly T[]): T[] {
  return [...items].sort((a, b) => compareCodes(a.code, b.code))
}

/**
 * Vorschlag für den nächsten Lagerplatz-Code eines Regals: "<Regalcode>-NN" hinter der höchsten laufenden Nummer der Lagerplätze
 * dieses Regals. Lagerplatz-Codes sind im ganzen System eindeutig (ohne Groß-/Kleinschreibung) - ist der Vorschlag schon vergeben
 * (z. B. weil ein anderes Regal denselben Code trägt), zählt die Nummer weiter, bis er frei ist.
 */
export function nextFreeBinCode(shelfCode: string, shelfBinCodes: readonly string[], allBinCodes: Iterable<string>): string {
  const taken = new Set<string>()
  for (const code of allBinCodes) taken.add(code.trim().toLowerCase())
  let next = 1
  for (const code of shelfBinCodes) {
    const match = /-(\d+)$/.exec(code)
    if (match) next = Math.max(next, Number(match[1]) + 1)
  }
  const format = (n: number) => `${shelfCode}-${String(n).padStart(2, '0')}`
  while (taken.has(format(next).toLowerCase())) next += 1
  return format(next)
}

/** Nächster freier Code "<Präfix><Nummer>" unter Geschwistern (z. B. Z1, Z2 ...; ohne Groß-/Kleinschreibung). */
export function nextFreeCode(prefix: string, siblingCodes: readonly string[]): string {
  const taken = new Set(siblingCodes.map((c) => c.trim().toLowerCase()))
  let n = 1
  while (taken.has(`${prefix}${n}`.toLowerCase())) n += 1
  return `${prefix}${n}`
}

/** Alle Lagerplatz-Codes der Lager (für die Eindeutigkeit über Regale hinweg). */
export function allBinCodes(warehouses: readonly WarehouseDto[]): string[] {
  return warehouses.flatMap((w) => w.zones).flatMap((z) => z.aisles).flatMap((a) => a.shelves).flatMap((s) => s.locations).map((b) => b.code)
}
