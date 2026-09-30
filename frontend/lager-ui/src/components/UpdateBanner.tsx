import { useTranslation } from 'react-i18next'
import { useAppUpdate } from '../lib/appUpdate'

/// <summary>
/// Kleines Banner unten rechts: "Neue Version verfügbar". Der Service-Worker
/// ist bereits aktiv, die geöffnete Seite läuft aber noch mit dem alten Code —
/// ein Neuladen holt die neue Version. Bewusst KEIN Auto-Reload, damit keine
/// ungespeicherten Eingaben verloren gehen.
/// </summary>
export function UpdateBanner() {
  const { t } = useTranslation()
  const updateAvailable = useAppUpdate((s) => s.updateAvailable)
  const setUpdateAvailable = useAppUpdate((s) => s.setUpdateAvailable)
  if (!updateAvailable) return null
  return (
    <div
      role="status"
      className="update-banner"
    >
      <span>{t('common:update.available')}</span>
      <button className="primary" onClick={() => window.location.reload()}>{t('common:reload')}</button>
      <button onClick={() => setUpdateAvailable(false)} aria-label={t('common:update.dismiss')}>×</button>
    </div>
  )
}
