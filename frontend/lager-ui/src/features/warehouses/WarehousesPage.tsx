import { useMemo, useState, type ReactNode } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useDeleteBin, useWarehouseLayout } from '../../api/hooks'
import type { AisleDto, ShelfDto, StorageLocationDto, WarehouseDto, ZoneDto } from '../../api/types'
import {
  binTypeOf,
  thresholdOf,
  useDeleteAisle,
  useDeleteShelf,
  useDeleteWarehouse,
  useDeleteZone,
  type BinType,
} from '../../api/warehouseAdminHooks'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { ErrorBanner } from '../../components/ErrorBanner'
import { LoadState } from '../../components/LoadState'
import { NewShelfDialog } from '../../components/NewShelfDialog'
import { StatusPill } from '../../components/StatusPill'
import { useFlashMessage } from '../../components/layoutEditor/useFlashMessage'
import type { PillTone } from '../../lib/statusTones'
import { AisleDialog, BinDialog, BinTypeDialog, ShelfEditDialog, WarehouseDialog, ZoneDialog } from './StructureDialogs'
import {
  allBinCodes,
  countInAisle,
  countInShelf,
  countInWarehouse,
  countInZone,
  describeCounts,
  sortByCode,
  type SubtreeCounts,
} from './treeModel'
import './warehouses.css'

/** Welcher Dialog gerade offen ist (höchstens einer). */
type DialogState =
  | { kind: 'warehouse'; existing?: WarehouseDto }
  | { kind: 'zone'; warehouse: WarehouseDto; existing?: ZoneDto }
  | { kind: 'aisle'; zone: ZoneDto; existing?: AisleDto }
  | { kind: 'newShelf'; aisleId: string }
  | { kind: 'shelf'; shelf: ShelfDto }
  | { kind: 'bin'; shelf: ShelfDto; existing?: StorageLocationDto }
  | { kind: 'binType'; bin: StorageLocationDto }

type DeleteKind = 'warehouse' | 'zone' | 'aisle' | 'shelf' | 'bin'

/** Was der Bestätigungsdialog löschen soll, samt dem, was darunter mitgelöscht wird. */
interface DeleteTarget {
  kind: DeleteKind
  id: string
  /** Mit Art und Code, z. B. `Regal "S-01"` (Sprache der Oberfläche). */
  label: string
  counts: SubtreeCounts
}

/** Alles, was die Knoten zum Aufklappen, Bearbeiten und Löschen brauchen. */
interface TreeContext {
  isOpen: (id: string, defaultOpen: boolean) => boolean
  toggle: (id: string) => void
  openDialog: (dialog: DialogState) => void
  askDelete: (target: DeleteTarget) => void
  allBinCodes: readonly string[]
}

const BIN_TONES: Readonly<Record<BinType, PillTone>> = { Standard: 'neutral', HotPick: 'warning', Reserve: 'info' }

