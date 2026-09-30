import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useCartConfigs, useCreateCartConfig, useDeleteCartConfig, useUpdateCartConfig } from '../api/hooks'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import type { PickCartConfigDto } from '../api/types'

interface FormState {
  name: string
  levelCount: number
  levelWidthMm: number
  levelDepthMm: number
  levelHeightMm: number
  maxWeightKg: number
}

const EMPTY: FormState = {
  name: '',
  levelCount: 3,
  levelWidthMm: 600,
  levelDepthMm: 400,
  levelHeightMm: 250,
  maxWeightKg: 50,
}

export function CartConfigsPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useCartConfigs()
  const createMut = useCreateCartConfig()
  const updateMut = useUpdateCartConfig()
  const deleteMut = useDeleteCartConfig()

  const [editingId, setEditingId] = useState<string | null>(null)
  const [form, setForm] = useState<FormState>(EMPTY)
  const [confirmDelete, setConfirmDelete] = useState<PickCartConfigDto | null>(null)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('picking:carts.what')} onRetry={() => void refetch()} />

  const list = data

  const fromDto = (c: PickCartConfigDto): FormState => ({
    name: c.name,
    levelCount: c.levelCount,
    levelWidthMm: c.levelWidthMm,
    levelDepthMm: c.levelDepthMm,
    levelHeightMm: c.levelHeightMm,
    maxWeightKg: c.maxWeightGrams / 1000,
  })

  const startEdit = (c: PickCartConfigDto) => {
    setEditingId(c.id)
    setForm(fromDto(c))
  }

  const cancel = () => {
    setEditingId(null)
    setForm(EMPTY)
  }

  // mutate statt mutateAsync: Fehler bleiben in createMut/updateMut.error (siehe ErrorBanner) — keine unbehandelte Rejection.
  const submit = () => {
    const payload = {
      name: form.name,
      levelCount: form.levelCount,
      levelWidthMm: form.levelWidthMm,
      levelDepthMm: form.levelDepthMm,
      levelHeightMm: form.levelHeightMm,
      maxWeightGrams: Math.round(form.maxWeightKg * 1000),
    }
    if (editingId) {
      updateMut.mutate({ id: editingId, req: payload }, { onSuccess: cancel })
    } else {
      createMut.mutate(payload, { onSuccess: cancel })
    }
  }

  const remove = () => {
    const c = confirmDelete
    setConfirmDelete(null)
    if (!c) return
    deleteMut.mutate(c.id, { onSuccess: () => { if (editingId === c.id) cancel() } })
  }

  const update = <K extends keyof FormState>(k: K, v: FormState[K]) =>
    setForm((s) => ({ ...s, [k]: v }))

  const totalVolumeM3 = (form.levelCount * form.levelWidthMm * form.levelDepthMm * form.levelHeightMm) / 1_000_000_000

  return (
    <>
      <h2>{t('picking:carts.title')}</h2>

      <CollapsibleCard
        title={editingId ? t('picking:carts.formEdit') : t('picking:carts.formNew')}
        storageKey="cart-config-form"
        defaultOpen
      >
        <div className="grid-3">
          <label>{t('picking:carts.name')}<input value={form.name} onChange={(e) => update('name', e.target.value)} placeholder={t('picking:carts.namePlaceholder')} /></label>
          <label>{t('picking:carts.levels')}<input type="number" min={1} value={form.levelCount} onChange={(e) => update('levelCount', +e.target.value)} /></label>
          <label>{t('picking:carts.maxWeight')}<input type="number" min={1} value={form.maxWeightKg} onChange={(e) => update('maxWeightKg', +e.target.value)} /></label>
        </div>
        <div className="grid-3" style={{ marginTop: 8 }}>
          <label>{t('picking:carts.levelWidth')}<input type="number" min={1} value={form.levelWidthMm} onChange={(e) => update('levelWidthMm', +e.target.value)} /></label>
          <label>{t('picking:carts.levelDepth')}<input type="number" min={1} value={form.levelDepthMm} onChange={(e) => update('levelDepthMm', +e.target.value)} /></label>
          <label>{t('picking:carts.levelHeight')}<input type="number" min={1} value={form.levelHeightMm} onChange={(e) => update('levelHeightMm', +e.target.value)} /></label>
        </div>

        <h4 style={{ marginTop: 16 }}>{t('picking:carts.schema')}</h4>
        <CartSchema
          levelCount={form.levelCount}
          levelWidthMm={form.levelWidthMm}
          levelDepthMm={form.levelDepthMm}
          levelHeightMm={form.levelHeightMm}
        />
        <p className="muted" style={{ marginTop: 8 }}>
          <Trans
            i18nKey="picking:carts.volume"
            values={{ volume: totalVolumeM3.toFixed(2), levels: form.levelCount, w: form.levelWidthMm, d: form.levelDepthMm, h: form.levelHeightMm }}
            components={{ strong: <strong /> }}
          />
        </p>

        <div className="toolbar" style={{ marginTop: 16 }}>
          <button className="primary" onClick={submit} disabled={!form.name || createMut.isPending || updateMut.isPending}>
            {editingId ? t('picking:carts.saveChanges') : t('picking:carts.create')}
          </button>
          {editingId && <button onClick={cancel}>{t('common:cancel')}</button>}
        </div>
        <ErrorBanner
          error={createMut.error ?? updateMut.error}
          title={t('picking:carts.saveFailed')}
          onDismiss={() => { createMut.reset(); updateMut.reset() }}
          style={{ marginTop: 8 }}
        />
      </CollapsibleCard>

      <ErrorBanner error={deleteMut.error} title={t('picking:carts.deleteFailed')} onDismiss={deleteMut.reset} />

      <CollapsibleCard title={t('picking:carts.list.title', { count: list.length })} storageKey="cart-config-list" defaultOpen>
        <table>
          <caption className="visually-hidden">{t('picking:carts.list.caption')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('picking:carts.name')}</th>
              <th scope="col">{t('picking:carts.list.levels')}</th>
              <th scope="col">{t('picking:carts.list.dimensions')}</th>
              <th scope="col">{t('picking:carts.list.maxWeight')}</th>
              <th scope="col">{t('picking:carts.list.volume')}</th>
              <th scope="col"><span className="visually-hidden">{t('picking:col.actions')}</span></th>
            </tr>
          </thead>
          <tbody>
            {list.map((c) => (
              <tr key={c.id}>
                <td><strong>{c.name}</strong></td>
                <td>{c.levelCount}</td>
                <td>{c.levelWidthMm} × {c.levelDepthMm} × {c.levelHeightMm}</td>
                <td>{(c.maxWeightGrams / 1000).toFixed(1)} kg</td>
                <td>{(c.totalVolumeMm3 / 1_000_000_000).toFixed(2)} m³</td>
                <td>
                  <button onClick={() => startEdit(c)}>{t('common:edit')}</button>{' '}
                  <button className="danger" onClick={() => setConfirmDelete(c)} disabled={deleteMut.isPending}>{t('common:delete')}</button>
                </td>
              </tr>
            ))}
            {list.length === 0 && (
              <tr><td colSpan={6} className="muted">{t('picking:carts.list.empty')}</td></tr>
            )}
          </tbody>
        </table>
      </CollapsibleCard>

      <ConfirmDialog
        open={confirmDelete !== null}
        title={t('picking:carts.delete.title')}
        confirmLabel={t('common:delete')}
        danger
        onConfirm={remove}
        onCancel={() => setConfirmDelete(null)}
      >
        <p><Trans i18nKey="picking:carts.delete.body" values={{ name: confirmDelete?.name }} components={{ strong: <strong /> }} /></p>
      </ConfirmDialog>
    </>
  )
}

