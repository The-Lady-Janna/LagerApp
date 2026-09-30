import { useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { ArticleDto, OrderDto } from '../../api/types'
import { StatusPill } from '../../components/StatusPill'
import {
  EMPTY_BIN_FILTER, compareCodes, filterBins, matchesQuery,
  type BinFilter, type BinRow,
} from './labelSelection'

/** So viele Zeilen zeigt eine Liste höchstens; "Alle Treffer auswählen" gilt trotzdem für alle Treffer. */
const MAX_VISIBLE_ROWS = 200

interface Column<Row> {
  header: string
  render: (row: Row) => ReactNode
}

interface SelectionTableProps<Row extends { id: string }> {
  /** Alle Treffer (schon gefiltert und sortiert). */
  rows: readonly Row[]
  columns: readonly Column<Row>[]
  selected: ReadonlySet<string>
  onChange: (next: Set<string>) => void
  /** Bezeichnung einer Zeile für Screenreader ("Etikett für A-01-01"). */
  rowLabel: (row: Row) => string
  /** Text ohne Treffer. */
  emptyText: string
}

/**
 * Trefferliste mit Ankreuzfeldern: einzeln wählen, "Alle Treffer" in der Kopfzeile oder über die Schaltflächen. Die Auswahl
 * (Ids) gehört der Seite und bleibt beim Filtern erhalten; nur die Treffer werden aus- bzw. abgewählt.
 */
function SelectionTable<Row extends { id: string }>({ rows, columns, selected, onChange, rowLabel, emptyText }: SelectionTableProps<Row>) {
  const { t } = useTranslation()
  const selectedHits = rows.filter((row) => selected.has(row.id)).length
  const allSelected = rows.length > 0 && selectedHits === rows.length
  const visible = rows.slice(0, MAX_VISIBLE_ROWS)

  const setHits = (checked: boolean) => {
    const next = new Set(selected)
    for (const row of rows) {
      if (checked) next.add(row.id)
      else next.delete(row.id)
    }
    onChange(next)
  }
  const toggle = (id: string, checked: boolean) => {
    const next = new Set(selected)
    if (checked) next.add(id)
    else next.delete(id)
    onChange(next)
  }

  return (
    <>
      <div className="toolbar">
        <button type="button" onClick={() => setHits(true)} disabled={rows.length === 0 || allSelected}>
          {t('labels:picker.selectAll', { count: rows.length })}
        </button>
        <button type="button" onClick={() => setHits(false)} disabled={selectedHits === 0}>{t('labels:picker.deselect')}</button>
        <button type="button" onClick={() => onChange(new Set())} disabled={selected.size === 0}>{t('labels:picker.clear')}</button>
        <span className="muted" role="status">{t('labels:picker.status', { selected: selected.size, hits: rows.length })}</span>
      </div>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th scope="col" style={{ width: 32 }}>
                <input
                  type="checkbox"
                  aria-label={t('labels:picker.selectAllAria')}
                  checked={allSelected}
                  ref={(el) => { if (el) el.indeterminate = selectedHits > 0 && !allSelected }}
                  onChange={(e) => setHits(e.target.checked)}
                  disabled={rows.length === 0}
                />
              </th>
              {columns.map((column) => <th key={column.header} scope="col">{column.header}</th>)}
            </tr>
          </thead>
          <tbody>
            {visible.map((row) => (
              <tr key={row.id}>
                <td>
                  <input type="checkbox" aria-label={rowLabel(row)} checked={selected.has(row.id)} onChange={(e) => toggle(row.id, e.target.checked)} />
                </td>
                {columns.map((column) => <td key={column.header}>{column.render(row)}</td>)}
              </tr>
            ))}
            {rows.length === 0 && (
              <tr><td colSpan={columns.length + 1} className="muted">{emptyText}</td></tr>
            )}
          </tbody>
        </table>
      </div>
      {rows.length > visible.length && (
        <p className="muted">{t('labels:picker.more', { shown: visible.length, total: rows.length })}</p>
      )}
    </>
  )
}

interface PickerProps {
  selected: ReadonlySet<string>
  onChange: (next: Set<string>) => void
}

/**
 * Lagerplätze: Filter nach Lager, Gang und Regal plus Suche über die Codes, dazu die Trefferliste. Für den Sammeldruck
 * genügt es, ein Regal (oder einen Gang, ein Lager) zu wählen und "Alle Treffer auswählen" zu drücken; die Etiketten
 * erscheinen sortiert nach Lagerplatz-Code.
 */
export function BinPicker({ rows, selected, onChange }: PickerProps & { rows: readonly BinRow[] }) {
  const { t } = useTranslation()
  const [filter, setFilter] = useState<BinFilter>(EMPTY_BIN_FILTER)

  const warehouses = useMemo(() => uniqueOptions(rows, 'warehouseId', 'warehouseCode'), [rows])
  const aisles = useMemo(
    () => uniqueOptions(rows.filter((r) => filter.warehouseId === '' || r.warehouseId === filter.warehouseId), 'aisleId', 'aisleCode'),
    [rows, filter.warehouseId],
  )
  const shelves = useMemo(
    () => uniqueOptions(
      rows.filter((r) => (filter.warehouseId === '' || r.warehouseId === filter.warehouseId) && (filter.aisleId === '' || r.aisleId === filter.aisleId)),
      'shelfId', 'shelfCode',
    ),
    [rows, filter.warehouseId, filter.aisleId],
  )
  const hits = useMemo(() => filterBins(rows, filter), [rows, filter])

  return (
    <>
      <div className="toolbar">
        <label>
          {t('labels:picker.warehouse')}
          <select value={filter.warehouseId} onChange={(e) => setFilter({ ...filter, warehouseId: e.target.value, aisleId: '', shelfId: '' })}>
            <option value="">{t('labels:picker.allWarehouses')}</option>
            {warehouses.map((o) => <option key={o.id} value={o.id}>{o.code}</option>)}
          </select>
        </label>
        <label>
          {t('labels:picker.aisle')}
          <select value={filter.aisleId} onChange={(e) => setFilter({ ...filter, aisleId: e.target.value, shelfId: '' })}>
            <option value="">{t('labels:picker.allAisles')}</option>
            {aisles.map((o) => <option key={o.id} value={o.id}>{o.code}</option>)}
          </select>
        </label>
        <label>
          {t('labels:picker.shelf')}
          <select value={filter.shelfId} onChange={(e) => setFilter({ ...filter, shelfId: e.target.value })}>
            <option value="">{t('labels:picker.allShelves')}</option>
            {shelves.map((o) => <option key={o.id} value={o.id}>{o.code}</option>)}
          </select>
        </label>
        <label>
          {t('labels:picker.search')}
          <input type="search" value={filter.query} onChange={(e) => setFilter({ ...filter, query: e.target.value })} placeholder={t('labels:picker.binPlaceholder')} />
        </label>
      </div>
      <SelectionTable
        rows={hits}
        selected={selected}
        onChange={onChange}
        rowLabel={(row) => t('labels:picker.binRow', { code: row.code })}
        emptyText={rows.length === 0 ? t('labels:picker.binsNone') : t('labels:picker.binsFilter')}
        columns={[
          { header: t('labels:picker.colBin'), render: (row) => <code>{row.code}</code> },
          { header: t('labels:picker.shelf'), render: (row) => row.shelfCode },
          { header: t('labels:picker.aisle'), render: (row) => row.aisleCode },
          { header: t('labels:picker.warehouse'), render: (row) => row.warehouseCode },
        ]}
      />
    </>
  )
}

/** Artikel: Suche über SKU und Name, dazu die Trefferliste (sortiert nach SKU). */
export function ArticlePicker({ articles, selected, onChange }: PickerProps & { articles: readonly ArticleDto[] }) {
  const { t } = useTranslation()
  const [query, setQuery] = useState('')
  const hits = useMemo(
    () => articles.filter((a) => matchesQuery([a.sku, a.name, a.gtin], query)).sort((a, b) => compareCodes(a.sku, b.sku)),
    [articles, query],
  )
  return (
    <>
      <div className="toolbar">
        <label>
          {t('labels:picker.search')}
          <input type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('labels:picker.articlePlaceholder')} style={{ minWidth: 240 }} />
        </label>
      </div>
      <SelectionTable
        rows={hits}
        selected={selected}
        onChange={onChange}
        rowLabel={(row) => t('labels:picker.articleRow', { sku: row.sku })}
        emptyText={articles.length === 0 ? t('labels:picker.articlesNone') : t('labels:picker.articlesFilter')}
        columns={[
          { header: t('labels:picker.colSku'), render: (row) => <code>{row.sku}</code> },
          { header: t('labels:picker.colName'), render: (row) => row.name },
        ]}
      />
    </>
  )
}

