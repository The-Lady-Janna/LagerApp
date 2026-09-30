import { useState, type FormEvent, type ReactNode } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useAddBinToShelf } from '../../api/hooks'
import type { ShelfDto, StorageLocationDto, WarehouseDto, ZoneDto, AisleDto } from '../../api/types'
import {
  BIN_TYPES,
  binTypeOf,
  thresholdOf,
  useCreateAisle,
  useCreateWarehouse,
  useCreateZone,
  useSetBinType,
  useUpdateAisle,
  useUpdateBin,
  useUpdateShelf,
  useUpdateWarehouse,
  useUpdateZone,
  type AisleOrientation,
  type BinType,
} from '../../api/warehouseAdminHooks'
import { ErrorBanner } from '../../components/ErrorBanner'
import { Modal } from '../../components/Modal'
import {
  MAX_CODE_LENGTH,
  MAX_DIMENSION_MM,
  MAX_NAME_LENGTH,
  MAX_THRESHOLD,
  MAX_WEIGHT_KG,
  gramsToKilogramText,
  parseDimension,
  parseKilogramsAsGrams,
  parseThreshold,
} from './formFields'
import { nextFreeBinCode, nextFreeCode } from './treeModel'

/// <summary>
/// Formular-Dialoge der Lagerstruktur-Seite: Lager, Zone, Gang, Regal (ändern), Lagerplatz (anlegen/ändern) und Bin-Typ.
/// Jeder Dialog ruft seinen Mutations-Hook selbst auf, zeigt Fehler des Servers (doppelter Code, ...) im Dialog an und
/// schließt erst nach dem Speichern; `onSaved` meldet der Seite die Erfolgsmeldung. Regal anlegen macht NewShelfDialog.
/// </summary>

interface DialogProps {
  onClose: () => void
  /** Wird nach erfolgreichem Speichern mit einer kurzen Meldung aufgerufen (der Dialog schließt danach selbst). */
  onSaved: (message: string) => void
}

interface FormDialogProps {
  title: string
  width?: number
  pending: boolean
  error: unknown
  onDismissError: () => void
  onClose: () => void
  onSubmit: () => void
  submitLabel: string
  submitDisabled: boolean
  children: ReactNode
}

function FormDialog({ title, width = 460, pending, error, onDismissError, onClose, onSubmit, submitLabel, submitDisabled, children }: FormDialogProps) {
  const { t } = useTranslation()
  return (
    <Modal title={title} width={width} onClose={onClose} closeOnEscape={!pending}>
      <form
        onSubmit={(e: FormEvent) => {
          e.preventDefault()
          if (!submitDisabled && !pending) onSubmit()
        }}
      >
        {children}
        <ErrorBanner error={error} title={t('warehouse:dialog.saveFailed')} onDismiss={onDismissError} style={{ marginTop: 12 }} />
        <div className="toolbar modal-actions">
          <button type="submit" className="primary" disabled={pending || submitDisabled}>{pending ? t('common:saving') : submitLabel}</button>
          <button type="button" onClick={onClose} disabled={pending}>{t('common:cancel')}</button>
        </div>
      </form>
    </Modal>
  )
}

function TextField({ label, value, onChange, maxLength, autoFocus = false }: {
  label: string
  value: string
  onChange: (value: string) => void
  maxLength: number
  autoFocus?: boolean
}) {
  return (
    <label>
      {label}
      <input value={value} maxLength={maxLength} onChange={(e) => onChange(e.target.value)} data-autofocus={autoFocus ? '' : undefined} />
    </label>
  )
}

function NumberField({ label, value, onChange, invalid, min, max, step }: {
  label: string
  value: string
  onChange: (value: string) => void
  invalid: boolean
  min: number
  max: number
  step?: number
}) {
  return (
    <label>
      {label}
      <input type="number" inputMode="decimal" min={min} max={max} step={step} value={value} aria-invalid={invalid || undefined} onChange={(e) => onChange(e.target.value)} />
    </label>
  )
}

// ---- Lager ----------------------------------------------------------------------------------------------------------

