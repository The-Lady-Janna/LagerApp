import { useTranslation } from 'react-i18next'
import { SUPPORTED_LANGUAGES, normalizeLanguage, setLanguage } from '../i18n'

/// <summary>
/// Sprachumschalter DE/EN. Die Wahl wird in localStorage ("lager.lang") gespeichert (i18n/setLanguage) und wirkt sofort
/// auf die ganze Oberfläche. `variant="sidebar"` passt zur dunklen Seitenleiste (wie die Theme-Wahl), `"plain"` zum
/// Login-Formular.
/// </summary>
export function LanguageSwitcher({ variant = 'sidebar' }: { variant?: 'sidebar' | 'plain' }) {
  const { t, i18n } = useTranslation()
  const active = normalizeLanguage(i18n.resolvedLanguage ?? i18n.language)
  return (
    <div className={variant === 'sidebar' ? 'theme-switch lang-switch' : 'lang-switch lang-switch--plain'} role="group" aria-label={t('nav:language.label')}>
      {SUPPORTED_LANGUAGES.map((lng) => (
        <button
          key={lng}
          type="button"
          lang={lng}
          title={t(`nav:language.names.${lng}`)}
          aria-pressed={active === lng}
          onClick={() => { void setLanguage(lng) }}
        >
          {lng.toUpperCase()}
        </button>
      ))}
    </div>
  )
}
