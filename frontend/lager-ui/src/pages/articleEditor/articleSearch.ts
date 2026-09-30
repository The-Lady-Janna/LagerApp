import type { ArticleDto } from '../../api/types'
import { hasSeasonWindow, isInSeason } from './season'

/// <summary>
/// Suche und Statusfilter der Artikelliste. Die Suche spiegelt Article.MatchesSearch auf dem Server (GET /api/articles?search=):
/// Teilstring in Name, SKU, Alternativ-SKU oder GTIN, Groß-/Kleinschreibung egal; die GTIN wird ohne Leerraum verglichen
/// ("4006 3813" findet "4006381333931"). Leerer Suchtext trifft alles.
/// </summary>
export function matchesArticleSearch(article: ArticleDto, term: string): boolean {
  const text = term.trim().toLocaleLowerCase('de-DE')
  if (text === '') return true

  if (article.name.toLocaleLowerCase('de-DE').includes(text)) return true
  if (article.sku.toLocaleLowerCase('de-DE').includes(text)) return true
  if ((article.alternativeSkus ?? []).some((sku) => sku.toLocaleLowerCase('de-DE').includes(text))) return true

  const digits = text.replace(/\s+/g, '')
  return !!article.gtin && digits !== '' && article.gtin.includes(digits)
}

/** Alle Kennungen, unter denen ein Artikel gefunden wird (SKU, GTIN, Alternativ-SKUs) — für Tooltips und Anzeige. */
export function articleIdentifiers(article: ArticleDto): string[] {
  return [article.sku, ...(article.gtin ? [article.gtin] : []), ...(article.alternativeSkus ?? [])]
}

/**
 * Ist der Artikel heute bestellbar (im Saison-Fenster)? Maßgeblich ist der Wert des Servers (<c>isCurrentlyActive</c>);
 * fehlt er (ältere Server, Testdaten), rechnet die Oberfläche mit denselben Regeln nach.
 */
export function isArticleActive(article: ArticleDto, now: Date = new Date()): boolean {
  return article.isCurrentlyActive ?? isInSeason(article.validFrom, article.validUntil, now)
}

export type ArticleStatusFilter = 'all' | 'active' | 'inactive'

/** Hat der Artikel ein Saison-Fenster (dann zeigt die Liste eine Saison-Kennzeichnung)? */
export function hasSeason(article: ArticleDto): boolean {
  return hasSeasonWindow(article.validFrom, article.validUntil)
}

export function matchesStatusFilter(article: ArticleDto, filter: ArticleStatusFilter, now: Date = new Date()): boolean {
  if (filter === 'all') return true
  const active = isArticleActive(article, now)
  return filter === 'active' ? active : !active
}
