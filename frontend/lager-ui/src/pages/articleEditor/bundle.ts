import type { ArticleDto } from '../../api/types'
import { t } from '../../i18n'

/// <summary>
/// Guards für Bundle-Komponenten im Editor: keine Selbstreferenz, keine Duplikate, keine Zyklen (A enthält B enthält A, auch
/// über mehrere Ebenen). Der Server prüft dasselbe beim Speichern (409 bundle_cycle) — der Editor bietet ungültige Komponenten
/// gar nicht erst an und erklärt, warum.
/// </summary>

export type BundleCheck =
  | { ok: true }
  | { ok: false; reason: 'self' | 'duplicate' | 'cycle'; message: string }

/**
 * Der Weg von <paramref name="fromId"/> nach <paramref name="targetId"/> über Bundle-Komponenten (Artikel-Ids einschließlich
 * Start und Ziel) oder null, wenn es keinen gibt. Läuft über den Stand der Artikelliste; Zyklen in den Daten enden über die Besuchsliste.
 */
export function findBundlePath(fromId: string, targetId: string, articles: readonly ArticleDto[]): string[] | null {
  const byId = new Map(articles.map((a) => [a.id, a]))
  const visited = new Set<string>()

  const walk = (id: string, path: string[]): string[] | null => {
    if (id === targetId) return path
    if (visited.has(id)) return null
    visited.add(id)
    for (const component of byId.get(id)?.bundleComponents ?? []) {
      const found = walk(component.componentArticleId, [...path, component.componentArticleId])
      if (found) return found
    }
    return null
  }

  return walk(fromId, [fromId])
}

interface CheckInput {
  /** Id des bearbeiteten Artikels; beim Neuanlegen undefined (ein neuer Artikel kann noch in keinem Zyklus stehen). */
  articleId: string | undefined
  /** Die Komponente, die hinzugefügt werden soll. */
  componentId: string
  /** Ids der bereits aufgenommenen Komponenten. */
  existing: readonly string[]
  /** Alle Artikel (Stand des Servers). */
  articles: readonly ArticleDto[]
}

/**
 * Darf <c>componentId</c> in das Bundle <c>articleId</c> aufgenommen werden? Sonst der Grund als Meldung: der Artikel selbst,
 * schon enthalten, oder ein Zyklus (mit dem Weg "A → B → A"). Der Stand der Komponenten des bearbeiteten Artikels selbst spielt
 * für den Zyklus keine Rolle: entscheidend ist, ob die Komponente den Artikel über andere Bundles schon enthält.
 */
export function checkBundleComponent({ articleId, componentId, existing, articles }: CheckInput): BundleCheck {
  if (articleId && componentId === articleId) {
    return { ok: false, reason: 'self', message: t('articles:bundle.self') }
  }
  if (existing.includes(componentId)) {
    return { ok: false, reason: 'duplicate', message: t('articles:bundle.duplicate') }
  }
  if (articleId) {
    const path = findBundlePath(componentId, articleId, articles)
    if (path) {
      const sku = (id: string) => articles.find((a) => a.id === id)?.sku ?? id
      const chain = [sku(articleId), ...path.map(sku)].join(' → ')
      return { ok: false, reason: 'cycle', message: t('articles:bundle.cycle', { chain }) }
    }
  }
  return { ok: true }
}
