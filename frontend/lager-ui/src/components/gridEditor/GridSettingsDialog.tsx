import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Modal } from '../Modal'
import { applySettings, cellsOutside, GRID_LIMITS, settingsOf, type GridDraft, type GridSettings } from './gridModel'

interface Props {
  draft: GridDraft
  onApply: (settings: GridSettings, next: GridDraft) => void
  onClose: () => void
}

type Field = keyof GridSettings

/** Eingabe in der Einheit des Formulars (Meter, kg) und zurück in mm/g. */
const FIELDS: { key: Field; min: number; max: number; step: number; toForm: (v: number) => number; fromForm: (v: number) => number }[] = [
  { key: 'columns', min: 1, max: GRID_LIMITS.maxColumns, step: 1, toForm: (v) => v, fromForm: (v) => Math.round(v) },
  { key: 'rows', min: 1, max: GRID_LIMITS.maxRows, step: 1, toForm: (v) => v, fromForm: (v) => Math.round(v) },
  { key: 'cellSizeMm', min: GRID_LIMITS.minCellSizeMm / 1000, max: GRID_LIMITS.maxCellSizeMm / 1000, step: 0.05, toForm: (v) => v / 1000, fromForm: (v) => Math.round(v * 1000) },
  { key: 'containerWidthMm', min: 1, max: 100_000, step: 10, toForm: (v) => v, fromForm: (v) => Math.round(v) },
  { key: 'containerDepthMm', min: 1, max: 100_000, step: 10, toForm: (v) => v, fromForm: (v) => Math.round(v) },
  { key: 'containerHeightMm', min: 1, max: 100_000, step: 10, toForm: (v) => v, fromForm: (v) => Math.round(v) },
  { key: 'containerMaxWeightGrams', min: 0, max: 1_000_000, step: 1, toForm: (v) => v / 1000, fromForm: (v) => Math.round(v * 1000) },
]

/// <summary>
/// Rastergröße (Spalten, Zeilen, Feldgröße in Metern) und die Behältermaße für alle Lagerfelder. Verkleinern entfernt die
/// Felder außerhalb; der Dialog nennt vorher, wie viele das sind. Die Änderung landet im Entwurf (rückgängig machbar) und
/// wird erst mit "Speichern" angewendet.
/// </summary>
export function GridSettingsDialog({ draft, onApply, onClose }: Props) {
  const { t } = useTranslation()
  const initial = settingsOf(draft)
  const [values, setValues] = useState<Record<Field, string>>(() =>
    Object.fromEntries(FIELDS.map((f) => [f.key, String(f.toForm(initial[f.key]))])) as Record<Field, string>)

  const parsed = Object.fromEntries(FIELDS.map((f) => {
    const raw = Number(values[f.key].replace(',', '.'))
    return [f.key, values[f.key].trim() !== '' && Number.isFinite(raw) && raw >= f.min && raw <= f.max ? f.fromForm(raw) : null]
  })) as Record<Field, number | null>
  const cellSize = parsed.cellSizeMm
  const tooWide = cellSize !== null && parsed.containerWidthMm !== null && parsed.containerWidthMm > cellSize
  const tooDeep = cellSize !== null && parsed.containerDepthMm !== null && parsed.containerDepthMm > cellSize
  const valid = FIELDS.every((f) => parsed[f.key] !== null) && !tooWide && !tooDeep
  const dropped = parsed.columns !== null && parsed.rows !== null ? cellsOutside(draft, parsed.columns, parsed.rows) : 0

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (!valid) return
    const settings = parsed as GridSettings
    onApply(settings, applySettings(draft, settings).draft)
  }

  return (
    <Modal title={t('gridLayout:settings.title')} width={520} onClose={onClose}>
      <form onSubmit={submit}>
        <fieldset className="grid-settings">
          <legend>{t('gridLayout:settings.grid')}</legend>
          {FIELDS.slice(0, 3).map((f) => (
            <NumberInput key={f.key} field={f} value={values[f.key]} invalid={parsed[f.key] === null}
              onChange={(v) => setValues({ ...values, [f.key]: v })} />
          ))}
        </fieldset>
        <fieldset className="grid-settings">
          <legend>{t('gridLayout:settings.container')}</legend>
          {FIELDS.slice(3).map((f) => (
            <NumberInput key={f.key} field={f} value={values[f.key]}
              invalid={parsed[f.key] === null || (f.key === 'containerWidthMm' && tooWide) || (f.key === 'containerDepthMm' && tooDeep)}
              onChange={(v) => setValues({ ...values, [f.key]: v })} />
          ))}
        </fieldset>
        {(tooWide || tooDeep) && <p className="text-danger">{t('gridLayout:settings.containerTooLarge')}</p>}
        {dropped > 0 && <p className="warning">{t('gridLayout:settings.dropped', { count: dropped })}</p>}
        <p className="muted">{t('gridLayout:settings.note')}</p>
        <div className="toolbar modal-actions">
          <button type="submit" className="primary" disabled={!valid}>{t('gridLayout:settings.apply')}</button>
          <button type="button" onClick={onClose}>{t('common:cancel')}</button>
        </div>
      </form>
    </Modal>
  )
}

function NumberInput({ field, value, invalid, onChange }: {
  field: (typeof FIELDS)[number]
  value: string
  invalid: boolean
  onChange: (value: string) => void
}) {
  const { t } = useTranslation()
  return (
    <label>
      {t(`gridLayout:settings.field.${field.key}`)}
      <input
        type="number"
        inputMode="decimal"
        min={field.min}
        max={field.max}
        step={field.step}
        value={value}
        aria-invalid={invalid || undefined}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  )
}
