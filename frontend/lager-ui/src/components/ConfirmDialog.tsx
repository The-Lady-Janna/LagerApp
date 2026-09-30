import { useEffect, useId, useRef, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Modal } from './Modal'

interface Props {
  open: boolean
  title: string
  /** Erklärung, was die Aktion bewirkt (und dass sie sich nicht rückgängig machen lässt); auch Eingabefelder/Fehlerhinweise. */
  children?: ReactNode
  confirmLabel?: string
  cancelLabel?: string
  /** Rot hervorgehobene Bestätigung für zerstörerische Aktionen. */
  danger?: boolean
  /** Die Aktion läuft bereits: Bestätigen/Abbrechen/Escape sind gesperrt (kein Doppelklick, kein Schließen mittendrin). */
  pending?: boolean
  /** Bestätigen sperren, z. B. solange eine Eingabe im Dialog ungültig ist. */
  confirmDisabled?: boolean
  onConfirm: () => void
  onCancel: () => void
}

/// <summary>
/// Barrierefreier Bestätigungsdialog für irreversible/buchende Aktionen (ersetzt window.confirm), gebaut auf Modal:
/// role="dialog" + aria-modal, Escape und "Abbrechen" schließen, der Fokus liegt beim Öffnen auf der primären Aktion
/// (bzw. auf einem Element mit data-autofocus im Inhalt) und kehrt beim Schließen zum auslösenden Element zurück; Tab
/// bleibt im Dialog. Ein Klick auf den Hintergrund schließt bewusst NICHT — der zweite Klick eines Doppelklicks auf
/// den auslösenden Button würde den Dialog sonst sofort wieder verwerfen.
/// Doppelklick-Schutz: onConfirm feuert höchstens einmal, bis `pending` wieder endet bzw. der Dialog neu geöffnet wird.
/// </summary>
export function ConfirmDialog(props: Props) {
  return props.open ? <ConfirmDialogBody {...props} /> : null
}

function ConfirmDialogBody({
  title, children, confirmLabel, cancelLabel,
  danger = false, pending = false, confirmDisabled = false, onConfirm, onCancel,
}: Props) {
  const { t } = useTranslation()
  const bodyId = useId()
  const confirmRef = useRef<HTMLButtonElement>(null)
  const firedRef = useRef(false)

  // Nach einer beendeten Aktion (z. B. fehlgeschlagen, Dialog bleibt offen) darf erneut bestätigt werden.
  useEffect(() => {
    if (!pending) firedRef.current = false
  }, [pending])

  const confirm = () => {
    if (pending || confirmDisabled || firedRef.current) return
    firedRef.current = true
    onConfirm()
  }

  return (
    <Modal
      title={title}
      role="dialog"
      onClose={onCancel}
      closeOnEscape={!pending}
      describedBy={children ? bodyId : undefined}
      initialFocus={confirmRef}
    >
      {children && <div id={bodyId} className="modal-body">{children}</div>}
      <div className="toolbar modal-actions">
        <button ref={confirmRef} type="button" className={danger ? 'danger' : 'primary'} onClick={confirm} disabled={pending || confirmDisabled}>
          {confirmLabel ?? t('common:confirm')}
        </button>
        <button type="button" onClick={onCancel} disabled={pending}>{cancelLabel ?? t('common:cancel')}</button>
      </div>
    </Modal>
  )
}