/// <summary>
/// Lagerstruktur pflegen: Baumansicht Lager > Zone > Gang > Regal > Lagerplatz mit Anlegen, Bearbeiten und Löschen (ConfirmDialog),
/// dazu Bin-Typ und Nachschub-Schwelle je Lagerplatz. Ohne Lager erscheint der Leerzustand "Noch kein Lager - jetzt anlegen":
/// so lässt sich ein Lager auch ohne Demodaten aufbauen. Der Server prüft alles (Codes je Elternteil eindeutig, Löschen nur
/// ohne Bestand/offene Aufgaben) und meldet Verstöße als Fehler im jeweiligen Dialog.
/// </summary>
export function WarehousesPage() {
  const { t } = useTranslation()
  const layout = useWarehouseLayout()
  const deleteWarehouse = useDeleteWarehouse()
  const deleteZone = useDeleteZone()
  const deleteAisle = useDeleteAisle()
  const deleteShelf = useDeleteShelf()
  const deleteBin = useDeleteBin()
  const { message, flash } = useFlashMessage()

  // Aufgeklappt sind Lager, Zonen und Gänge von Anfang an, Regale (mit vielen Lagerplätzen) zugeklappt: die Menge hält nur die
  // vom Standard abweichenden Knoten.
  const [toggled, setToggled] = useState<ReadonlySet<string>>(new Set())
  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [target, setTarget] = useState<DeleteTarget | null>(null)

  const warehouses = useMemo(() => sortByCode(layout.data ?? []), [layout.data])
  const binCodes = useMemo(() => allBinCodes(warehouses), [warehouses])

  if (!layout.data) {
    return <LoadState isLoading={layout.isLoading} error={layout.error} what={t('warehouse:structure.what')} onRetry={() => void layout.refetch()} />
  }

  const deleters: Record<DeleteKind, typeof deleteWarehouse> = {
    warehouse: deleteWarehouse,
    zone: deleteZone,
    aisle: deleteAisle,
    shelf: deleteShelf,
    bin: deleteBin,
  }
  const activeDelete = target ? deleters[target.kind] : null

  const ctx: TreeContext = {
    isOpen: (id, defaultOpen) => defaultOpen !== toggled.has(id),
    toggle: (id) => setToggled((prev) => {
      const next = new Set(prev)
      if (!next.delete(id)) next.add(id)
      return next
    }),
    openDialog: setDialog,
    askDelete: (next) => { Object.values(deleters).forEach((m) => m.reset()); setTarget(next) },
    allBinCodes: binCodes,
  }

  const closeDialog = () => setDialog(null)
  const cancelDelete = () => { activeDelete?.reset(); setTarget(null) }
  const runDelete = () => {
    if (!target || !activeDelete) return
    activeDelete.mutate(target.id, { onSuccess: () => { flash(t('warehouse:structure.deleted', { label: target.label })); setTarget(null) } })
  }
  const impact = target ? describeCounts(target.counts) : ''

  return (
    <>
      <h2>{t('warehouse:structure.title')}</h2>
      <p className="muted">
        <Trans i18nKey="warehouse:structure.intro" components={{ a: <Link to="/layout" /> }} />
      </p>

      <div className="toolbar">
        <button className="primary" onClick={() => setDialog({ kind: 'warehouse' })}>{t('warehouse:structure.newWarehouse')}</button>
      </div>

      <ErrorBanner error={layout.error} title={t('warehouse:structure.refreshFailed')} onRetry={() => void layout.refetch()} />

      <div style={{ minHeight: 36, marginBottom: 8 }}>
        {message && <div className="success" role="status" style={{ margin: 0 }}>{message}</div>}
      </div>

      {warehouses.length === 0 ? (
        <div className="card wh-empty">
          <h3>{t('warehouse:selector.none')}</h3>
          <p className="muted">
            {t('warehouse:structure.emptyBody')}
          </p>
          <button className="primary" onClick={() => setDialog({ kind: 'warehouse' })}>{t('warehouse:structure.createWarehouse')}</button>
        </div>
      ) : (
        warehouses.map((w) => (
          <div className="card" key={w.id}>
            <ul className="wh-tree">
              <WarehouseNode warehouse={w} ctx={ctx} />
            </ul>
          </div>
        ))
      )}

      {dialog?.kind === 'warehouse' && <WarehouseDialog existing={dialog.existing} onClose={closeDialog} onSaved={flash} />}
      {dialog?.kind === 'zone' && <ZoneDialog warehouse={dialog.warehouse} existing={dialog.existing} onClose={closeDialog} onSaved={flash} />}
      {dialog?.kind === 'aisle' && <AisleDialog zone={dialog.zone} existing={dialog.existing} onClose={closeDialog} onSaved={flash} />}
      {dialog?.kind === 'newShelf' && <NewShelfDialog open aisleId={dialog.aisleId} onClose={closeDialog} onCreated={flash} />}
      {dialog?.kind === 'shelf' && <ShelfEditDialog shelf={dialog.shelf} onClose={closeDialog} onSaved={flash} />}
      {dialog?.kind === 'bin' && <BinDialog shelf={dialog.shelf} existing={dialog.existing} allBinCodes={binCodes} onClose={closeDialog} onSaved={flash} />}
      {dialog?.kind === 'binType' && <BinTypeDialog bin={dialog.bin} onClose={closeDialog} onSaved={flash} />}

      <ConfirmDialog
        open={target !== null}
        title={target ? t('warehouse:structure.deleteTitle', { label: target.label }) : ''}
        confirmLabel={t('common:delete')}
        danger
        pending={activeDelete?.isPending ?? false}
        onConfirm={runDelete}
        onCancel={cancelDelete}
      >
        {target && (
          <>
            {impact && (
              <p>
                <Trans
                  i18nKey={target.kind === 'warehouse' ? 'warehouse:structure.deleteImpactWarehouse' : 'warehouse:structure.deleteImpact'}
                  values={{ impact }}
                  components={{ strong: <strong /> }}
                />
              </p>
            )}
            <p>
              {target.kind === 'bin'
                ? t('warehouse:structure.deleteHintBin')
                : t('warehouse:structure.deleteHint')}
            </p>
            <p>{t('warehouse:structure.deleteIrreversible')}</p>
            <ErrorBanner error={activeDelete?.error} title={t('warehouse:structure.deleteFailed')} onDismiss={activeDelete?.reset} />
          </>
        )}
      </ConfirmDialog>
    </>
  )
}

