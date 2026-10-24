import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { useWalls, useWarehouseLayout } from '../api/hooks'
import { useGridLayout } from '../api/gridLayoutHooks'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { LoadState } from '../components/LoadState'
import { GridEditor } from '../components/gridEditor/GridEditor'
import { useActiveWarehouse } from '../state/activeWarehouse'
import { LayoutEditorPage } from './LayoutEditorPage'
import '../components/gridEditor/gridEditor.css'

type LayoutMode = 'grid' | 'advanced'

/// <summary>
/// Seite Lager-Layout mit zwei Editoren: "Raster" (einfach, wie Zellen einer Tabelle) und "Erweitert" (frei in Millimetern).
/// Die Wahl steht in der URL (?mode=grid|advanced). Ohne Wahl öffnet das Raster, wenn das Lager schon eins hat oder noch
/// nichts gezeichnet ist; ein frei gezeichnetes Lager ohne Raster öffnet im erweiterten Editor. Ungespeicherte Raster-Änderungen
/// fragen vor dem Wechsel nach.
/// </summary>
export function LayoutPage() {
  const { t } = useTranslation()
  const layout = useWarehouseLayout()
  const walls = useWalls()
  const activeId = useActiveWarehouse((s) => s.activeId)
  const warehouses = layout.data ?? []
  const warehouse = warehouses.find((w) => w.id === activeId) ?? warehouses[0] ?? null
  const grid = useGridLayout(warehouse?.id)
  const [params, setParams] = useSearchParams()
  const [dirty, setDirty] = useState(false)
  const [pendingMode, setPendingMode] = useState<LayoutMode | null>(null)

  // Laden, Fehler und "noch kein Lager" zeigt der erweiterte Editor schon passend an.
  if (!warehouse) return <LayoutEditorPage />

  const requested = params.get('mode')
  const hasFreeDrawing = warehouse.zones.some((z) => z.aisles.some((a) => a.shelves.length > 0))
    || (walls.data ?? []).some((w) => w.warehouseId === warehouse.id)
  const mode: LayoutMode | null = requested === 'grid' || requested === 'advanced'
    ? requested
    : grid.data ? (grid.data.exists || !hasFreeDrawing ? 'grid' : 'advanced') : null

  const switchTo = (next: LayoutMode) => {
    if (next === mode) return
    if (mode === 'grid' && dirty) { setPendingMode(next); return }
    setParams({ mode: next }, { replace: true })
  }

  const switcher = (
    <>
      <div className="layout-mode" role="group" aria-label={t('gridLayout:mode.label')}>
        <button type="button" className={mode === 'grid' ? 'is-active' : ''} aria-pressed={mode === 'grid'} onClick={() => switchTo('grid')}>
          {t('gridLayout:mode.grid')}
        </button>
        <button type="button" className={mode === 'advanced' ? 'is-active' : ''} aria-pressed={mode === 'advanced'} onClick={() => switchTo('advanced')}>
          {t('gridLayout:mode.advanced')}
        </button>
      </div>
      {mode === 'advanced' && grid.data?.exists && (
        <p className="card card--info" style={{ marginTop: 0 }}>{t('gridLayout:mode.managedHint')}</p>
      )}
      <ConfirmDialog
        open={pendingMode !== null}
        title={t('gridLayout:mode.leaveTitle')}
        confirmLabel={t('gridLayout:mode.leave')}
        danger
        onConfirm={() => { if (pendingMode) setParams({ mode: pendingMode }, { replace: true }); setPendingMode(null); setDirty(false) }}
        onCancel={() => setPendingMode(null)}
      >
        {t('gridLayout:mode.leaveBody')}
      </ConfirmDialog>
    </>
  )

  if (mode === 'advanced') return <LayoutEditorPage headerSlot={switcher} />

  return (
    <>
      <h2>{t('warehouse:editor.title')}</h2>
      {switcher}
      <p className="muted" style={{ marginTop: 0 }}>{t('gridLayout:intro', { warehouse: `${warehouse.code} – ${warehouse.name}` })}</p>
      {mode === null
        ? <LoadState isLoading={grid.isLoading} error={grid.error} what={t('gridLayout:what')} onRetry={() => void grid.refetch()} />
        : <GridEditor key={warehouse.id} warehouse={warehouse} onDirtyChange={setDirty} />}
    </>
  )
}