export function WarehouseDialog({ existing, onClose, onSaved }: DialogProps & { existing?: WarehouseDto }) {
  const { t } = useTranslation()
  const create = useCreateWarehouse()
  const update = useUpdateWarehouse()
  const mutation = existing ? update : create
  const [code, setCode] = useState(existing?.code ?? '')
  const [name, setName] = useState(existing?.name ?? '')

  const submit = () => {
    const req = { code: code.trim(), name: name.trim() }
    const done = (verb: 'created' | 'saved') => ({ onSuccess: () => { onSaved(t(`warehouse:flash.${verb}`, { kind: t('warehouse:kind.warehouse'), code: req.code })); onClose() } })
    if (existing) update.mutate({ id: existing.id, req }, done('saved'))
    else create.mutate(req, done('created'))
  }

  return (
    <FormDialog
      title={existing ? t('warehouse:warehouseDialog.titleEdit', { code: existing.code }) : t('warehouse:warehouseDialog.titleNew')}
      pending={mutation.isPending}
      error={mutation.error}
      onDismissError={mutation.reset}
      onClose={onClose}
      onSubmit={submit}
      submitLabel={existing ? t('common:save') : t('warehouse:warehouseDialog.submitNew')}
      submitDisabled={code.trim() === '' || name.trim() === ''}
    >
      <div className="grid-2">
        <TextField label={t('warehouse:dialog.code')} value={code} onChange={setCode} maxLength={MAX_CODE_LENGTH} autoFocus />
        <TextField label={t('warehouse:dialog.name')} value={name} onChange={setName} maxLength={MAX_NAME_LENGTH} />
      </div>
      <p className="muted" style={{ fontSize: 12, marginTop: 8 }}><Trans i18nKey="warehouse:warehouseDialog.hint" components={{ code: <code /> }} /></p>
    </FormDialog>
  )
}

// ---- Zone -----------------------------------------------------------------------------------------------------------

export function ZoneDialog({ warehouse, existing, onClose, onSaved }: DialogProps & { warehouse: WarehouseDto; existing?: ZoneDto }) {
  const { t } = useTranslation()
  const create = useCreateZone()
  const update = useUpdateZone()
  const mutation = existing ? update : create
  const suggestion = nextFreeCode('Z', warehouse.zones.map((z) => z.code))
  const [code, setCode] = useState(existing?.code ?? suggestion)
  const [name, setName] = useState(existing?.name ?? t('warehouse:zoneDefaultName', { n: suggestion.slice(1) }))

  const submit = () => {
    const req = { code: code.trim(), name: name.trim() }
    const done = (verb: 'created' | 'saved') => ({ onSuccess: () => { onSaved(t(`warehouse:flash.${verb}`, { kind: t('warehouse:kind.zone'), code: req.code })); onClose() } })
    if (existing) update.mutate({ id: existing.id, req }, done('saved'))
    else create.mutate({ warehouseId: warehouse.id, ...req }, done('created'))
  }

  return (
    <FormDialog
      title={existing ? t('warehouse:zoneDialog.titleEdit', { code: existing.code }) : t('warehouse:zoneDialog.titleNew', { code: warehouse.code })}
      pending={mutation.isPending}
      error={mutation.error}
      onDismissError={mutation.reset}
      onClose={onClose}
      onSubmit={submit}
      submitLabel={existing ? t('common:save') : t('warehouse:zoneDialog.submitNew')}
      submitDisabled={code.trim() === '' || name.trim() === ''}
    >
      <div className="grid-2">
        <TextField label={t('warehouse:dialog.code')} value={code} onChange={setCode} maxLength={MAX_CODE_LENGTH} autoFocus />
        <TextField label={t('warehouse:dialog.name')} value={name} onChange={setName} maxLength={MAX_NAME_LENGTH} />
      </div>
      <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>{t('warehouse:zoneDialog.hint')}</p>
    </FormDialog>
  )
}

// ---- Gang -----------------------------------------------------------------------------------------------------------

const ORIENTATIONS: readonly AisleOrientation[] = ['AlongX', 'AlongY']

export function AisleDialog({ zone, existing, onClose, onSaved }: DialogProps & { zone: ZoneDto; existing?: AisleDto }) {
  const { t } = useTranslation()
  const create = useCreateAisle()
  const update = useUpdateAisle()
  const mutation = existing ? update : create
  const [code, setCode] = useState(existing?.code ?? nextFreeCode('A', zone.aisles.map((a) => a.code)))
  const [orientation, setOrientation] = useState<AisleOrientation>(existing?.orientation ?? 'AlongX')

  const submit = () => {
    const req = { code: code.trim(), orientation }
    const done = (verb: 'created' | 'saved') => ({ onSuccess: () => { onSaved(t(`warehouse:flash.${verb}`, { kind: t('warehouse:kind.aisle'), code: req.code })); onClose() } })
    if (existing) update.mutate({ id: existing.id, req }, done('saved'))
    else create.mutate({ zoneId: zone.id, ...req }, done('created'))
  }

  return (
    <FormDialog
      title={existing ? t('warehouse:aisleDialog.titleEdit', { code: existing.code }) : t('warehouse:aisleDialog.titleNew', { code: zone.code })}
      pending={mutation.isPending}
      error={mutation.error}
      onDismissError={mutation.reset}
      onClose={onClose}
      onSubmit={submit}
      submitLabel={existing ? t('common:save') : t('warehouse:aisleDialog.submitNew')}
      submitDisabled={code.trim() === ''}
    >
      <div className="grid-2">
        <TextField label={t('warehouse:dialog.code')} value={code} onChange={setCode} maxLength={MAX_CODE_LENGTH} autoFocus />
        <label>
          {t('warehouse:aisleDialog.orientation')}
          <select value={orientation} onChange={(e) => setOrientation(e.target.value as AisleOrientation)}>
            {ORIENTATIONS.map((o) => <option key={o} value={o}>{t(`warehouse:aisleDialog.orientations.${o}`)}</option>)}
          </select>
        </label>
      </div>
      <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>{t('warehouse:aisleDialog.hint')}</p>
    </FormDialog>
  )
}

