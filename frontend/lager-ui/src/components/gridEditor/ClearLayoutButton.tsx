import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useClearLayout } from '../../api/gridLayoutHooks'
import type { WarehouseDto } from '../../api/types'
import { dismissToastsForError, showToast } from '../../state/toasts'
import { ConfirmDialog } from '../ConfirmDialog'
import { ErrorBanner } from '../ErrorBanner'

interface Props {
  warehouse: WarehouseDto
  walls: number
  pickPoints: number
  gridCells: number
  /** Ungespeicherte Änderungen im Raster gehen mit verloren (der Dialog sagt es dazu). */
  unsavedGridChanges: boolean
  onCleared: () => void
}

/// <summary>
/// "Layout löschen": entfernt nach einer Rückfrage in einem Schritt Zonen, Gänge, Regale, Lagerplätze, Wände, Pickpunkte und das
/// Raster des Lagers (das Lager selbst bleibt). Liegt irgendwo Bestand oder verweisen Belege auf einen Lagerplatz, lehnt der Server
/// ab und nichts ändert sich; die Meldung steht dann im Dialog.
/// </summary>
export function ClearLayoutButton({ warehouse, walls, pickPoints, gridCells, unsavedGridChanges, onCleared }: Props) {
  const { t } = useTranslation()
  const clear = useClearLayout(warehouse.id)
  const [open, setOpen] = useState(false)
  const [error, setError] = useState<unknown>(null)

  const shelves = warehouse.zones.flatMap((z) => z.aisles).flatMap((a) => a.shelves)
  const bins = shelves.reduce((n, s) => n + s.locations.length, 0)
  const empty = warehouse.zones.length === 0 && walls === 0 && pickPoints === 0 && gridCells === 0

  const confirm = () => {
    setError(null)
    clear.mutate(undefined, {
      onSuccess: (r) => {
        setOpen(false)
        onCleared()
        showToast('success', t('gridLayout:clear.done', { shelves: r.shelves, bins: r.storageLocations, walls: r.walls, pickPoints: r.pickPoints }))
      },
      onError: (e) => { dismissToastsForError(e); setError(e) },
    })
  }

  return (
    <>
      <button
        type="button"
        className="danger"
        onClick={() => { setError(null); setOpen(true) }}
        disabled={empty}
        title={empty ? t('gridLayout:clear.empty') : undefined}
      >
        {t('gridLayout:clear.button')}
      </button>
      <ConfirmDialog
        open={open}
        title={t('gridLayout:clear.title', { warehouse: warehouse.code })}
        confirmLabel={t('gridLayout:clear.confirm')}
        danger
        pending={clear.isPending}
        onConfirm={confirm}
        onCancel={() => setOpen(false)}
      >
        <p>{t('gridLayout:clear.body', { shelves: shelves.length, bins, walls, pickPoints, cells: gridCells })}</p>
        {unsavedGridChanges && <p className="warning">{t('gridLayout:clear.unsaved')}</p>}
        <p className="muted">{t('gridLayout:clear.note')}</p>
        <ErrorBanner error={error} title={t('gridLayout:clear.failed')} onDismiss={() => setError(null)} />
      </ConfirmDialog>
    </>
  )
}
