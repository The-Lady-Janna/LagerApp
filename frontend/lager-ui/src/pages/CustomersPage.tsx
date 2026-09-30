import { useId, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useAddCustomerAddress, useCreateCustomer, useCustomers, useUpdateCustomer } from '../api/hooks'
import type { CustomerDto } from '../api/types'
import { ErrorBanner } from '../components/ErrorBanner'
import { LoadState } from '../components/LoadState'

export function CustomersPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useCustomers(true)
  const create = useCreateCustomer()
  const update = useUpdateCustomer()
  const addAddress = useAddCustomerAddress()

  const [showForm, setShowForm] = useState(false)
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [expanded, setExpanded] = useState<string | null>(null)

  return (
    <>
      <h2>{t('customers:title')}</h2>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <button className="primary" onClick={() => setShowForm(!showForm)}>
          {showForm ? t('common:cancel') : t('customers:newButton')}
        </button>
      </div>

      {showForm && (
        <div className="card" style={{ marginBottom: 16 }}>
          <h3>{t('customers:new.title')}</h3>
          <div className="grid-2">
            <label>{t('customers:code')} <input value={code} onChange={(e) => setCode(e.target.value)} /></label>
            <label>{t('customers:name')} <input value={name} onChange={(e) => setName(e.target.value)} /></label>
          </div>
          <div className="toolbar" style={{ marginTop: 8 }}>
            <button className="primary" disabled={create.isPending || !code || !name}
              onClick={async () => {
                try {
                  await create.mutateAsync({ code, name })
                  setCode(''); setName(''); setShowForm(false)
                } catch {
                  /* Fehler steht unter dem Formular, die Eingaben bleiben erhalten */
                }
              }}>
              {create.isPending ? t('common:saving') : t('customers:create')}
            </button>
          </div>
          <ErrorBanner error={create.error} title={t('customers:createFailed')} fallback={t('customers:createFailedFallback')} onDismiss={create.reset} style={{ marginTop: 8 }} />
        </div>
      )}

      <ErrorBanner error={update.error} title={t('customers:saveFailed')} fallback={t('customers:saveFailedFallback')} onDismiss={update.reset} />
      <ErrorBanner error={addAddress.error} title={t('customers:addressFailed')} fallback={t('customers:addressFailedFallback')} onDismiss={addAddress.reset} />

      <LoadState isLoading={isLoading} error={error} hasData={data !== undefined} what={t('customers:what')} onRetry={() => void refetch()}>
        {data?.map((c) => (
          <CustomerRow
            key={c.id}
            customer={c}
            expanded={expanded === c.id}
            onToggle={() => setExpanded(expanded === c.id ? null : c.id)}
            onUpdate={(req) => update.mutate({ id: c.id, req })}
            onAddAddress={(req) => addAddress.mutate({ id: c.id, req })}
          />
        ))}
        {data?.length === 0 && <p className="muted">{t('customers:none')}</p>}
      </LoadState>
    </>
  )
}

