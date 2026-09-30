import { useEffect, useId, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'

export interface ModalProps {
  /** Dialogtitel — Beschriftung des Dialogs (aria-labelledby). */
  title: ReactNode
  /** Titel nur für Screenreader ausgeben (z. B. bei der Suche, die ihr Eingabefeld als Kopf hat). */
  hideTitle?: boolean
  /** Schließen (Escape, Klick auf den Hintergrund). Ohne Angabe ist der Dialog nicht per Escape/Hintergrund schließbar. */
  onClose?: () => void
  /** Escape schließt (Standard: ja, sofern onClose gesetzt ist). Für Pflicht-Dialoge oder laufende Aktionen abschalten. */
  closeOnEscape?: boolean
  /** Ein Klick auf den Hintergrund schließt (Standard: nein — ein Doppelklick auf den auslösenden Button würde den Dialog sonst sofort verwerfen). */
  closeOnBackdrop?: boolean
  /** id eines Elements im Dialog, das ihn erklärt (aria-describedby), z. B. die Folgen einer Bestätigung. */
  describedBy?: string
  /** Maximale Breite in px (auf schmalen Bildschirmen immer begrenzt auf die Bildschirmbreite). Standard 460. */
  width?: number
  /** "center" (Standard), "top" (Suche/Palette) oder "fullscreen" (Kamera-Ansicht). */
  placement?: 'center' | 'top' | 'fullscreen'
  /** Dialogfläche ohne Rahmen/Hintergrund/Innenabstand (Kamera-Ansicht) bzw. ohne Innenabstand ("flush"). */
  appearance?: 'card' | 'flush' | 'bare'
  /** Element, das beim Öffnen den Fokus bekommt. Ein Element mit data-autofocus im Inhalt hat Vorrang; ohne beides das erste bedienbare Element, sonst der Dialog. */
  initialFocus?: RefObject<HTMLElement | null>
  /** role="alertdialog" für Rückfragen, die eine Entscheidung erzwingen. */
  role?: 'dialog' | 'alertdialog'
  className?: string
  style?: CSSProperties
  children: ReactNode
}

export const FOCUSABLE = 'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])'

// Stapel offener Modale: nur das oberste reagiert auf Escape/Tab (z. B. ein Bestätigungsdialog über einem Formular-Dialog).
const modalStack: symbol[] = []
let scrollLocks = 0

function lockScroll() {
  if (scrollLocks++ === 0) document.body.classList.add('modal-open')
}
function unlockScroll() {
  if (--scrollLocks === 0) document.body.classList.remove('modal-open')
}

/// <summary>
/// Gemeinsame Basis aller modalen Dialoge (ersetzt die selbstgebauten Overlays): role="dialog" + aria-modal,
/// aria-labelledby (Titel) / aria-describedby, Fokusfalle (Tab/Shift+Tab bleiben im Dialog), Escape schließt,
/// der Fokus kehrt beim Schließen zum auslösenden Element zurück, der Hintergrund scrollt nicht mit.
/// Wird gerendert, solange die Komponente gemountet ist — die aufrufende Seite blendet sie per Bedingung ein.
/// Auf schmalen Bildschirmen ist der Dialog nie breiter als der Bildschirm (Rand 16 px).
/// </summary>
export function Modal({
  title, hideTitle = false, onClose, closeOnEscape = true, closeOnBackdrop = false, describedBy,
  width, placement = 'center', appearance = 'card', initialFocus, role = 'dialog', className, style, children,
}: ModalProps) {
  const titleId = useId()
  const dialogRef = useRef<HTMLDivElement>(null)
  const id = useRef<symbol>(Symbol('modal'))
  // Das Element mit Fokus VOR dem Öffnen (schon beim ersten Render festgehalten: ein autoFocus in den Kindern
  // hätte den Fokus beim ersten Effekt bereits in den Dialog verlegt).
  const [previousFocus] = useState<HTMLElement | null>(() =>
    typeof document !== 'undefined' && document.activeElement instanceof HTMLElement ? document.activeElement : null)

  // Die Callbacks in Refs, damit die Tastatur-Listener bei jedem Render der Eltern nicht neu registriert werden.
  const onCloseRef = useRef(onClose)
  const closeOnEscapeRef = useRef(closeOnEscape)
  useEffect(() => {
    onCloseRef.current = onClose
    closeOnEscapeRef.current = closeOnEscape
  })

  // Fokus setzen, Scroll sperren, Fokus zurückgeben.
  useEffect(() => {
    const dialog = dialogRef.current
    lockScroll()
    if (dialog && !dialog.contains(document.activeElement)) {
      const preferred = initialFocus?.current
      const usable = preferred && !(preferred as HTMLButtonElement).disabled ? preferred : null
      const target = dialog.querySelector<HTMLElement>('[data-autofocus]')
        ?? usable
        ?? dialog.querySelector<HTMLElement>(FOCUSABLE)
        ?? dialog
      target.focus()
    }
    return () => {
      unlockScroll()
      if (previousFocus && document.contains(previousFocus)) previousFocus.focus()
    }
  }, [initialFocus, previousFocus])

  useEffect(() => {
    const me = id.current
    modalStack.push(me)
    const onKeyDown = (event: KeyboardEvent) => {
      if (modalStack[modalStack.length - 1] !== me) return
      if (event.key === 'Escape') {
        event.stopPropagation()
        if (closeOnEscapeRef.current) onCloseRef.current?.()
        return
      }
      if (event.key !== 'Tab' || !dialogRef.current) return
      // Fokus-Falle: Tab/Shift+Tab wandern nur durch die Elemente des Dialogs.
      const items = Array.from(dialogRef.current.querySelectorAll<HTMLElement>(FOCUSABLE))
      if (items.length === 0) {
        event.preventDefault()
        dialogRef.current.focus()
        return
      }
      const first = items[0]
      const last = items[items.length - 1]
      const active = document.activeElement
      const outside = !dialogRef.current.contains(active) || active === dialogRef.current
      if (event.shiftKey && (active === first || outside)) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && (active === last || outside)) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      const index = modalStack.indexOf(me)
      if (index >= 0) modalStack.splice(index, 1)
    }
  }, [])

  const backdropClass = ['modal-backdrop', placement !== 'center' ? `modal-backdrop--${placement}` : ''].filter(Boolean).join(' ')
  const dialogClass = ['modal-dialog', appearance !== 'card' ? `modal-dialog--${appearance}` : '', className ?? ''].filter(Boolean).join(' ')
  const dialogStyle = { ...(width ? { '--modal-width': `${width}px` } : {}), ...style } as CSSProperties

  return createPortal(
    <div
      className={backdropClass}
      // Nur ein Klick, der auch auf dem Hintergrund BEGONNEN hat (nicht ein im Dialog gestartetes Markieren), schließt.
      onMouseDown={closeOnBackdrop && onClose ? (e) => { if (e.target === e.currentTarget) onClose() } : undefined}
    >
      <div
        ref={dialogRef}
        className={dialogClass}
        style={dialogStyle}
        role={role}
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={describedBy}
        tabIndex={-1}
      >
        <h3 id={titleId} className={hideTitle ? 'visually-hidden' : 'modal-title'}>{title}</h3>
        {children}
      </div>
    </div>,
    document.body,
  )
}
