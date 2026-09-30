import { lazy, Suspense } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router-dom'

// Der Editor ist groß (Bundle, Alternativ-SKUs, Saison, GTIN) und liegt in einem eigenen Chunk: mit ihm im Hauptpaket
// überschritte es die 500-kB-Grenze von Vite.
const loadEditor = () => import('./articleEditor/ArticleEditor').then((m) => ({ default: m.ArticleEditor }))
const ArticleEditor = lazy(loadEditor)

// Der Ladebeginn liegt schon beim Import dieser Seite, nicht erst beim ersten Rendern: der Chunk lädt im Hintergrund und ist
// meist da, wenn der Editor geöffnet wird (Tests, die den Editor sofort erwarten, warten sonst auf den Chunk-Download bzw.
// die erste Transformation des Moduls und laufen unter Last in ihr Timeout). Doppelt geladen wird nichts: import() ist zwischengespeichert.
// Ein Fehler hier ist harmlos: beim Rendern lädt lazy() den Chunk erneut und zeigt dort den Fehler.
loadEditor().catch(() => undefined)

export function ArticleEditorPage() {
  const { id } = useParams()
  const { t } = useTranslation()
  // key: beim Wechsel zwischen Artikeln (oder zu "neu") beginnt der Entwurf frisch.
  return (
    <Suspense fallback={<p className="muted" role="status">{t('articles:editor.loadingEditor')}</p>}>
      <ArticleEditor key={id ?? 'new'} id={id} />
    </Suspense>
  )
}
