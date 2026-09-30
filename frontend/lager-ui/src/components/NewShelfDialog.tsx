import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useCreateShelf, useWarehouseLayout } from '../api/hooks'
import type { WarehouseDto } from '../api/types'
import { useCreateAisle, useCreateZone } from '../api/warehouseAdminHooks'
import { useActiveWarehouse } from '../state/activeWarehouse'
import { useAuth } from '../state/auth'
import { ErrorBanner } from './ErrorBanner'
import { Modal } from './Modal'

interface Props {
  open: boolean
  onClose: () => void
  /** Gang, in dem das Regal entsteht (z. B. aus der Lagerstruktur-Seite): dann entfällt die Auswahl. */
  aisleId?: string
  /** Wird nach dem Anlegen mit einer kurzen Meldung aufgerufen. */
  onCreated?: (message: string) => void
}

/// <summary>
/// Regal (mit den ersten Lagerplätzen) anlegen. Ohne Vorwissen über Gang-Ids: ist genau ein Gang vorhanden, ist er vorgewählt; gibt es
/// noch gar keinen Gang, aber ein Lager, legt der Dialog beim Anlegen die Zone "Z1" und den Gang "A1" mit an (Lagerstruktur, WP22);
/// gibt es kein Lager, verweist er auf die Lagerstruktur-Seite. Der Inhalt wird nur gemountet, solange der Dialog offen ist -
/// jedes Öffnen beginnt mit frischen Feldern (und ohne den Fehler des letzten Versuchs).
/// </summary>
export function NewShelfDialog({ open, ...rest }: Props) {
  return open ? <NewShelfForm {...rest} /> : null
}