/** Simple side-view schematic of the cart's levels. */
function CartSchema({ levelCount, levelWidthMm, levelDepthMm, levelHeightMm }: {
  levelCount: number; levelWidthMm: number; levelDepthMm: number; levelHeightMm: number
}) {
  const { t } = useTranslation()
  // Scale to fit a 300x200 SVG comfortably.
  const totalHeight = levelCount * levelHeightMm
  const maxW = 280
  const maxH = 200
  const scaleX = maxW / levelWidthMm
  const scaleY = maxH / Math.max(totalHeight, 1)
  const scale = Math.min(scaleX, scaleY, 0.25)

  const wPx = levelWidthMm * scale
  const hPx = levelHeightMm * scale
  const totalHPx = levelCount * hPx

  return (
    <svg
      width={wPx + 24} height={totalHPx + 30} role="img"
      aria-label={t('picking:carts.schemaAria', { levels: levelCount, depth: levelDepthMm })}
      style={{ background: 'var(--c-surface-alt)', border: '1px solid var(--c-border)', borderRadius: 4, maxWidth: '100%' }}
    >
      {/* axles */}
      <line x1={6} y1={totalHPx + 12} x2={wPx + 18} y2={totalHPx + 12} style={{ stroke: 'var(--c-muted)' }} strokeWidth={2} />
      <circle cx={12} cy={totalHPx + 20} r={5} style={{ fill: 'var(--c-muted)' }} />
      <circle cx={wPx + 12} cy={totalHPx + 20} r={5} style={{ fill: 'var(--c-muted)' }} />
      {Array.from({ length: levelCount }).map((_, i) => (
        <g key={i}>
          <rect
            x={12}
            y={totalHPx - (i + 1) * hPx + 2}
            width={wPx}
            height={hPx - 4}
            style={{ fill: 'var(--c-warning-bg)', stroke: 'var(--c-warning)' }}
            strokeWidth={1}
          />
          <text x={16} y={totalHPx - i * hPx - 4} fontSize={11} style={{ fill: 'var(--c-warning)' }}>{t('picking:carts.level', { n: i + 1 })}</text>
        </g>
      ))}
      <text x={12} y={totalHPx + 26} fontSize={10} style={{ fill: 'var(--c-muted)' }}>
        {t('picking:carts.depth', { depth: levelDepthMm })}
      </text>
    </svg>
  )
}
