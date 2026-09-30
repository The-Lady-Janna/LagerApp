import { useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useAbcAnalysis, useBinHeatmap, useDeadStock, useLiveStatus, usePickerPerformance, useReportDashboard, useSlottingSuggestions, useStockValuation } from '../api/hooks'
import type { DailySeriesPointDto } from '../api/types'
import { LoadState } from '../components/LoadState'
import { StatusPill } from '../components/StatusPill'
import { ExpiringStockCard } from '../features/traceability/ExpiringStockCard'
import { formatDateTime, formatNumber } from '../lib/format'
import { kpiHint, kpiLabel } from '../lib/kpiText'

const RANGES = [1, 7, 30, 90]

export function ReportsPage() {
  const { t } = useTranslation()
  const [range, setRange] = useState(7)
  const { data, isLoading, error, refetch } = useReportDashboard(range)
  const live = useLiveStatus(10000)
  const dead = useDeadStock(90)
  const abc = useAbcAnalysis(range)
  const heat = useBinHeatmap(range)
  const valuation = useStockValuation()
  const slotting = useSlottingSuggestions(range)
  const pickers = usePickerPerformance(range)

  return (
    <>
      <h2>{t('reports:title')}</h2>

      {/* Live-Status-Strip — Polling alle 10s. Zeigt sofort kritische Counts. */}
      {valuation.data && (
        <div className="card report-banner" style={{ marginBottom: 12 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
            <div>
              <div style={{ fontSize: 12, opacity: 0.7 }}>{t('reports:valuation.title')}</div>
              <div style={{ fontSize: 32, fontWeight: 700 }}>
                {formatNumber(valuation.data.totalValueCents / 100, { minimumFractionDigits: 2 })} {valuation.data.currency}
              </div>
            </div>
            <div style={{ textAlign: 'right', fontSize: 12, opacity: 0.7 }}>
              {t('reports:valuation.summary', { articles: valuation.data.articleCount, quantity: formatNumber(valuation.data.totalQuantity), at: formatDateTime(valuation.data.at) })}
            </div>
          </div>
        </div>
      )}

      {live.data && (
        <div className="live-strip">
          <LiveTile label={t('reports:live.ordersOpen')} value={live.data.ordersOpen} />
          <LiveTile label={t('reports:live.inPicking')} value={live.data.ordersInPicking} accent="info" />
          <LiveTile label={t('reports:live.readyPack')} value={live.data.picklistsPickedReadyToPack} accent="warning" />
          <LiveTile label={t('reports:live.doneToday')} value={live.data.picklistsCompletedToday} accent="success" />
          <LiveTile label={t('reports:live.inventoryOpen')} value={live.data.inventoryCountsOpen} />
          <LiveTile label={t('reports:live.replenOpen')} value={live.data.replenishmentTasksOpen} />
          <LiveTile label={t('reports:live.critical')} value={live.data.stockAlertsCritical} accent={live.data.stockAlertsCritical > 0 ? 'danger' : undefined} />
          <LiveTile label={t('reports:live.warning')} value={live.data.stockAlertsWarning} accent={live.data.stockAlertsWarning > 0 ? 'warning' : undefined} />
        </div>
      )}

      {/* MHD-Warnliste: unabhängig vom Zeitraum-Filter der Kennzahlen (eigene Frist 7/30/90 Tage) */}
      <ExpiringStockCard />

      <div className="toolbar" style={{ marginBottom: 12 }}>
        <span className="muted">{t('reports:range.label')}</span>
        {RANGES.map((value) => (
          <button
            key={value}
            onClick={() => setRange(value)}
            className={range === value ? 'primary' : ''}
          >
            {t(`reports:range.d${value}`)}
          </button>
        ))}
        {data && (
          <span className="muted" style={{ marginLeft: 'auto', fontSize: 12 }}>
            {formatDateTime(data.from)} → {formatDateTime(data.to)}
          </span>
        )}
      </div>

      {!data && <LoadState isLoading={isLoading} error={error} what={t('reports:what')} onRetry={() => void refetch()} />}

      {data && (
        <>
          {/* Tile grid for headline KPIs */}
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
              gap: 12,
              marginBottom: 16,
            }}
          >
            {data.headline.map((kpi) => (
              <div key={kpi.label} className="card" style={{ padding: 16 }}>
                <div className="muted" style={{ fontSize: 12, marginBottom: 6 }}>{kpiLabel(kpi.label)}</div>
                <div style={{ fontSize: 28, fontWeight: 600, lineHeight: 1.1 }}>
                  {kpi.value}
                  {kpi.unit && <span className="muted" style={{ fontSize: 14, marginLeft: 4 }}>{kpi.unit}</span>}
                </div>
                {kpi.hint && <div className="muted" style={{ fontSize: 11, marginTop: 4 }}>{kpiHint(kpi.hint)}</div>}
              </div>
            ))}
          </div>

          <div className="grid-auto" style={{ gap: 12 }}>
            <div className="card">
              <h3>{t('reports:perDay.pickLists')}</h3>
              <Sparkline series={data.pickListsPerDay} color="var(--c-link)" label={t('reports:perDay.pickLists')} />
              <SeriesLegend series={data.pickListsPerDay} />
            </div>

            <div className="card">
              <h3>{t('reports:perDay.orders')}</h3>
              <Sparkline series={data.ordersPerDay} color="var(--c-success)" label={t('reports:perDay.orders')} />
              <SeriesLegend series={data.ordersPerDay} />
            </div>

            <div className="card">
              <h3>{t('reports:byStatus.title')}</h3>
              {data.orderStatusBreakdown.length === 0 ? (
                <p className="muted">{t('reports:byStatus.none')}</p>
              ) : (
                <table style={{ width: '100%' }}>
                  <tbody>
                    {data.orderStatusBreakdown.map((b) => (
                      <tr key={b.status}>
                        <td>{t(`status:order.${b.status}`, { defaultValue: b.status })}</td>
                        <td style={{ textAlign: 'right' }}>{b.count}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            <div className="card">
              <h3>{t('reports:topArticles.title')}</h3>
              {data.topArticles.length === 0 ? (
                <p className="muted">{t('reports:topArticles.none')}</p>
              ) : (
                <table style={{ width: '100%' }}>
                  <thead>
                    <tr>
                      <th scope="col">{t('reports:col.sku')}</th>
                      <th scope="col">{t('reports:col.article')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:col.picks')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:col.sumQuantity')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.topArticles.map((a) => (
                      <tr key={a.articleId}>
                        <td><code>{a.sku}</code></td>
                        <td>{a.name}</td>
                        <td style={{ textAlign: 'right' }}>{a.pickCount}</td>
                        <td style={{ textAlign: 'right' }}>{a.totalQuantity}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            <div className="card">
              <h3>{t('reports:topBins.title')}</h3>
              {data.topBins.length === 0 ? (
                <p className="muted">{t('reports:topBins.none')}</p>
              ) : (
                <table style={{ width: '100%' }}>
                  <thead>
                    <tr>
                      <th scope="col">{t('reports:col.bin')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:col.article')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:col.sumQuantity')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.topBins.map((b) => (
                      <tr key={b.binId}>
                        <td><code>{b.binCode}</code></td>
                        <td style={{ textAlign: 'right' }}>{b.articleCount}</td>
                        <td style={{ textAlign: 'right' }}>{b.totalQuantity}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            <div className="card">
              <h3>{t('reports:abc.title', { range })}</h3>
              <p className="muted" style={{ fontSize: 12 }}>
                {t('reports:abc.hint')}
              </p>
              {abc.data && abc.data.length === 0 && <p className="muted">{t('reports:noPicks')}</p>}
              {abc.data && abc.data.length > 0 && (
                <table style={{ width: '100%' }}>
                  <thead>
                    <tr>
                      <th scope="col">{t('reports:abc.class')}</th>
                      <th scope="col">{t('reports:col.sku')}</th>
                      <th scope="col">{t('reports:col.article')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:abc.share')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {abc.data.slice(0, 15).map((a) => (
                      <tr key={a.articleId}>
                        <td>
                          <StatusPill status={a.class} tone={a.class === 'A' ? 'success' : a.class === 'B' ? 'warning' : 'neutral'} />
                        </td>
                        <td><code>{a.sku}</code></td>
                        <td>{a.name}</td>
                        <td style={{ textAlign: 'right' }}>{a.sharePercent.toFixed(1)} %</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            <div className="card">
              <h3>{t('reports:heat.title', { range })}</h3>
              <p className="muted" style={{ fontSize: 12 }}>
                {t('reports:heat.hint')}
              </p>
              {heat.data && heat.data.length === 0 && <p className="muted">{t('reports:noPicks')}</p>}
              {heat.data && heat.data.length > 0 && (() => {
                const maxCount = Math.max(...heat.data.map((h) => h.pickCount))
                return (
                  <table style={{ width: '100%' }}>
                    <tbody>
                      {heat.data.slice(0, 15).map((h) => {
                        const intensity = Math.round((h.pickCount / maxCount) * 100)
                        // Heatmap-Farbe: rot bei hoch, gelb mittel, blau niedrig.
                        const hue = 240 - (intensity * 2.4)  // 240 (blau) → 0 (rot)
                        return (
                          <tr key={h.binId}>
                            <td style={{ width: 80 }}><code>{h.binCode}</code></td>
                            <td style={{ padding: '4px 0' }}>
                              <div style={{
                                background: `hsl(${hue}, 70%, 55%)`,
                                width: `${intensity}%`,
                                height: 16,
                                borderRadius: 3,
                                minWidth: 4,
                              }} />
                            </td>
                            <td style={{ width: 50, textAlign: 'right', fontWeight: 600 }}>{h.pickCount}</td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                )
              })()}
            </div>

            <div className="card">
              <h3>{t('reports:pickers.title', { range })}</h3>
              <p className="muted" style={{ fontSize: 12 }}>
                {t('reports:pickers.hint')}
              </p>
              {pickers.isLoading && <p className="muted">{t('reports:pickers.computing')}</p>}
              {pickers.data && pickers.data.rows.length === 0 && (
                <p className="muted">{t('reports:pickers.none')}</p>
              )}
              {pickers.data && pickers.data.rows.length > 0 && (
                <table style={{ width: '100%' }}>
                  <thead>
                    <tr>
                      <th scope="col">{t('reports:pickers.picker')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:pickers.pickLists')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:pickers.items')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:pickers.avgDistance')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:pickers.avgDuration')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {pickers.data.rows.map((r) => (
                      <tr key={r.picker}>
                        <td><strong>{r.picker}</strong></td>
                        <td style={{ textAlign: 'right', fontWeight: 600 }}>{r.pickListsCompleted}</td>
                        <td style={{ textAlign: 'right' }}>{r.itemsPicked}</td>
                        <td style={{ textAlign: 'right' }}>{r.avgDistanceMeters.toFixed(1)} m</td>
                        <td style={{ textAlign: 'right' }}>
                          {r.avgDurationMinutes !== null
                            ? `${r.avgDurationMinutes.toFixed(1)} min`
                            : <span className="muted">—</span>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            <div className="card">
              <h3>{t('reports:slotting.title')}</h3>
              <p className="muted" style={{ fontSize: 12 }}>
                {t('reports:slotting.hint')}
              </p>
              {slotting.data && slotting.data.length === 0 && <p className="muted">{t('reports:slotting.none')}</p>}
              {slotting.data && slotting.data.length > 0 && (
                <table style={{ width: '100%' }}>
                  <thead>
                    <tr>
                      <th scope="col">{t('reports:col.sku')}</th><th scope="col">{t('reports:slotting.current')}</th><th scope="col">{t('reports:slotting.suggested')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:col.picks')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:slotting.savings')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {slotting.data.slice(0, 10).map((s) => (
                      <tr key={s.articleId}>
                        <td><code>{s.articleSku}</code></td>
                        <td><code>{s.currentBinCode}</code> ({(s.currentDistanceMm / 1000).toFixed(1)} m)</td>
                        <td><code>{s.suggestedBinCode}</code> ({(s.suggestedDistanceMm / 1000).toFixed(1)} m)</td>
                        <td style={{ textAlign: 'right' }}>{s.pickFrequency}</td>
                        <td className="text-success" style={{ textAlign: 'right', fontWeight: 600 }}>
                          {(s.estimatedSavingsMm / 1000).toFixed(1)} m
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

            <div className="card">
              <h3>{t('reports:dead.title')}</h3>
              <p className="muted" style={{ fontSize: 12 }}>
                {t('reports:dead.hint')}
              </p>
              {dead.data && dead.data.length === 0 && <p className="muted">{t('reports:dead.none')}</p>}
              {dead.data && dead.data.length > 0 && (
                <table style={{ width: '100%' }}>
                  <thead>
                    <tr>
                      <th scope="col">{t('reports:col.sku')}</th>
                      <th scope="col">{t('reports:col.article')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:dead.stock')}</th>
                      <th scope="col" style={{ textAlign: 'right' }}>{t('reports:dead.days')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {dead.data.slice(0, 15).map((a) => (
                      <tr key={a.articleId}>
                        <td><code>{a.sku}</code></td>
                        <td>{a.name}</td>
                        <td style={{ textAlign: 'right' }}>{a.totalQuantity}</td>
                        <td className="text-danger" style={{ textAlign: 'right', fontWeight: 600 }}>
                          {a.daysSinceLastMovement ?? '∞'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </div>
        </>
      )}
    </>
  )
}

function LiveTile({ label, value, accent }: { label: string; value: number; accent?: 'info' | 'warning' | 'success' | 'danger' }) {
  return (
    <div className={accent ? `live-tile live-tile--${accent}` : 'live-tile'}>
      <div className="live-tile-label">{label}</div>
      <div className="live-tile-value">{value}</div>
    </div>
  )
}

// Inline SVG sparkline — avoids pulling Recharts (~80 kB) just for two charts.
function Sparkline({ series, color, label }: { series: DailySeriesPointDto[]; color: string; label: string }) {
  const { t } = useTranslation()
  if (series.length === 0) return <p className="muted">{t('reports:noData')}</p>
  const w = 360
  const h = 80
  const pad = 6
  const max = Math.max(1, ...series.map((p) => p.count))
  const stepX = series.length > 1 ? (w - pad * 2) / (series.length - 1) : 0
  const pts = series.map((p, i) => {
    const x = pad + i * stepX
    const y = h - pad - (p.count / max) * (h - pad * 2)
    return [x, y] as const
  })
  const path = pts.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ')
  const area =
    `M${pts[0][0]},${h - pad} ` +
    pts.map(([x, y]) => `L${x.toFixed(1)},${y.toFixed(1)}`).join(' ') +
    ` L${pts[pts.length - 1][0]},${h - pad} Z`

  const total = series.reduce((s, p) => s + p.count, 0)
  return (
    <svg viewBox={`0 0 ${w} ${h}`} style={{ width: '100%', height: h }} role="img" aria-label={t('reports:sparklineAria', { label, count: series.length, total })}>
      <title>{label}</title>
      <path d={area} style={{ fill: color }} opacity={0.15} />
      <path d={path} style={{ stroke: color }} strokeWidth={2} fill="none" />
      {pts.map(([x, y], i) => (
        <circle key={i} cx={x} cy={y} r={2.5} style={{ fill: color }} />
      ))}
    </svg>
  )
}

function SeriesLegend({ series }: { series: DailySeriesPointDto[] }) {
  const { t } = useTranslation()
  const total = series.reduce((s, p) => s + p.count, 0)
  const max = series.reduce((m, p) => (p.count > m.count ? p : m), { date: '', count: 0 })
  return (
    <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>
      <Trans i18nKey="reports:legend.summary" values={{ total, max: max.count }} components={{ strong: <strong /> }} />
      {max.date && t('reports:legend.peakOn', { date: max.date })}
    </div>
  )
}
