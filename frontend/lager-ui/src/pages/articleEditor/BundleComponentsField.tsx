import { useId, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { ArticleDto, BundleComponentRequest } from '../../api/types'
import { matchesArticleSearch } from './articleSearch'
import { checkBundleComponent } from './bundle'
import { MAX_COMPONENT_QUANTITY } from './formState'

interface Props {
  /** Id des bearbeiteten Artikels; beim Neuanlegen undefined. */
  articleId: string | undefined
  rows: readonly BundleComponentRequest[]
  onChange: (next: BundleComponentRequest[]) => void
  /** Alle Artikel (Stand des Servers); undefined = noch nicht geladen. */
  articles: readonly ArticleDto[] | undefined
  /** Fehlermeldung zu den Mengen. */
  error?: string
}

/** So viele Suchtreffer zeigt die Liste höchstens (der Rest wird mit "weitere" angedeutet). */
const MAX_RESULTS = 8

/// <summary>
/// Bundle-Komponenten: Artikelsuche (SKU, Name, GTIN) plus Menge je Komponente. Ein Artikel mit mindestens einer Komponente ist ein
/// Bundle; er hat keinen eigenen Bestand, beim Kommissionieren wird er in seine Komponenten aufgelöst.
/// Ungültige Komponenten werden in der Trefferliste gesperrt und begründet: der Artikel selbst, schon enthalten oder ein Zyklus
/// (A enthält B enthält A, auch über mehrere Ebenen).
/// </summary>
export function BundleComponentsField({ articleId, rows, onChange, articles, error }: Props) {
  const { t } = useTranslation()
  const searchId = useId()
  const errorId = useId()
  const [query, setQuery] = useState('')

  const byId = new Map((articles ?? []).map((a) => [a.id, a]))
  const term = query.trim()
  const matches = term === '' ? [] : (articles ?? []).filter((a) => matchesArticleSearch(a, term))

  const add = (componentId: string) => {
    onChange([...rows, { componentArticleId: componentId, quantity: 1 }])
    setQuery('')
  }

  const setQuantity = (componentId: string, quantity: number) =>
    onChange(rows.map((r) => (r.componentArticleId === componentId ? { ...r, quantity } : r)))

  return (
    <div>
      {rows.length === 0 ? (
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          {t('articles:bundle.none')}
        </p>
      ) : (
        <table aria-label={t('articles:bundle.tableAria')}>
          <thead>
            <tr><th>{t('articles:bundle.colComponent')}</th><th>{t('articles:bundle.colQuantity')}</th><th></th></tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const article = byId.get(row.componentArticleId)
              const sku = article?.sku ?? row.componentArticleId
              return (
                <tr key={row.componentArticleId}>
                  <td><code>{sku}</code>{article && <> — {article.name}</>}</td>
                  <td>
                    <input
                      type="number"
                      min={1}
                      max={MAX_COMPONENT_QUANTITY}
                      step={1}
                      value={row.quantity}
                      aria-label={t('articles:bundle.quantityAria', { sku })}
                      aria-invalid={error !== undefined && (!Number.isInteger(row.quantity) || row.quantity < 1)}
                      onChange={(e) => setQuantity(row.componentArticleId, +e.target.value)}
                      style={{ width: 110 }}
                    />
                  </td>
                  <td>
                    <button type="button" onClick={() => onChange(rows.filter((r) => r.componentArticleId !== row.componentArticleId))}
                      aria-label={t('articles:removeAria', { sku })}>
                      {t('common:remove')}
                    </button>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
      {error && <p id={errorId} role="alert" style={{ color: 'var(--c-danger)', fontSize: 12, margin: '4px 0 0' }}>{error}</p>}

      <div style={{ marginTop: 12 }}>
        <label htmlFor={searchId}>{t('articles:bundle.search')}</label>
        <input
          id={searchId}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder={articles ? t('articles:bundle.searchPlaceholder') : t('articles:bundle.loadingArticles')}
          disabled={!articles}
          autoComplete="off"
          style={{ marginTop: 4, width: '100%' }}
        />
        {term !== '' && (
          matches.length === 0 ? (
            <p className="muted" style={{ fontSize: 12, margin: '6px 0 0' }}>{t('articles:bundle.noHits')}</p>
          ) : (
            <ul aria-label={t('articles:bundle.hits')} style={{ listStyle: 'none', padding: 0, margin: '6px 0 0' }}>
              {matches.slice(0, MAX_RESULTS).map((article) => {
                const check = checkBundleComponent({
                  articleId,
                  componentId: article.id,
                  existing: rows.map((r) => r.componentArticleId),
                  articles: articles ?? [],
                })
                return (
                  <li key={article.id} className="row" style={{ justifyContent: 'space-between', padding: '4px 0', borderBottom: '1px solid var(--c-border-light)' }}>
                    <span>
                      <code>{article.sku}</code> — {article.name}
                      {!check.ok && <span className="muted" style={{ fontSize: 12 }}> ({check.message})</span>}
                    </span>
                    <button type="button" onClick={() => add(article.id)} disabled={!check.ok} aria-label={t('articles:bundle.addAria', { sku: article.sku })}>
                      {t('common:add')}
                    </button>
                  </li>
                )
              })}
              {matches.length > MAX_RESULTS && (
                <li className="muted" style={{ fontSize: 12, padding: '4px 0' }}>{t('articles:bundle.more', { count: matches.length - MAX_RESULTS })}</li>
              )}
            </ul>
          )
        )}
      </div>
    </div>
  )
}