function CustomerRow({ customer, expanded, onToggle, onUpdate, onAddAddress }: {
  customer: CustomerDto
  expanded: boolean
  onToggle: () => void
  onUpdate: (req: import('../api/types').UpdateCustomerRequest) => void
  onAddAddress: (req: import('../api/types').AddAddressRequest) => void
}) {
  const { t } = useTranslation()
  const [name, setName] = useState(customer.name)
  const [email, setEmail] = useState(customer.email ?? '')
  const [phone, setPhone] = useState(customer.phone ?? '')
  const [notes, setNotes] = useState(customer.notes ?? '')
  const [discount, setDiscount] = useState(customer.defaultDiscountPercent)
  const [currency, setCurrency] = useState(customer.currency)

  // Address form
  const [addrLabel, setAddrLabel] = useState('')
  const [addrKind, setAddrKind] = useState<'Shipping' | 'Billing' | 'Both'>('Shipping')
  const [street, setStreet] = useState('')
  const [zip, setZip] = useState('')
  const [city, setCity] = useState('')
  const [country, setCountry] = useState('DE')
  const detailsId = useId()

  return (
    <div className="card" style={{ marginBottom: 8, opacity: customer.isActive ? 1 : 0.5 }}>
      {/* Der Kopf ist ein echter Button (Tastatur, Screenreader) mit aria-expanded statt eines klickbaren div. */}
      <button type="button" className="row-toggle" onClick={onToggle} aria-expanded={expanded} aria-controls={expanded ? detailsId : undefined}>
        <span>
          <strong><code>{customer.code}</code> — {customer.name}</strong>
          <span className="muted" style={{ display: 'block', fontSize: 12 }}>{t('customers:addresses', { count: customer.addresses.length })}</span>
        </span>
        <span aria-hidden="true">{expanded ? '▲' : '▼'}</span>
      </button>

      {expanded && (
        <div id={detailsId} style={{ marginTop: 12 }}>
          <div className="grid-3">
            <label>{t('customers:name')} <input value={name} onChange={(e) => setName(e.target.value)} /></label>
            <label>{t('customers:email')} <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
            <label>{t('customers:phone')} <input value={phone} onChange={(e) => setPhone(e.target.value)} /></label>
            <label>{t('customers:currency')} <input value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())} maxLength={3} /></label>
            <label>{t('customers:discount')} <input type="number" min={0} max={100} value={discount} onChange={(e) => setDiscount(+e.target.value)} /></label>
          </div>
          <label style={{ marginTop: 8 }}>{t('customers:notes')} <textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} /></label>
          <button className="primary" style={{ marginTop: 8 }}
            onClick={() => onUpdate({ name, email: email || null, phone: phone || null, notes: notes || null, currency, defaultDiscountPercent: discount })}>
            {t('common:save')}
          </button>

          <h4 style={{ marginTop: 16 }}>{t('customers:addressesTitle')}</h4>
          <table style={{ width: '100%' }}>
            <caption className="visually-hidden">{t('customers:caption', { name: customer.name })}</caption>
            <thead><tr><th scope="col">{t('customers:kind.label')}</th><th scope="col">{t('customers:label')}</th><th scope="col">{t('customers:address')}</th></tr></thead>
            <tbody>
              {customer.addresses.map((a) => (
                <tr key={a.id}>
                  <td>{t(`customers:kind.${a.kind}`, { defaultValue: a.kind })}</td>
                  <td>{a.label}</td>
                  <td>{a.street}{a.street2 ? `, ${a.street2}` : ''}, {a.zip} {a.city}, {a.country}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <h5 style={{ marginTop: 12 }}>{t('customers:newAddress')}</h5>
          <div className="grid-3">
            <label>{t('customers:kind.label')}
              <select value={addrKind} onChange={(e) => setAddrKind(e.target.value as 'Shipping' | 'Billing' | 'Both')}>
                <option value="Shipping">{t('customers:kind.Shipping')}</option>
                <option value="Billing">{t('customers:kind.Billing')}</option>
                <option value="Both">{t('customers:kind.Both')}</option>
              </select>
            </label>
            <label>{t('customers:label')} <input value={addrLabel} onChange={(e) => setAddrLabel(e.target.value)} placeholder={t('customers:labelPlaceholder')} /></label>
            <label>{t('customers:country')} <input value={country} onChange={(e) => setCountry(e.target.value.toUpperCase())} maxLength={3} /></label>
            <label>{t('customers:street')} <input value={street} onChange={(e) => setStreet(e.target.value)} /></label>
            <label>{t('customers:zip')} <input value={zip} onChange={(e) => setZip(e.target.value)} /></label>
            <label>{t('customers:city')} <input value={city} onChange={(e) => setCity(e.target.value)} /></label>
          </div>
          <button style={{ marginTop: 8 }} disabled={!addrLabel || !street}
            onClick={() => {
              onAddAddress({ kind: addrKind, label: addrLabel, street, street2: null, zip, city, country })
              setAddrLabel(''); setStreet(''); setZip(''); setCity('')
            }}>
            {t('customers:addAddress')}
          </button>
        </div>
      )}
    </div>
  )
}
