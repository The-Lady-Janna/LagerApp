import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { EXPIRING_DAY_OPTIONS, useExpiringStock } from '../../api/lotHooks'
import { LoadState } from '../../components/LoadState'
import { formatDateOnly } from '../../lib/format'
import { ExpiryPill } from './ExpiryPill'
import { describeDays, traceabilityPath } from './expiry'

/** So viele Zeilen zeigt die Karte höchstens; der Rest steht in der Zählzeile ("… und n weitere"). */
const MAX_ROWS = 50

/// <summary>
/// Karte "Ablaufende Chargen" (Reports): abgelaufene und innerhalb der gewählten Frist (7/30/90 Tage) ablaufende
/// Bestandszeilen aus GET /api/reports/expiring — mit Lagerplatz, Menge, Tagen und Status. Die Chargennummer führt zur
/// Rückverfolgung (/traceability).
/// </summary>
export function ExpiringStockCard() {
  const { t } = useTranslation()
  const [days, setDays] = useState<number>(30)
  const expiring = useExpiringStock(days)
  const rows = expiring.data ?? []
  const expired = rows.filter((r) => r.status === 'Expired').length
  const critical = rows.filter((r) => r.status === 'Critical').length
  const soon = rows.filter((r) => r.status === 'Soon').length

  return (
    <section className="card" style={{ marginBottom: 12 }} aria-labelledby="expiring-title">
      <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
        <h3 id="expiring-title" style={{ margin: 0 }}>{t('traceability:expiring.title')}</h3>
        <div className="toolbar" style={{ marginBottom: 0 }} role="group" aria-label={t('traceability:expiring.periodAria')}>
          <span className="muted">{t('traceability:expiring.period')}</span>
          {EXPIRING_DAY_OPTIONS.map((d) => (
            <button key={d} type="button" className={days === d ? 'primary' : ''} aria-pressed={days === d} onClick={() => setDays(d)}>
              {t('traceability:expiring.days', { count: d })}
            </button>
          ))}
        </div>
      </div>
      <p className="muted" style={{ fontSize: 12 }}>
        {t('traceability:expiring.hint', { days })}
      </p>

      {!expiring.data && <LoadState isLoading={expiring.isLoading} error={expiring.error} what={t('traceability:expiring.what')} onRetry={() => void expiring.refetch()} />}

      {expiring.data && rows.length === 0 && <p className="muted" role="status">{t('traceability:expiring.none', { days })}</p>}

      {rows.length > 0 && (
        <>
          <p style={{ fontSize: 13 }} role="status">
            <Trans
              i18nKey="traceability:expiring.summary"
              values={{ rows: rows.length, expired, critical, soon }}
              components={{ strong: <strong />, expired: <span className="text-danger" />, critical: <span className="text-warning" /> }}
            />
          </p>
          <div className="table-wrap">
            <table style={{ width: '100%' }}>
              <caption className="visually-hidden">{t('traceability:expiring.caption')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('traceability:col.status')}</th><th scope="col">{t('traceability:col.article')}</th><th scope="col">{t('traceability:col.location')}</th><th scope="col">{t('traceability:col.lot')}</th>
                  <th scope="col">{t('traceability:col.expiry')}</th><th scope="col">{t('traceability:expiring.colExpiry')}</th><th scope="col" style={{ textAlign: 'right' }}>{t('traceability:col.quantity')}</th>
                </tr>
              </thead>
              <tbody>
                {rows.slice(0, MAX_ROWS).map((r) => (
                  <tr key={r.stockItemId}>
                    <td><ExpiryPill expiryDate={r.expiryDate} status={r.status} days={r.daysUntilExpiry} /></td>
                    <td><code>{r.articleSku}</code> {r.articleName}</td>
                    <td><code>{r.binCode}</code></td>
                    <td>
                      {r.lotNumber
                        ? <Link to={traceabilityPath(r.lotNumber)} title={t('traceability:openTrace')}>{r.lotNumber}</Link>
                        : <span className="muted">—</span>}
                    </td>
                    <td>{formatDateOnly(r.expiryDate)}</td>
                    <td className={r.status === 'Expired' ? 'text-danger' : undefined}>{describeDays(r.daysUntilExpiry)}</td>
                    <td style={{ textAlign: 'right', fontWeight: 600 }}>{r.quantity}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {rows.length > MAX_ROWS && <p className="muted" style={{ fontSize: 12 }}>{t('traceability:expiring.more', { count: rows.length - MAX_ROWS })}</p>}
        </>
      )}
    </section>
  )
}
