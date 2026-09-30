import type { LabelKind } from '../../api/labelHooks'
import { t } from '../../i18n'
import type { ArticleDto, OrderDto, WarehouseDto } from '../../api/types'

/// <summary>
/// Was auf einem Etikett steht und wie die Auswahl entsteht: aus Lagerplätzen (mit Filter nach Lager, Gang, Regal),
/// Artikeln und Bestellungen werden die Etiketten (LabelData) gebaut. Reine Funktionen ohne DOM.
/// </summary>

/** Ein Etikett: Art, Datensatz, der kodierte Code und die Zeilen darunter. */
export interface LabelData {
  /** Eindeutig je Art und Datensatz ("bin:<id>"): React-Schlüssel. */
  key: string
  kind: LabelKind
  /** Id des Datensatzes (für den ZPL-Endpunkt). */
  id: string
  /** Was der Barcode kodiert und was groß darunter steht: Lagerplatz-Code, SKU oder Bestellnummer. */
  code: string
  /** Zeile unter dem Code (Artikelname). */
  title?: string
  /** Kleingedrucktes (Regal, Kundenreferenz). */
  detail?: string
}

/** Natürliche Sortierung von Codes: "A-01-2" vor "A-01-10", Groß-/Kleinschreibung nachrangig, bei Gleichstand nach Zeichenfolge. */
export function compareCodes(a: string, b: string): number {
  return a.localeCompare(b, 'de', { numeric: true, sensitivity: 'base' }) || (a < b ? -1 : a > b ? 1 : 0)
}

/** Enthält einer der Texte die Suche (Groß-/Kleinschreibung und Leerraum am Rand egal)? Ohne Suche passt alles. */
export function matchesQuery(fields: readonly (string | null | undefined)[], query: string): boolean {
  const needle = query.trim().toLowerCase()
  if (needle === '') return true
  return fields.some((field) => field?.toLowerCase().includes(needle))
}

// ---- Lagerplätze ------------------------------------------------------------------------------------------------------

/** Ein Lagerplatz samt seiner Ebenen im Lager (für Filter und Kleingedrucktes). */
export interface BinRow {
  id: string
  code: string
  warehouseId: string
  warehouseCode: string
  aisleId: string
  aisleCode: string
  shelfId: string
  shelfCode: string
}

/** Alle Lagerplätze des Layouts (Lager → Zone → Gang → Regal → Platz), nach Code sortiert. */
export function flattenBins(layout: readonly WarehouseDto[]): BinRow[] {
  const rows: BinRow[] = []
  for (const warehouse of layout)
    for (const zone of warehouse.zones)
      for (const aisle of zone.aisles)
        for (const shelf of aisle.shelves)
          for (const bin of shelf.locations)
            rows.push({
              id: bin.id, code: bin.code,
              warehouseId: warehouse.id, warehouseCode: warehouse.code,
              aisleId: aisle.id, aisleCode: aisle.code,
              shelfId: shelf.id, shelfCode: shelf.code,
            })
  return rows.sort((a, b) => compareCodes(a.code, b.code))
}

/** Filter der Lagerplätze; leerer Text = alle. Ein Regal ist genauer als ein Gang, dieser genauer als ein Lager. */
export interface BinFilter {
  warehouseId: string
  aisleId: string
  shelfId: string
  /** Freitext über Lagerplatz-, Regal- und Gang-Code. */
  query: string
}

export const EMPTY_BIN_FILTER: BinFilter = { warehouseId: '', aisleId: '', shelfId: '', query: '' }

export function filterBins(rows: readonly BinRow[], filter: BinFilter): BinRow[] {
  return rows.filter((row) =>
    (filter.warehouseId === '' || row.warehouseId === filter.warehouseId)
    && (filter.aisleId === '' || row.aisleId === filter.aisleId)
    && (filter.shelfId === '' || row.shelfId === filter.shelfId)
    && matchesQuery([row.code, row.shelfCode, row.aisleCode], filter.query))
}

export const binLabel = (row: BinRow): LabelData => ({
  key: `bin:${row.id}`, kind: 'bin', id: row.id, code: row.code, detail: t('labels:label.shelf', { shelf: row.shelfCode }),
})

export const articleLabel = (article: ArticleDto): LabelData => ({
  key: `article:${article.id}`, kind: 'article', id: article.id, code: article.sku, title: article.name,
})

export const orderLabel = (order: OrderDto): LabelData => ({
  key: `order:${order.id}`, kind: 'order', id: order.id, code: order.orderNumber,
  detail: order.customerReference ? t('labels:label.customer', { ref: order.customerReference }) : undefined,
})

/** Die gewählten Etiketten: nur die mit ausgewählter Id, nach Code sortiert (Sammeldruck: "sortiert nach Bin-Code"). */
export function selectedLabels(all: readonly LabelData[], selected: ReadonlySet<string>): LabelData[] {
  return all.filter((label) => selected.has(label.id)).sort((a, b) => compareCodes(a.code, b.code))
}

// ---- Auswahl per Adresse ----------------------------------------------------------------------------------------------

const KINDS: readonly LabelKind[] = ['bin', 'article', 'order']

export function parseKind(value: string | null | undefined): LabelKind {
  return KINDS.find((kind) => kind === value) ?? 'bin'
}

/** Ids aus `?ids=a,b,c` (Verweis von anderen Seiten: "Etikett drucken"); Leerraum und Doppelte fallen weg. */
export function parseIds(value: string | null | undefined): Set<string> {
  return new Set((value ?? '').split(',').map((id) => id.trim()).filter((id) => id !== ''))
}
