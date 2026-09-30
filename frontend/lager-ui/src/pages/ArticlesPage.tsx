import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useArticles } from '../api/hooks'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { LoadState } from '../components/LoadState'
import { BundleBadge, SeasonBadge } from './articleEditor/ArticleBadges'
import { hasSeason, isArticleActive, matchesArticleSearch, matchesStatusFilter, type ArticleStatusFilter } from './articleEditor/articleSearch'

const STATUS_FILTERS: readonly ArticleStatusFilter[] = ['all', 'active', 'inactive']

export function ArticlesPage() {
  const { t } = useTranslation()
  const { data, isLoading, error, refetch } = useArticles()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<ArticleStatusFilter>('all')

  if (!data) return <LoadState isLoading={isLoading} error={error} what={t('articles:what')} onRetry={() => void refetch()} />

  // Suche über alle Kennungen (Name, SKU, Alternativ-SKU, GTIN) — dieselbe Regel wie GET /api/articles?search=.
  const now = new Date()
  const visible = data.filter((a) => matchesArticleSearch(a, search) && matchesStatusFilter(a, status, now))
  const filtered = search.trim() !== '' || status !== 'all'
  const skuById = new Map(data.map((a) => [a.id, a.sku] as const))

  return (
    <>
      <h2>{t('articles:title')}</h2>
      <div className="toolbar">
        <Link to="/articles/new"><button className="primary">{t('articles:new')}</button></Link>
        <input
          type="search"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder={t('articles:searchPlaceholder')}
          aria-label={t('articles:searchAria')}
          style={{ minWidth: 280 }}
        />
        <label className="row" style={{ gap: 6 }}>
          {t('articles:statusLabel')}
          <select value={status} onChange={(e) => setStatus(e.target.value as ArticleStatusFilter)}>
            {STATUS_FILTERS.map((key) => (
              <option key={key} value={key}>{t(`articles:filter.${key}`)}</option>
            ))}
          </select>
        </label>
      </div>
      <CollapsibleCard
        title={filtered ? t('articles:cardTitleFiltered', { visible: visible.length, total: data.length }) : t('articles:cardTitle', { count: data.length })}
        storageKey="articles-list"
        defaultOpen
      >
        <table>
          <thead>
            <tr>
              <th>{t('articles:col.sku')}</th>
              <th>{t('articles:col.name')}</th>
              <th>{t('articles:col.gtin')}</th>
              <th>{t('articles:col.dimensions')}</th>
              <th>{t('articles:col.weight')}</th>
              <th>{t('articles:col.stackable')}</th>
              <th>{t('articles:col.flags')}</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {visible.map((a) => (
              <tr key={a.id}>
                <td><code>{a.sku}</code></td>
                <td>{a.name}</td>
                <td>{a.gtin ? <code>{a.gtin}</code> : <span className="muted">—</span>}</td>
                <td>{a.dimensions.lengthMm} × {a.dimensions.widthMm} × {a.dimensions.heightMm}</td>
                <td>{a.weightGrams}</td>
                <td>
                  {a.stacking.isStackable
                    ? t('articles:stackInfo', { axis: a.stacking.stackingAxis, increment: a.stacking.stackingIncrementMm })
                    : <span className="muted">{t('common:no')}</span>}
                </td>
                <td>
                  <span className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                    <BundleBadge article={a} skuById={skuById} />
                    {hasSeason(a) && <SeasonBadge active={isArticleActive(a, now)} validFrom={a.validFrom} validUntil={a.validUntil} />}
                  </span>
                </td>
                <td><Link to={`/articles/${a.id}`}>{t('common:edit')}</Link></td>
              </tr>
            ))}
            {visible.length === 0 && (
              <tr>
                <td colSpan={8} className="muted">
                  {data.length === 0 ? t('articles:empty.none') : t('articles:empty.filtered')}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </CollapsibleCard>
    </>
  )
}