function NewShelfForm({ onClose, aisleId: fixedAisleId, onCreated }: Omit<Props, 'open'>) {
  const { t } = useTranslation()
  const layout = useWarehouseLayout()
  const createMut = useCreateShelf()
  const createZone = useCreateZone()
  const createAisle = useCreateAisle()
  const activeWarehouseId = useActiveWarehouse((s) => s.activeId)
  const canManage = useAuth((s) => s.hasRole('Manager'))

  const warehouses = layout.data ?? []
  const aisles = warehouses.flatMap((w) =>
    w.zones.flatMap((z) => z.aisles.map((a) => ({ id: a.id, label: `${w.code} / ${z.code} / ${a.code}` })))
  )
  // Ohne jeden Gang legt der Dialog Zone und Gang beim Anlegen mit an (im aktiven bzw. ersten Lager).
  const needsStructure = fixedAisleId === undefined && layout.data !== undefined && aisles.length === 0 && warehouses.length > 0

  const [pickedAisleId, setPickedAisleId] = useState('')
  const [pickedWarehouseId, setPickedWarehouseId] = useState('')
  const [structurePending, setStructurePending] = useState(false)
  const [structureError, setStructureError] = useState<unknown>(null)
  const [code, setCode] = useState('')
  const [xMm, setXMm] = useState(0)
  const [yMm, setYMm] = useState(0)
  const [widthMm, setWidthMm] = useState(2000)
  const [depthMm, setDepthMm] = useState(600)
  const [heightMm, setHeightMm] = useState(2000)
  const [binCount, setBinCount] = useState(3)
  const [binWidth, setBinWidth] = useState(600)
  const [binDepth, setBinDepth] = useState(600)
  const [binHeight, setBinHeight] = useState(500)
  const [binMaxKg, setBinMaxKg] = useState(50)

  // Auswahl: ausdrücklich gewählter Gang, sonst der feste, sonst der einzige vorhandene.
  const aisleId = fixedAisleId ?? (aisles.some((a) => a.id === pickedAisleId) ? pickedAisleId : aisles.length === 1 ? aisles[0].id : '')
  const targetWarehouse = warehouses.find((w) => w.id === (pickedWarehouseId || activeWarehouseId)) ?? warehouses[0]
  const pending = createMut.isPending || structurePending
  const error = createMut.error ?? structureError

  const shelfRequest = (targetAisleId: string) => ({
    aisleId: targetAisleId,
    code: code.trim(),
    position: { xMm, yMm, zMm: 0 },
    widthMm,
    depthMm,
    heightMm,
    initialBinCount: binCount,
    binWidthMm: binWidth,
    binDepthMm: binDepth,
    binHeightMm: binHeight,
    binMaxWeightGrams: binMaxKg * 1000,
  })

  const done = () => {
    onCreated?.(t('warehouse:flash.created', { kind: t('warehouse:kind.shelf'), code: code.trim() }))
    onClose()
  }

  // Zone und Gang fehlen: der Reihe nach anlegen. Bricht ein Schritt ab, bleibt das bisher Angelegte bestehen (der Dialog zeigt den
  // Fehler; beim nächsten Versuch ist der Gang dann schon da und wird vorgewählt).
  const createWithStructure = async (warehouseId: string, existingZoneId: string | undefined) => {
    setStructurePending(true)
    setStructureError(null)
    try {
      const zoneId = existingZoneId ?? (await createZone.mutateAsync({ warehouseId, code: 'Z1', name: t('warehouse:zoneDefaultName', { n: 1 }) })).id
      const aisle = await createAisle.mutateAsync({ zoneId, code: 'A1' })
      await createMut.mutateAsync(shelfRequest(aisle.id))
      done()
    } catch (e) {
      setStructureError(e)
    } finally {
      setStructurePending(false)
    }
  }

  // mutate statt mutateAsync: ein Fehler bleibt in createMut.error (ErrorBanner unten), keine unbehandelte Rejection.
  const submit = () => {
    if (!code.trim() || pending) return
    if (needsStructure) {
      if (!targetWarehouse) return
      void createWithStructure(targetWarehouse.id, targetWarehouse.zones[0]?.id)
      return
    }
    if (!aisleId) return
    createMut.mutate(shelfRequest(aisleId), { onSuccess: done })
  }

  const dismissError = () => { createMut.reset(); setStructureError(null) }

  return (
    <Modal title={t('warehouse:newShelf.title')} width={560} onClose={onClose} closeOnEscape={!pending}>
      <div>
        {layout.data !== undefined && warehouses.length === 0 && (
          <div className="warning" role="status" style={{ marginBottom: 12 }}>
            {t('warehouse:newShelf.noWarehouse')} {canManage
              ? <Trans i18nKey="warehouse:newShelf.noWarehouseManage" components={{ a: <Link to="/warehouses" onClick={onClose} /> }} />
              : t('warehouse:newShelf.noWarehouseOther')}
          </div>
        )}
        {needsStructure && targetWarehouse && (
          <div className="info" role="status" style={{ marginBottom: 12 }}>
            <Trans
              i18nKey={targetWarehouse.zones.length === 0 ? 'warehouse:newShelf.needsStructureZone' : 'warehouse:newShelf.needsStructure'}
              values={{ code: targetWarehouse.code }}
              components={{ strong: <strong />, a: <Link to="/warehouses" onClick={onClose} /> }}
            />
          </div>
        )}
        <h4>{t('warehouse:newShelf.shelf')}</h4>
        <div className="grid-2">
          {fixedAisleId === undefined && needsStructure && warehouses.length > 1 ? (
            <label>
              {t('warehouse:newShelf.warehouse')}
              <select value={targetWarehouse?.id ?? ''} onChange={(e) => setPickedWarehouseId(e.target.value)}>
                {warehouses.map((w) => <option key={w.id} value={w.id}>{w.code} — {w.name}</option>)}
              </select>
            </label>
          ) : fixedAisleId === undefined && !needsStructure ? (
            <label>
              {t('warehouse:newShelf.aisle')}
              <select value={aisleId} onChange={(e) => setPickedAisleId(e.target.value)}>
                <option value="">{t('warehouse:newShelf.choose')}</option>
                {aisles.map((a) => <option key={a.id} value={a.id}>{a.label}</option>)}
              </select>
            </label>
          ) : (
            <div>
              {t('warehouse:newShelf.aisle')}
              <div style={{ padding: '6px 0' }}>{needsStructure ? t('warehouse:newShelf.willCreate') : aisleLabel(warehouses, fixedAisleId)}</div>
            </div>
          )}
          <label>{t('warehouse:dialog.code')}<input value={code} onChange={(e) => setCode(e.target.value)} placeholder={t('warehouse:newShelf.codePlaceholder')} /></label>
        </div>
        <div className="grid-3" style={{ marginTop: 8 }}>
          <label>{t('warehouse:newShelf.x')}<input type="number" value={xMm} onChange={(e) => setXMm(+e.target.value)} /></label>
          <label>{t('warehouse:newShelf.y')}<input type="number" value={yMm} onChange={(e) => setYMm(+e.target.value)} /></label>
          <label>—<span className="muted" style={{ padding: '6px 0' }}>{t('warehouse:newShelf.z')}</span></label>
        </div>
        <div className="grid-3" style={{ marginTop: 8 }}>
          <label>{t('warehouse:dialog.width')}<input type="number" value={widthMm} onChange={(e) => setWidthMm(+e.target.value)} /></label>
          <label>{t('warehouse:dialog.depth')}<input type="number" value={depthMm} onChange={(e) => setDepthMm(+e.target.value)} /></label>
          <label>{t('warehouse:dialog.height')}<input type="number" value={heightMm} onChange={(e) => setHeightMm(+e.target.value)} /></label>
        </div>

        <h4>{t('warehouse:newShelf.initialBins')}</h4>
        <div className="grid-2">
          <label>{t('warehouse:newShelf.count')}<input type="number" value={binCount} onChange={(e) => setBinCount(Math.max(0, +e.target.value))} /></label>
          <label>{t('warehouse:dialog.maxLoad')}<input type="number" value={binMaxKg} onChange={(e) => setBinMaxKg(+e.target.value)} /></label>
        </div>
        <div className="grid-3" style={{ marginTop: 8 }}>
          <label>{t('warehouse:newShelf.binWidth')}<input type="number" value={binWidth} onChange={(e) => setBinWidth(+e.target.value)} /></label>
          <label>{t('warehouse:newShelf.binDepth')}<input type="number" value={binDepth} onChange={(e) => setBinDepth(+e.target.value)} /></label>
          <label>{t('warehouse:newShelf.binHeight')}<input type="number" value={binHeight} onChange={(e) => setBinHeight(+e.target.value)} /></label>
        </div>
        <p className="muted" style={{ fontSize: 11, marginTop: 6 }}>
          <Trans i18nKey="warehouse:newShelf.hint" values={{ code: code || '<Code>' }} components={{ code: <code /> }} />
        </p>

        <ErrorBanner error={error} title={t('warehouse:newShelf.failed')} onDismiss={dismissError} />

        <div className="toolbar" style={{ marginTop: 16, justifyContent: 'flex-end' }}>
          <button onClick={onClose} disabled={pending}>{t('common:cancel')}</button>
          <button className="primary" onClick={submit} disabled={pending || !code.trim() || (!needsStructure && !aisleId)}>
            {pending ? t('warehouse:newShelf.creating') : t('warehouse:newShelf.submit')}
          </button>
        </div>
      </div>
    </Modal>
  )
}

/** "Lager / Zone / Gang" zu einer Gang-Id (leer, wenn der Gang nicht (mehr) im Baum steht). */
function aisleLabel(warehouses: readonly WarehouseDto[], aisleId: string | undefined): string {
  for (const w of warehouses) {
    for (const z of w.zones) {
      const aisle = z.aisles.find((a) => a.id === aisleId)
      if (aisle) return `${w.code} / ${z.code} / ${aisle.code}`
    }
  }
  return ''
}
