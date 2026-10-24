import { useEffect, useLayoutEffect, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import type { GridCellType } from '../../api/gridLayoutHooks'
import { CELL_TYPES, isStorage } from './gridModel'

interface Props {
  /** Art des einzelnen markierten Felds (hervorgehoben); null bei mehreren Feldern. */
  current: GridCellType | null
  containers: number
  onChoose: (type: GridCellType) => void
  onContainers: (n: number) => void
  /** Als Popup direkt im Raster (Rechtsklick/Doppelklick/Enter) an dieser Bildschirmposition; ohne: eingebettet im Panel. */
  popup?: { x: number; y: number }
  title?: string
  onClose?: () => void
}

/// <summary>
/// Feldart für die Markierung wählen: eine Schaltfläche je Art (Farbe wie im Raster) und die Zahl der Behälter für Lagerfelder.
/// Als Popup schließt es mit Escape, einem Klick daneben oder nach der Wahl; der Fokus liegt auf der aktuellen Art.
/// </summary>
export function CellTypeChooser({ current, containers, onChoose, onContainers, popup, title, onClose }: Props) {
  const { t } = useTranslation()
  const ref = useRef<HTMLDivElement>(null)

  // Im Fenster halten: rechts/unten nicht abschneiden (direkt am Element, vor dem ersten Zeichnen).
  useLayoutEffect(() => {
    const el = ref.current
    if (!popup || !el) return
    const box = el.getBoundingClientRect()
    el.style.left = `${Math.max(8, Math.min(popup.x, window.innerWidth - box.width - 8))}px`
    el.style.top = `${Math.max(8, Math.min(popup.y, window.innerHeight - box.height - 8))}px`
  }, [popup])

  // onClose kommt meist als neue Funktion je Render: über eine Ref, damit der Effekt nur beim Öffnen läuft (sonst sprünge
  // der Fokus bei jeder Eingabe zurück auf die Schaltflächen).
  const closeRef = useRef(onClose)
  useEffect(() => { closeRef.current = onClose })

  useEffect(() => {
    if (!popup) return
    const active = ref.current?.querySelector<HTMLElement>('[aria-pressed="true"]') ?? ref.current?.querySelector<HTMLElement>('button')
    active?.focus()
    const outside = (e: PointerEvent) => { if (!ref.current?.contains(e.target as Node)) closeRef.current?.() }
    const escape = (e: globalThis.KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); closeRef.current?.() } }
    document.addEventListener('pointerdown', outside, true)
    document.addEventListener('keydown', escape, true)
    return () => {
      document.removeEventListener('pointerdown', outside, true)
      document.removeEventListener('keydown', escape, true)
    }
  }, [popup])

  const body = (
    <>
      <div className="grid-chooser__types">
        {CELL_TYPES.map((type) => (
          <button
            key={type}
            type="button"
            className={`grid-tool${current === type ? ' is-active' : ''}`}
            aria-pressed={current === type}
            onClick={() => onChoose(type)}
            title={t(`gridLayout:hint.${type}`)}
          >
            <span className={`grid-tool__swatch grid-cell--${type.toLowerCase()}`} aria-hidden="true">
              {isStorage(type) ? containers : type === 'Walkway' ? '' : t(`gridLayout:glyph.${type}`)}
            </span>
            {t(`gridLayout:type.${type}`)}
          </button>
        ))}
      </div>
      <label className="grid-chooser__containers">
        {t('gridLayout:chooser.containers')}
        <input
          type="number"
          min={1}
          max={50}
          value={containers}
          onChange={(e) => { const n = Number(e.target.value); if (Number.isFinite(n) && n >= 1) onContainers(n) }}
        />
      </label>
    </>
  )

  if (!popup) return <div className="grid-chooser">{body}</div>

  return (
    <div
      ref={ref}
      className="grid-chooser grid-chooser--popup card"
      role="dialog"
      aria-label={t('gridLayout:chooser.label', { ref: title ?? '' })}
      style={{ left: popup.x, top: popup.y }}
    >
      {title && <div className="grid-chooser__title">{t('gridLayout:chooser.label', { ref: title })}</div>}
      {body}
    </div>
  )
}
