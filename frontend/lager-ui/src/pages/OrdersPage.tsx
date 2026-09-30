import { useMemo, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router-dom'
import {
  useArticles, useCancelOrder, useCartConfigs, useCreateManualOrder, useCustomers, useGenerateCartPickList, useOrders,
} from '../api/hooks'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { StatTile } from '../components/StatTile'
import {
  CANCELLABLE_ORDER_STATUSES, ORDER_PRIORITIES, ORDER_STATUSES,
  type OrderDto, type OrderStatus,
} from '../api/types'
import { formatDateOnly, formatDateTime, parseServerDate } from '../lib/format'
import { useAuth } from '../state/auth'
import { OrderStatusPill } from './orders/OrderStatusPill'

interface LineDraft { articleId: string; quantity: number }

/** Reihenfolge der Gruppen in der Liste: neue Bestellungen mit Bestand zuerst, dann ohne Bestand, dann der Rest im Lebenszyklus. */
function groupRank(o: OrderDto): number {
  if (o.status === 'New') return o.hasStockAfterFifo ? 0 : 1
  const index = ORDER_STATUSES.indexOf(o.status as OrderStatus)
  return index < 0 ? ORDER_STATUSES.length + 2 : index + 1   // Picking = 2 ... Cancelled = 6
}

/** Innerhalb einer Gruppe wie beim Kommissionieren: Priorität absteigend, Fälligkeit aufsteigend, dann Eingang. */
function comparePickOrder(a: OrderDto, b: OrderDto): number {
  const byPriority = (b.priority ?? 0) - (a.priority ?? 0)
  if (byPriority !== 0) return byPriority
  const dueA = parseServerDate(a.dueDate)?.getTime() ?? Number.POSITIVE_INFINITY
  const dueB = parseServerDate(b.dueDate)?.getTime() ?? Number.POSITIVE_INFINITY
  if (dueA !== dueB) return dueA < dueB ? -1 : 1
  return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()
}

/** Ein Kalendertag (Eingabefeld "2026-10-05") als Fälligkeit: Mitternacht UTC, damit der Tag in jeder Zeitzone gleich bleibt. */
function dueDateToServer(day: string): string | null {
  return /^\d{4}-\d{2}-\d{2}$/.test(day) ? `${day}T00:00:00.000Z` : null
}

/** Die Fälligkeit zählt als Kalendertag (die Spalte zeigt nur das Datum, UTC): erst der Tag danach ist überfällig, der Fälligkeitstag selbst nicht. */
function isOverdue(o: OrderDto): boolean {
  if (!CANCELLABLE_ORDER_STATUSES.includes(o.status)) return false
  const due = parseServerDate(o.dueDate)
  return due !== null && due.toISOString().slice(0, 10) < new Date().toISOString().slice(0, 10)
}

export function OrdersPage() {
  const { t } = useTranslation()
  const orders = useOrders()
  const articles = useArticles()
  const customers = useCustomers()
  const createMut = useCreateManualOrder()
  const cancelMut = useCancelOrder()
  const canCancel = useAuth((s) => s.hasRole('Manager'))
  const cartConfigs = useCartConfigs()
  const generateCart = useGenerateCartPickList()
  const navigate = useNavigate()
  const [chosenCartId, setChosenCartId] = useState<string>('')
  const [optimizeBinReuse, setOptimizeBinReuse] = useState(false)
  const [statusFilter, setStatusFilter] = useState<'all' | OrderStatus>('all')

  const [orderNumber, setOrderNumber] = useState('')
  const [customerRef, setCustomerRef] = useState('')
  const [customerId, setCustomerId] = useState('')
  const [addressId, setAddressId] = useState('')
  const [priority, setPriority] = useState(0)
  const [dueDay, setDueDay] = useState('')
  const [lines, setLines] = useState<LineDraft[]>([{ articleId: '', quantity: 1 }])

  const { total, openCount, withStockNow, withoutStockNow, droppedByFifo, ineligible, sorted } = useMemo(() => {
    const all: OrderDto[] = orders.data ?? []
    const openOrders = all.filter((o) => o.status === 'New')

    const withStockNow = openOrders.filter((o) => o.hasStockNow).length
    const withStockAfterFifo = openOrders.filter((o) => o.hasStockAfterFifo).length
    const openCount = openOrders.length

    const shown = statusFilter === 'all' ? all : all.filter((o) => o.status === statusFilter)
    const sorted = [...shown].sort((a, b) => groupRank(a) - groupRank(b) || comparePickOrder(a, b))

    return {
      total: all.length,
      openCount,
      withStockNow,
      withoutStockNow: openCount - withStockNow,
      droppedByFifo: withStockNow - withStockAfterFifo,
      ineligible: openCount - withStockAfterFifo, // total ohne Stock nach FIFO
      sorted,
    }
  }, [orders.data, statusFilter])

  const customerList = customers.data ?? []
  const chosenCustomer = customerList.find((c) => c.id === customerId)
  const shippingAddresses = (chosenCustomer?.addresses ?? []).filter((a) => a.kind === 'Shipping' || a.kind === 'Both')

  const chooseCustomer = (id: string) => {
    setCustomerId(id)
    // Beim Kundenwechsel die (einzige) Lieferadresse vorbelegen, sonst keine.
    const options = (customerList.find((c) => c.id === id)?.addresses ?? []).filter((a) => a.kind === 'Shipping' || a.kind === 'Both')
    setAddressId(options.length === 1 ? options[0].id : '')
  }

  const fillCart = async () => {
    if (!chosenCartId) return
    try {
      const pl = await generateCart.mutateAsync({
        pickCartConfigId: chosenCartId,
        optimizeForBinReuse: optimizeBinReuse,
      })
      navigate(`/picklists/${pl.id}`)
    } catch {
      /* error in card */
    }
  }

  const askCancel = (o: OrderDto) => {
    const consequence = o.status === 'New' ? t('orders:cancel.consequenceNew') : t('orders:cancel.consequenceOther')
    if (!window.confirm(t('orders:cancel.confirm', { number: o.orderNumber, consequence }))) return
    cancelMut.mutate(o.id)
  }

  const addLine = () => setLines((l) => [...l, { articleId: '', quantity: 1 }])
  const removeLine = (idx: number) => setLines((l) => l.filter((_, i) => i !== idx))
  const updateLine = (idx: number, patch: Partial<LineDraft>) =>
    setLines((l) => l.map((item, i) => (i === idx ? { ...item, ...patch } : item)))

  const submit = async () => {
    const validLines = lines.filter((l) => l.articleId && l.quantity > 0)
    if (!orderNumber || validLines.length === 0) return
    try {
      await createMut.mutateAsync({
        orderNumber,
        customerReference: customerRef || null,
        lines: validLines,
        customerId: customerId || null,
        shippingAddressId: customerId && addressId ? addressId : null,
        priority,
        dueDate: dueDateToServer(dueDay),
      })
    } catch {
      return // Fehler steht unter dem Formular, die Eingaben bleiben erhalten
    }
    setOrderNumber('')
    setCustomerRef('')
    setCustomerId('')
    setAddressId('')
    setPriority(0)
    setDueDay('')
    setLines([{ articleId: '', quantity: 1 }])
  }

  return (
    <>
      <h2>{t('orders:title')}</h2>

      {/* Dashboard — immer offen, da Schnellüberblick */}
      <div className="card card--info">
        <h3 style={{ margin: '0 0 8px' }}>{t('orders:overview')}</h3>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(min(140px, 100%), 1fr))', gap: 12 }}>
          <StatTile label={t('orders:tile.total')} value={total} />
          <StatTile label={t('orders:tile.open')} value={openCount} />
          <StatTile label={t('orders:tile.withStock')} value={withStockNow} tone="success" />
          <StatTile label={t('orders:tile.withoutStock')} value={withoutStockNow} tone="danger" />
          <StatTile label={t('orders:tile.fifo')} value={droppedByFifo} tone="warning"
            tooltip={t('orders:tile.fifoTip')} />
          <StatTile label={t('orders:tile.ineligible')} value={ineligible} tone="danger" />
        </div>
      </div>

      <CollapsibleCard title={t('orders:cart.title')} storageKey="orders-cart" defaultOpen>
        <p className="muted">
          {t('orders:cart.hint', { count: openCount })}
        </p>
        <div className="row">
          <label style={{ flex: 1 }}>
            {t('orders:cart.config')}
            <select value={chosenCartId} onChange={(e) => setChosenCartId(e.target.value)}>
              <option value="">{t('orders:choose')}</option>
              {(cartConfigs.data ?? []).map((c) => (
                <option key={c.id} value={c.id}>
                  {t('orders:cart.option', { name: c.name, levels: c.levelCount, volume: (c.totalVolumeMm3 / 1_000_000_000).toFixed(2), weight: (c.maxWeightGrams / 1000).toFixed(0) })}
                </option>
              ))}
            </select>
          </label>
          <button
            className="primary"
            onClick={fillCart}
            disabled={!chosenCartId || openCount === 0 || generateCart.isPending}
          >
            {generateCart.isPending ? t('orders:cart.generating') : t('orders:cart.fill')}
          </button>
        </div>
        <label className="row" style={{ marginTop: 8, gap: 6, cursor: 'pointer' }}>
          <input
            type="checkbox"
            checked={optimizeBinReuse}
            onChange={(e) => setOptimizeBinReuse(e.target.checked)}
            style={{ width: 'auto' }}
          />
          <span>
            <strong>{t('orders:cart.optimize')}</strong>
            <span className="muted">{t('orders:cart.optimizeHint')}</span>
          </span>
        </label>
        {(cartConfigs.data ?? []).length === 0 && (
          <p className="muted" style={{ marginTop: 8 }}>
            <Trans i18nKey="orders:cart.none" components={{ a: <Link to="/cart-configs" /> }} />
          </p>
        )}
        <ErrorBanner error={generateCart.error} title={t('orders:cart.failed')} onDismiss={generateCart.reset} style={{ marginTop: 8 }} />
      </CollapsibleCard>

      <CollapsibleCard title={t('orders:manual.title')} storageKey="orders-new" defaultOpen={false}>
        <div className="grid-2">
          <label>{t('orders:manual.number')}<input value={orderNumber} onChange={(e) => setOrderNumber(e.target.value)} /></label>
          <label>{t('orders:manual.customerRef')}<input value={customerRef} onChange={(e) => setCustomerRef(e.target.value)} /></label>
          <label>{t('orders:manual.customer')}
            <select value={customerId} onChange={(e) => chooseCustomer(e.target.value)}>
              <option value="">{t('orders:manual.noCustomer')}</option>
              {customerList.map((c) => <option key={c.id} value={c.id}>{c.code} – {c.name}</option>)}
            </select>
          </label>
          <label>{t('orders:manual.address')}
            <select value={addressId} onChange={(e) => setAddressId(e.target.value)} disabled={!customerId}>
              <option value="">{customerId ? t('orders:manual.noAddress') : t('orders:manual.chooseCustomerFirst')}</option>
              {shippingAddresses.map((a) => (
                <option key={a.id} value={a.id}>{a.label}: {a.street}, {a.zip} {a.city}</option>
              ))}
            </select>
          </label>
          <label>{t('orders:manual.priority')}
            <select value={priority} onChange={(e) => setPriority(+e.target.value)}>
              {ORDER_PRIORITIES.map((value) => <option key={value} value={value}>{t(`orders:priority.${value}`)}</option>)}
            </select>
          </label>
          <label>{t('orders:manual.due')}
            <input type="date" value={dueDay} onChange={(e) => setDueDay(e.target.value)} />
          </label>
        </div>
        <h4>{t('orders:manual.lines')}</h4>
        {lines.map((l, idx) => (
          <div key={idx} className="row" style={{ marginBottom: 8 }}>
            <select
              aria-label={t('orders:manual.articleAria', { n: idx + 1 })}
              value={l.articleId}
              onChange={(e) => updateLine(idx, { articleId: e.target.value })}
              style={{ flex: 1, minWidth: 0 }}
            >
              <option value="">{t('orders:manual.article')}</option>
              {articles.data?.map((a) => <option key={a.id} value={a.id}>{a.sku} – {a.name}</option>)}
            </select>
            <input
              type="number" min={1} max={100000}
              aria-label={t('orders:manual.quantityAria', { n: idx + 1 })}
              value={l.quantity}
              onChange={(e) => updateLine(idx, { quantity: +e.target.value })}
              style={{ width: 80 }}
            />
            <button type="button" onClick={() => removeLine(idx)} disabled={lines.length === 1} aria-label={t('orders:manual.removeAria', { n: idx + 1 })}>×</button>
          </div>
        ))}
        <div className="toolbar">
          <button onClick={addLine}>{t('orders:manual.addLine')}</button>
          <button className="primary" onClick={submit} disabled={createMut.isPending}>
            {t('orders:manual.create')}
          </button>
        </div>
        <ErrorBanner error={createMut.error} title={t('orders:manual.failed')} onDismiss={createMut.reset} />
      </CollapsibleCard>

      <CollapsibleCard
        title={statusFilter === 'all' ? t('orders:list.title', { count: sorted.length }) : t('orders:list.titleFiltered', { count: sorted.length, total })}
        storageKey="orders-list"
        defaultOpen
        headerRight={<span className="muted" style={{ fontSize: 11 }}>{t('orders:list.sortHint')}</span>}
      >
        <div className="toolbar">
          <label style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
            {t('orders:col.status')}
            <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value as 'all' | OrderStatus)}>
              <option value="all">{t('orders:list.all')}</option>
              {ORDER_STATUSES.map((s) => <option key={s} value={s}>{t(`status:order.${s}`)}</option>)}
            </select>
          </label>
        </div>
        <ErrorBanner error={cancelMut.error} title={t('orders:cancel.failed')} fallback={t('orders:cancel.failedFallback')} onDismiss={cancelMut.reset} />
        <LoadState isLoading={orders.isLoading} error={orders.error} hasData={orders.data !== undefined} what={t('orders:what')} onRetry={() => void orders.refetch()}>
        <table>
          <caption className="visually-hidden">{t('orders:list.caption')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('orders:col.number')}</th>
              <th scope="col">{t('orders:col.status')}</th>
              <th scope="col">{t('orders:col.priority')}</th>
              <th scope="col">{t('orders:col.due')}</th>
              <th scope="col">{t('orders:col.customer')}</th>
              <th scope="col">{t('orders:col.source')}</th>
              <th scope="col">{t('orders:col.lines')}</th>
              <th scope="col">{t('orders:col.created')}</th>
              <th scope="col">{t('orders:col.stock')}</th>
              <th scope="col"><span className="visually-hidden">{t('orders:col.actions')}</span></th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((o) => (
              <tr key={o.id} style={{ opacity: o.status === 'Cancelled' || (o.status === 'New' && !o.hasStockAfterFifo) ? 0.55 : 1 }}>
                <td><code>{o.orderNumber}</code></td>
                <td><OrderStatusPill status={o.status} /></td>
                <td>{(o.priority ?? 0) > 0 ? t(`orders:priority.${o.priority ?? 0}`, { defaultValue: String(o.priority) }) : <span className="muted">—</span>}</td>
                <td className={isOverdue(o) ? 'text-danger' : undefined} style={isOverdue(o) ? { fontWeight: 600 } : undefined}>
                  {formatDateOnly(o.dueDate)}{isOverdue(o) ? t('orders:overdue') : ''}
                </td>
                <td>{o.customerName ?? o.customerReference ?? <span className="muted">—</span>}</td>
                <td>{o.source}</td>
                <td>{o.lines.length}</td>
                <td>{formatDateTime(o.createdAt)}</td>
                <td><StockBadge order={o} /></td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <Link to={`/orders/${o.id}`}>{t('orders:open')}</Link>
                  {canCancel && CANCELLABLE_ORDER_STATUSES.includes(o.status) && (
                    <button
                      className="danger"
                      style={{ marginLeft: 8 }}
                      disabled={cancelMut.isPending}
                      aria-label={t('orders:cancel.aria', { number: o.orderNumber })}
                      onClick={() => askCancel(o)}
                    >
                      {t('orders:cancel.button')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
            {sorted.length === 0 && <tr><td colSpan={10} className="muted">{t('orders:list.empty')}</td></tr>}
          </tbody>
        </table>
        </LoadState>
      </CollapsibleCard>
    </>
  )
}

function StockBadge({ order }: { order: OrderDto }) {
  const { t } = useTranslation()
  if (order.status !== 'New') return <span className="muted">—</span>
  if (!order.hasStockNow) return <span className="text-danger" style={{ fontWeight: 600 }}>{t('orders:stock.none')}</span>
  if (!order.hasStockAfterFifo) return <span className="text-warning" style={{ fontWeight: 600 }}>{t('orders:stock.fifoConflict')}</span>
  return <span className="text-success">{t('orders:stock.ok')}</span>
}