// ---- Baum -----------------------------------------------------------------------------------------------------------

/** Zusatztext nur für Screenreader: macht gleich beschriftete Schaltflächen ("Löschen") pro Zeile unterscheidbar. */
function Hidden({ children }: { children: ReactNode }) {
  return <span className="visually-hidden">{children}</span>
}

function Row({ expandable, open, onToggle, toggleLabel, kind, children, actions }: {
  expandable: boolean
  open: boolean
  onToggle: () => void
  /** Wer auf-/zugeklappt wird, für Screenreader ("Zone Z-A"). */
  toggleLabel: string
  kind: string
  children: ReactNode
  actions: ReactNode
}) {
  const { t } = useTranslation()
  return (
    <div className="wh-row">
      {expandable ? (
        <button type="button" className="wh-toggle" aria-expanded={open} onClick={onToggle}>
          <span aria-hidden="true">{open ? '▾' : '▸'}</span>
          <Hidden>{open ? t('warehouse:tree.collapse', { who: toggleLabel }) : t('warehouse:tree.expand', { who: toggleLabel })}</Hidden>
        </button>
      ) : (
        <span className="wh-toggle-spacer" aria-hidden="true" />
      )}
      <div className="wh-label"><span className="wh-kind">{kind}</span>{children}</div>
      <div className="wh-actions">{actions}</div>
    </div>
  )
}

function WarehouseNode({ warehouse, ctx }: { warehouse: WarehouseDto; ctx: TreeContext }) {
  const { t } = useTranslation()
  const open = ctx.isOpen(warehouse.id, true)
  const counts = countInWarehouse(warehouse)
  const zones = sortByCode(warehouse.zones)
  const who = `${t('warehouse:kind.warehouse')} ${warehouse.code}`
  return (
    <li>
      <Row
        kind={t('warehouse:kind.warehouse')}
        expandable={zones.length > 0}
        open={open}
        onToggle={() => ctx.toggle(warehouse.id)}
        toggleLabel={who}
        actions={(
          <>
            <button onClick={() => ctx.openDialog({ kind: 'zone', warehouse })}>{t('warehouse:tree.addZone')}{' '}<Hidden>{t('warehouse:tree.addTo', { who })}</Hidden></button>
            <button onClick={() => ctx.openDialog({ kind: 'warehouse', existing: warehouse })}>{t('common:edit')}<Hidden>: {who}</Hidden></button>
            <button className="danger" onClick={() => ctx.askDelete({ kind: 'warehouse', id: warehouse.id, label: `${t('warehouse:kind.warehouse')} "${warehouse.code}"`, counts })}>
              {t('common:delete')}<Hidden>: {who}</Hidden>
            </button>
          </>
        )}
      >
        <strong>{warehouse.code}</strong> {warehouse.name}
        <span className="wh-meta">{describeCounts(counts) || t('warehouse:tree.empty')}</span>
      </Row>
      {open && zones.length > 0 && (
        <ul>{zones.map((z) => <ZoneNode key={z.id} warehouse={warehouse} zone={z} ctx={ctx} />)}</ul>
      )}
    </li>
  )
}