// ---- Regal ----------------------------------------------------------------------------------------------------------

export function ShelfEditDialog({ shelf, onClose, onSaved }: DialogProps & { shelf: ShelfDto }) {
  const { t } = useTranslation()
  const update = useUpdateShelf()
  const [code, setCode] = useState(shelf.code)
  const [width, setWidth] = useState(String(shelf.widthMm))
  const [depth, setDepth] = useState(String(shelf.depthMm))
  const [height, setHeight] = useState(String(shelf.heightMm))
  const dims = { widthMm: parseDimension(width), depthMm: parseDimension(depth), heightMm: parseDimension(height) }

  const submit = () => {
    if (dims.widthMm === null || dims.depthMm === null || dims.heightMm === null) return
    const req = { code: code.trim(), widthMm: dims.widthMm, depthMm: dims.depthMm, heightMm: dims.heightMm }
    update.mutate({ id: shelf.id, req }, { onSuccess: () => { onSaved(t('warehouse:flash.saved', { kind: t('warehouse:kind.shelf'), code: req.code })); onClose() } })
  }

  return (
    <FormDialog
      title={t('warehouse:shelfDialog.titleEdit', { code: shelf.code })}
      pending={update.isPending}
      error={update.error}
      onDismissError={update.reset}
      onClose={onClose}
      onSubmit={submit}
      submitLabel={t('common:save')}
      submitDisabled={code.trim() === '' || dims.widthMm === null || dims.depthMm === null || dims.heightMm === null}
    >
      <TextField label={t('warehouse:dialog.code')} value={code} onChange={setCode} maxLength={MAX_CODE_LENGTH} autoFocus />
      <div className="grid-3" style={{ marginTop: 8 }}>
        <NumberField label={t('warehouse:dialog.width')} value={width} onChange={setWidth} invalid={dims.widthMm === null} min={1} max={MAX_DIMENSION_MM} />
        <NumberField label={t('warehouse:dialog.depth')} value={depth} onChange={setDepth} invalid={dims.depthMm === null} min={1} max={MAX_DIMENSION_MM} />
        <NumberField label={t('warehouse:dialog.height')} value={height} onChange={setHeight} invalid={dims.heightMm === null} min={1} max={MAX_DIMENSION_MM} />
      </div>
      <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>
        {t('warehouse:shelfDialog.hint')}
      </p>
    </FormDialog>
  )
}

// ---- Lagerplatz -----------------------------------------------------------------------------------------------------

/** Standardmaße eines neuen Lagerplatzes (wie im Layout-Editor). */
const NEW_BIN = { widthMm: 600, depthMm: 600, heightMm: 500, maxWeightGrams: 50_000 }

/**
 * Lagerplatz anlegen (im Regal `shelf`) oder ändern (`existing`): Code, Abmessungen, Höchstlast.
 * `allBinCodes` = alle Lagerplatz-Codes des Systems (für den Vorschlag eines freien Codes; der Server prüft die Eindeutigkeit).
 */
