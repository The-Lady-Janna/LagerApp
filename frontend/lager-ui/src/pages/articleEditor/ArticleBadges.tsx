import { useTranslation } from 'react-i18next'
import type { ArticleDto } from '../../api/types'
import { StatusPill } from '../../components/StatusPill'
import { seasonWindowText } from './season'

/// <summary>
/// Kleine Kennzeichnungen für die Artikelliste und den Editor: "Bundle" (Komponenten im Tooltip) und der Saison-Status
/// ("In Saison" / "Außerhalb der Saison" mit dem Fenster im Tooltip). Die Pills sind die StatusPill der App (Farben aus den
/// Design-Tokens, hell/dunkel); der Tooltip hängt an einem umgebenden span, weil die StatusPill keinen title kennt.
/// </summary>

/** Die StatusPill mit Tooltip. */
function TipPill({ tone, status, label, title }: { tone: 'special' | 'success' | 'danger'; status: string; label: string; title: string }) {
  return (
    <span title={title}>
      <StatusPill status={status} tone={tone} label={label} />
    </span>
  )
}

/** "Bundle" mit den Komponenten ("2× SCHRAUBE, 1× MUTTER") im Tooltip; für Nicht-Bundles nichts. */
export function BundleBadge({ article, skuById }: { article: Pick<ArticleDto, 'isBundle' | 'bundleComponents'>; skuById?: ReadonlyMap<string, string> }) {
  const { t } = useTranslation()
  if (!article.isBundle) return null
  const parts = (article.bundleComponents ?? []).map((c) => `${c.quantity}× ${c.componentSku || skuById?.get(c.componentArticleId) || c.componentArticleId}`)
  return <TipPill tone="special" status="bundle" label={t('articles:badge.bundle')} title={parts.length > 0 ? t('articles:badge.consistsOf', { parts: parts.join(', ') }) : t('articles:badge.bundle')} />
}

/**
 * Saison-Status: für Artikel ohne Fenster nichts (immer bestellbar); sonst "In Saison" oder "Außerhalb der Saison" mit dem Fenster
 * im Tooltip. <paramref name="active"/> ist der Wert des Servers (isCurrentlyActive) bzw. die Nachrechnung im Editor.
 */
export function SeasonBadge({ active, validFrom, validUntil }: { active: boolean; validFrom: string | null; validUntil: string | null }) {
  const { t } = useTranslation()
  if (!validFrom && !validUntil) return null
  const text = seasonWindowText(validFrom, validUntil)
  return active
    ? <TipPill tone="success" status="in-season" label={t('articles:badge.inSeason')} title={t('articles:badge.seasonTip', { window: text })} />
    : <TipPill tone="danger" status="out-of-season" label={t('articles:badge.outOfSeason')} title={t('articles:badge.seasonTipInactive', { window: text })} />
}
