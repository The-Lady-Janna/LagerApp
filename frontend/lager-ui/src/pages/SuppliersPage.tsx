import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useCreateSupplier, useSuppliers, useToggleSupplier, useUpdateSupplier } from '../api/hooks'
import type { CreateSupplierRequest, SupplierDto } from '../api/types'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'
import { formatMoney } from '../lib/format'

export function SuppliersPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useSuppliers(true)
  const create = useCreateSupplier()
  const update = useUpdateSupplier()
  const toggle = useToggleSupplier()
  const [showForm, setShowForm] = useState(false)
  const [editing, setEditing] = useState<SupplierDto | null>(null)

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('purchasing:suppliers.what')} onRetry={() => void refetch()} />

  return (
    <>
      <h2>{t('purchasing:suppliers.title')}</h2>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <button className="primary" onClick={() => { setEditing(null); setShowForm(true) }}>{t('purchasing:suppliers.new')}</button>
      </div>

      {showForm && (
        <SupplierForm
          // key: beim Wechsel des bearbeiteten Lieferanten (oder zu "neu") beginnt das Formular frisch —
          // sonst zeigt es die Felder des vorherigen Datensatzes und speichert sie unter dem neuen.
          key={editing?.id ?? 'new'}
          existing={editing}
          onCancel={() => { setShowForm(false); setEditing(null) }}
          onCreate={(req) => create.mutate(req, { onSuccess: () => setShowForm(false) })}
          onUpdate={(id, req) => update.mutate({ id, req }, { onSuccess: () => { setShowForm(false); setEditing(null) } })}
          pending={create.isPending || update.isPending}
          error={create.error ?? update.error}
          onDismissError={() => { create.reset(); update.reset() }}
        />
      )}

      <ErrorBanner error={toggle.error} title={t('purchasing:suppliers.toggleFailed')} onDismiss={toggle.reset} />

      <div className="table-wrap">
      <table style={{ width: '100%' }}>
        <caption className="visually-hidden">{t('purchasing:suppliers.caption')}</caption>
        <thead>
          <tr><th scope="col">{t('purchasing:suppliers.col.code')}</th><th scope="col">{t('purchasing:suppliers.col.name')}</th><th scope="col">{t('purchasing:suppliers.col.contact')}</th><th scope="col">{t('purchasing:suppliers.col.leadTime')}</th><th scope="col">{t('purchasing:suppliers.col.minOrder')}</th><th scope="col">{t('purchasing:suppliers.col.currency')}</th><th scope="col">{t('purchasing:suppliers.col.status')}</th><th scope="col"><span className="visually-hidden">{t('purchasing:suppliers.col.actions')}</span></th></tr>
        </thead>
        <tbody>
          {data.map((s) => (
            <tr key={s.id} style={{ opacity: s.isActive ? 1 : 0.5 }}>
              <td><code>{s.code}</code></td>
              <td><strong>{s.name}</strong></td>
              <td className="muted" style={{ fontSize: 12 }}>
                {s.contactEmail}{s.contactEmail && s.contactPhone ? ' · ' : ''}{s.contactPhone}
              </td>
              <td>{t('purchasing:suppliers.days', { count: s.leadTimeDays })}</td>
              <td>{formatMoney(s.minOrderValueCents, s.currency)}</td>
              <td>{s.currency}</td>
              <td>{s.isActive ? t('purchasing:suppliers.active') : t('purchasing:suppliers.inactive')}</td>
              <td>
                <button onClick={() => { setEditing(s); setShowForm(true) }}>{t('purchasing:suppliers.edit')}</button>
                <button onClick={() => toggle.mutate({ id: s.id, active: !s.isActive })} disabled={toggle.isPending} style={{ marginLeft: 4 }}>
                  {s.isActive ? t('purchasing:suppliers.deactivate') : t('purchasing:suppliers.activate')}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>
    </>
  )
}

function SupplierForm({ existing, onCancel, onCreate, onUpdate, pending, error, onDismissError }: {
  existing: SupplierDto | null
  onCancel: () => void
  onCreate: (req: CreateSupplierRequest) => void
  onUpdate: (id: string, req: { name: string; contactEmail: string | null; contactPhone: string | null; notes: string | null; leadTimeDays: number; minOrderValueCents: number; currency: string }) => void
  pending: boolean
  error: unknown
  onDismissError: () => void
}) {
  const { t } = useTranslation()
  const [code, setCode] = useState(existing?.code ?? '')
  const [name, setName] = useState(existing?.name ?? '')
  const [email, setEmail] = useState(existing?.contactEmail ?? '')
  const [phone, setPhone] = useState(existing?.contactPhone ?? '')
  const [notes, setNotes] = useState(existing?.notes ?? '')
  const [leadTime, setLeadTime] = useState(existing?.leadTimeDays ?? 7)
  const [minOrder, setMinOrder] = useState(existing?.minOrderValueCents ?? 0)
  const [currency, setCurrency] = useState(existing?.currency ?? 'EUR')

  const submit = () => {
    if (existing) onUpdate(existing.id, { name, contactEmail: email || null, contactPhone: phone || null, notes: notes || null, leadTimeDays: leadTime, minOrderValueCents: minOrder, currency })
    else onCreate({ code, name, contactEmail: email || null, contactPhone: phone || null, notes: notes || null, leadTimeDays: leadTime, minOrderValueCents: minOrder, currency })
  }

  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <h3>{existing ? t('purchasing:suppliers.form.titleEdit', { code: existing.code }) : t('purchasing:suppliers.form.titleNew')}</h3>
      <div className="grid-3">
        <label>{t('purchasing:suppliers.col.code')} <input value={code} onChange={(e) => setCode(e.target.value)} disabled={!!existing} /></label>
        <label>{t('purchasing:suppliers.col.name')} <input value={name} onChange={(e) => setName(e.target.value)} /></label>
        <label>{t('purchasing:suppliers.col.currency')} <input value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())} maxLength={3} /></label>
        <label>{t('purchasing:suppliers.form.email')} <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
        <label>{t('purchasing:suppliers.form.phone')} <input value={phone} onChange={(e) => setPhone(e.target.value)} /></label>
        <label>{t('purchasing:suppliers.form.leadTime')} <input type="number" min={0} value={leadTime} onChange={(e) => setLeadTime(+e.target.value)} /></label>
        <label>{t('purchasing:suppliers.form.minOrder')} <input type="number" min={0} value={minOrder} onChange={(e) => setMinOrder(+e.target.value)} /></label>
      </div>
      <label style={{ marginTop: 8 }}>{t('purchasing:suppliers.form.notes')}<textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} /></label>
      <div className="toolbar" style={{ marginTop: 12 }}>
        <button className="primary" onClick={submit} disabled={pending || !name || !code}>
          {pending ? t('common:saving') : t('common:save')}
        </button>
        <button onClick={onCancel}>{t('common:cancel')}</button>
      </div>
      <ErrorBanner error={error} title={t('purchasing:suppliers.form.saveFailed')} onDismiss={onDismissError} />
    </div>
  )
}