function ZoneNode({ warehouse, zone, ctx }: { warehouse: WarehouseDto; zone: ZoneDto; ctx: TreeContext }) {
  const { t } = useTranslation()
  const open = ctx.isOpen(zone.id, true)
  const counts = countInZone(zone)
  const aisles = sortByCode(zone.aisles)
  const who = `${t('warehouse:kind.zone')} ${zone.code}`
  return (
    <li>
      <Row
        kind={t('warehouse:kind.zone')}
        expandable={aisles.length > 0}
        open={open}
        onToggle={() => ctx.toggle(zone.id)}
        toggleLabel={who}
        actions={(
          <>
            <button onClick={() => ctx.openDialog({ kind: 'aisle', zone })}>{t('warehouse:tree.addAisle')}{' '}<Hidden>{t('warehouse:tree.addTo', { who })}</Hidden></button>
            <button onClick={() => ctx.openDialog({ kind: 'zone', warehouse, existing: zone })}>{t('common:edit')}<Hidden>: {who}</Hidden></button>
            <button className="danger" onClick={() => ctx.askDelete({ kind: 'zone', id: zone.id, label: `${t('warehouse:kind.zone')} "${zone.code}"`, counts })}>
              {t('common:delete')}<Hidden>: {who}</Hidden>
            </button>
          </>
        )}
      >
        <strong>{zone.code}</strong> {zone.name}
        <span className="wh-meta">{describeCounts(counts) || t('warehouse:tree.empty')}</span>
      </Row>
      {open && aisles.length > 0 && (
        <ul>{aisles.map((a) => <AisleNode key={a.id} zone={zone} aisle={a} ctx={ctx} />)}</ul>
      )}
    </li>
  )
}

function AisleNode({ zone, aisle, ctx }: { zone: ZoneDto; aisle: AisleDto; ctx: TreeContext }) {
  const { t } = useTranslation()
  const open = ctx.isOpen(aisle.id, true)
  const counts = countInAisle(aisle)
  const shelves = sortByCode(aisle.shelves)
  const who = `${t('warehouse:kind.aisle')} ${aisle.code}`
  return (
    <li>
      <Row
        kind={t('warehouse:kind.aisle')}
        expandable={shelves.length > 0}
        open={open}
        onToggle={() => ctx.toggle(aisle.id)}
        toggleLabel={who}
        actions={(
          <>
            <button onClick={() => ctx.openDialog({ kind: 'newShelf', aisleId: aisle.id })}>{t('warehouse:tree.addShelf')}{' '}<Hidden>{t('warehouse:tree.addTo', { who })}</Hidden></button>
            <button onClick={() => ctx.openDialog({ kind: 'aisle', zone, existing: aisle })}>{t('common:edit')}<Hidden>: {who}</Hidden></button>
            <button className="danger" onClick={() => ctx.askDelete({ kind: 'aisle', id: aisle.id, label: `${t('warehouse:kind.aisle')} "${aisle.code}"`, counts })}>
              {t('common:delete')}<Hidden>: {who}</Hidden>
            </button>
          </>
        )}
      >
        <strong>{aisle.code}</strong>
        <span className="wh-meta">{describeCounts(counts) || t('warehouse:tree.empty')}</span>
      </Row>
      {open && shelves.length > 0 && (
        <ul>{shelves.map((s) => <ShelfNode key={s.id} shelf={s} ctx={ctx} />)}</ul>
      )}
    </li>
  )
}