export function BinDialog({ shelf, existing, allBinCodes, onClose, onSaved }: DialogProps & {
  shelf: ShelfDto
  existing?: StorageLocationDto
  allBinCodes: readonly string[]
}) {
  const { t } = useTranslation()
  const create = useAddBinToShelf()
  const update = useUpdateBin()
  const mutation = existing ? update : create
  const source = existing ?? NEW_BIN
  const [code, setCode] = useState(existing?.code ?? nextFreeBinCode(shelf.code, shelf.locations.map((b) => b.code), allBinCodes))
  const [width, setWidth] = useState(String(source.widthMm))
  const [depth, setDepth] = useState(String(source.depthMm))
  const [height, setHeight] = useState(String(source.heightMm))
  const [maxKg, setMaxKg] = useState(gramsToKilogramText(source.maxWeightGrams))
  const parsed = {
    widthMm: parseDimension(width),
    depthMm: parseDimension(depth),
    heightMm: parseDimension(height),
    maxWeightGrams: parseKilogramsAsGrams(maxKg),
  }

  const submit = () => {
    if (parsed.widthMm === null || parsed.depthMm === null || parsed.heightMm === null || parsed.maxWeightGrams === null) return
    const req = { code: code.trim(), widthMm: parsed.widthMm, depthMm: parsed.depthMm, heightMm: parsed.heightMm, maxWeightGrams: parsed.maxWeightGrams }
    const done = (verb: 'created' | 'saved') => ({ onSuccess: () => { onSaved(t(`warehouse:flash.${verb}`, { kind: t('warehouse:kind.bin'), code: req.code })); onClose() } })
    if (existing) update.mutate({ id: existing.id, req }, done('saved'))
    else create.mutate({ shelfId: shelf.id, req }, done('created'))
  }

  return (
    <FormDialog
      title={existing ? t('warehouse:binDialog.titleEdit', { code: existing.code }) : t('warehouse:binDialog.titleNew', { code: shelf.code })}
      pending={mutation.isPending}
      error={mutation.error}
      onDismissError={mutation.reset}
      onClose={onClose}
      onSubmit={submit}
      submitLabel={existing ? t('common:save') : t('warehouse:binDialog.submitNew')}
      submitDisabled={code.trim() === '' || Object.values(parsed).some((v) => v === null)}
    >
      <TextField label={t('warehouse:dialog.code')} value={code} onChange={setCode} maxLength={MAX_CODE_LENGTH} autoFocus />
      <div className="grid-3" style={{ marginTop: 8 }}>
        <NumberField label={t('warehouse:dialog.width')} value={width} onChange={setWidth} invalid={parsed.widthMm === null} min={1} max={MAX_DIMENSION_MM} />
        <NumberField label={t('warehouse:dialog.depth')} value={depth} onChange={setDepth} invalid={parsed.depthMm === null} min={1} max={MAX_DIMENSION_MM} />
        <NumberField label={t('warehouse:dialog.height')} value={height} onChange={setHeight} invalid={parsed.heightMm === null} min={1} max={MAX_DIMENSION_MM} />
      </div>
      <div style={{ marginTop: 8 }}>
        <NumberField label={t('warehouse:dialog.maxLoad')} value={maxKg} onChange={setMaxKg} invalid={parsed.maxWeightGrams === null} min={0} max={MAX_WEIGHT_KG} step={0.1} />
      </div>
      <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>
        {t('warehouse:binDialog.hint')}
      </p>
    </FormDialog>
  )
}

// ---- Bin-Typ --------------------------------------------------------------------------------------------------------

/** Bin-Typ (Standard, Hot-Pick, Reserve) und Nachschub-Schwelle eines Lagerplatzes. Nur Hot-Pick-Plätze haben eine Schwelle. */
export function BinTypeDialog({ bin, onClose, onSaved }: DialogProps & { bin: StorageLocationDto }) {
  const { t } = useTranslation()
  const setType = useSetBinType()
  const [type, setTypeState] = useState<BinType>(binTypeOf(bin))
  const [threshold, setThreshold] = useState(String(thresholdOf(bin)))
  const parsedThreshold = parseThreshold(threshold)
  const isHotPick = type === 'HotPick'

  const submit = () => {
    const replenishmentThreshold = isHotPick ? parsedThreshold : 0
    if (replenishmentThreshold === null) return
    setType.mutate({ id: bin.id, req: { binType: type, replenishmentThreshold } }, {
      onSuccess: () => {
        const typeName = t(`status:binType.${type}`)
        onSaved(isHotPick
          ? t('warehouse:binTypeDialog.flashHotPick', { code: bin.code, type: typeName, threshold: replenishmentThreshold })
          : t('warehouse:binTypeDialog.flash', { code: bin.code, type: typeName }))
        onClose()
      },
    })
  }

  return (
    <FormDialog
      title={t('warehouse:binTypeDialog.title', { code: bin.code })}
      pending={setType.isPending}
      error={setType.error}
      onDismissError={setType.reset}
      onClose={onClose}
      onSubmit={submit}
      submitLabel={t('common:save')}
      submitDisabled={isHotPick && parsedThreshold === null}
    >
      <div className="grid-2">
        <label>
          {t('warehouse:binTypeDialog.label')}
          <select value={type} onChange={(e) => setTypeState(e.target.value as BinType)} data-autofocus="">
            {BIN_TYPES.map((binType) => <option key={binType} value={binType}>{t(`status:binType.${binType}`)}</option>)}
          </select>
        </label>
        <NumberField
          label={t('warehouse:binTypeDialog.threshold')}
          value={isHotPick ? threshold : '0'}
          onChange={setThreshold}
          invalid={isHotPick && parsedThreshold === null}
          min={0}
          max={MAX_THRESHOLD}
        />
      </div>
      <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>
        <Trans i18nKey="warehouse:binTypeDialog.hint" components={{ strong: <strong /> }} />
        {isHotPick && parsedThreshold === 0 && t('warehouse:binTypeDialog.zeroNote')}
      </p>
    </FormDialog>
  )
}