/** Bestellungen: Suche über Nummer und Kundenreferenz, dazu die Trefferliste (sortiert nach Bestellnummer). */
export function OrderPicker({ orders, selected, onChange }: PickerProps & { orders: readonly OrderDto[] }) {
  const { t } = useTranslation()
  const [query, setQuery] = useState('')
  const hits = useMemo(
    () => orders.filter((o) => matchesQuery([o.orderNumber, o.customerReference, o.customerName], query)).sort((a, b) => compareCodes(a.orderNumber, b.orderNumber)),
    [orders, query],
  )
  return (
    <>
      <div className="toolbar">
        <label>
          {t('labels:picker.search')}
          <input type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder={t('labels:picker.orderPlaceholder')} style={{ minWidth: 240 }} />
        </label>
      </div>
      <SelectionTable
        rows={hits}
        selected={selected}
        onChange={onChange}
        rowLabel={(row) => t('labels:picker.orderRow', { number: row.orderNumber })}
        emptyText={orders.length === 0 ? t('labels:picker.ordersNone') : t('labels:picker.ordersFilter')}
        columns={[
          { header: t('labels:picker.colOrder'), render: (row) => <code>{row.orderNumber}</code> },
          { header: t('labels:picker.colCustomer'), render: (row) => row.customerName ?? row.customerReference ?? <span className="muted">—</span> },
          { header: t('labels:picker.colStatus'), render: (row) => <StatusPill status={row.status} domain="order" /> },
        ]}
      />
    </>
  )
}

/** Die verschiedenen (Id, Code)-Paare zweier Felder der Zeilen, nach Code sortiert — Optionen der Filterlisten. */
function uniqueOptions(rows: readonly BinRow[], idKey: 'warehouseId' | 'aisleId' | 'shelfId', codeKey: 'warehouseCode' | 'aisleCode' | 'shelfCode') {
  const byId = new Map<string, string>()
  for (const row of rows) byId.set(row[idKey], row[codeKey])
  return [...byId].map(([id, code]) => ({ id, code })).sort((a, b) => compareCodes(a.code, b.code))
}