function ShelfNode({ shelf, ctx }: { shelf: ShelfDto; ctx: TreeContext }) {
  const { t } = useTranslation()
  const open = ctx.isOpen(shelf.id, false)
  const counts = countInShelf(shelf)
  const bins = sortByCode(shelf.locations)
  const who = `${t('warehouse:kind.shelf')} ${shelf.code}`
  return (
    <li>
      <Row
        kind={t('warehouse:kind.shelf')}
        expandable={bins.length > 0}
        open={open}
        onToggle={() => ctx.toggle(shelf.id)}
        toggleLabel={who}
        actions={(
          <>
            <button onClick={() => ctx.openDialog({ kind: 'bin', shelf })}>{t('warehouse:tree.addBin')}{' '}<Hidden>{t('warehouse:tree.addTo', { who })}</Hidden></button>
            <button onClick={() => ctx.openDialog({ kind: 'shelf', shelf })}>{t('common:edit')}<Hidden>: {who}</Hidden></button>
            <button className="danger" onClick={() => ctx.askDelete({ kind: 'shelf', id: shelf.id, label: `${t('warehouse:kind.shelf')} "${shelf.code}"`, counts })}>
              {t('common:delete')}<Hidden>: {who}</Hidden>
            </button>
          </>
        )}
      >
        <strong>{shelf.code}</strong>
        <span className="wh-meta">{describeCounts(counts) || t('warehouse:tree.noBins')} · {shelf.widthMm} × {shelf.depthMm} × {shelf.heightMm} mm</span>
      </Row>
      {open && bins.length > 0 && (
        <ul>{bins.map((b) => <BinNode key={b.id} shelf={shelf} bin={b} ctx={ctx} />)}</ul>
      )}
    </li>
  )
}

function BinNode({ shelf, bin, ctx }: { shelf: ShelfDto; bin: StorageLocationDto; ctx: TreeContext }) {
  const { t } = useTranslation()
  const type = binTypeOf(bin)
  const threshold = thresholdOf(bin)
  const who = `${t('warehouse:kind.bin')} ${bin.code}`
  return (
    <li>
      <Row
        kind={t('warehouse:kind.bin')}
        expandable={false}
        open={false}
        onToggle={() => undefined}
        toggleLabel={who}
        actions={(
          <>
            <button onClick={() => ctx.openDialog({ kind: 'bin', shelf, existing: bin })}>{t('common:edit')}<Hidden>: {who}</Hidden></button>
            <button onClick={() => ctx.openDialog({ kind: 'binType', bin })}>{t('warehouse:tree.binType')}<Hidden>: {who}</Hidden></button>
            <button className="danger" onClick={() => ctx.askDelete({ kind: 'bin', id: bin.id, label: `${t('warehouse:kind.bin')} "${bin.code}"`, counts: { zones: 0, aisles: 0, shelves: 0, bins: 0 } })}>
              {t('common:delete')}<Hidden>: {who}</Hidden>
            </button>
          </>
        )}
      >
        <strong>{bin.code}</strong>{' '}
        <StatusPill status={type} tone={BIN_TONES[type]} label={t(`status:binType.${type}`)} small />
        {type === 'HotPick' && <span className="wh-meta">{t('warehouse:tree.threshold', { threshold })}</span>}
        <span className="wh-meta">{t('warehouse:tree.binMeta', { w: bin.widthMm, d: bin.depthMm, h: bin.heightMm, kg: bin.maxWeightGrams / 1000 })}</span>
      </Row>
    </li>
  )
}
