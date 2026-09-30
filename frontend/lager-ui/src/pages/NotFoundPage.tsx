import { Trans, useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router-dom'

/// <summary>
/// Catch-all für unbekannte URLs (auch `/picker` ohne Pickliste-ID).
/// </summary>
export function NotFoundPage() {
  const { pathname } = useLocation()
  const { t } = useTranslation()
  return (
    <div className="card" style={{ maxWidth: 560, margin: '40px auto' }}>
      <h2 style={{ marginTop: 0 }}>{t('errors:notFound.title')}</h2>
      <p>
        <Trans i18nKey="errors:notFound.body" values={{ path: pathname }} components={{ code: <code /> }} />
      </p>
      <Link to="/">{t('common:toHome')}</Link>
    </div>
  )
}
