import i18n from 'i18next'
import LanguageDetector from 'i18next-browser-languagedetector'
import { initReactI18next } from 'react-i18next'

/// <summary>
/// Internationalisierung (react-i18next): Deutsch ist die Standardsprache und die Quelle aller Texte, Englisch die
/// vollständige Übersetzung. Die Texte liegen je Bereich (Namespace) in src/locales/<sprache>/<namespace>.json und werden
/// hier per import.meta.glob eingesammelt — ein neuer Namespace braucht also nur zwei Dateien (de + en), keinen Code.
/// Konvention und Prüfskript: src/locales/README.md, scripts/i18n-check.mjs (npm run i18n:check).
///
/// Sprache: gespeicherte Wahl (localStorage "lager.lang") → Browsersprache → Deutsch. Gespeichert wird NUR eine bewusste
/// Wahl (setLanguage); wer die Browsersprache erbt, folgt ihr weiter. Die Ressourcen sind gebündelt, die Initialisierung
/// ist synchron: schon der erste Render hat alle Texte (kein Suspense, keine Schlüssel-Blitzer).
/// </summary>

import { DEFAULT_LANGUAGE, SUPPORTED_LANGUAGES, currentLanguage, intlLocale, normalizeLanguage, setActiveLanguage, type Language } from './locale'

// Sprachcodes und Intl-Locale liegen abhängigkeitsfrei in ./locale (lib/format.ts lädt sie direkt, auch unter reinem Node).
export { DEFAULT_LANGUAGE, SUPPORTED_LANGUAGES, currentLanguage, intlLocale, normalizeLanguage }
export type { Language }
/** localStorage-Schlüssel der gewählten Sprache. */
export const LANGUAGE_STORAGE_KEY = 'lager.lang'
export const DEFAULT_NAMESPACE = 'common'

type Resources = Record<string, Record<string, Record<string, unknown>>>

// "../locales/de/common.json" → resources.de.common
const files = import.meta.glob<Record<string, unknown>>('../locales/*/*.json', { eager: true, import: 'default' })
const resources: Resources = {}
for (const [file, content] of Object.entries(files)) {
  const match = /\/locales\/([^/]+)\/([^/]+)\.json$/.exec(file)
  if (!match) continue
  const [, lng, ns] = match
  ;(resources[lng] ??= {})[ns] = content
}

const namespaces = [...new Set(Object.values(resources).flatMap((byNs) => Object.keys(byNs)))].sort()

// Hält die aktive Sprache (für lib/format.ts) und <html lang> aktuell. Der Listener steht VOR denen von react-i18next: beim
// Neuzeichnen nach dem Sprachwechsel ist die Locale der Formatierer schon umgestellt.
function applyLanguage(lng: string | undefined): void {
  setActiveLanguage(lng)
  if (typeof document !== 'undefined') document.documentElement.lang = normalizeLanguage(lng)
}

void i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources,
    ns: namespaces,
    defaultNS: DEFAULT_NAMESPACE,
    fallbackLng: DEFAULT_LANGUAGE,
    supportedLngs: [...SUPPORTED_LANGUAGES],
    load: 'languageOnly',
    // Nur eine bewusste Wahl wird gespeichert (setLanguage), nicht die erkannte Browsersprache.
    // "en-US" → "en", Unbekanntes → Deutsch: i18n.language ist danach immer eine der unterstützten Sprachen.
    detection: { order: ['localStorage', 'navigator'], lookupLocalStorage: LANGUAGE_STORAGE_KEY, caches: [], convertDetectedLanguage: normalizeLanguage },
    // React maskiert selbst; Werte wie "a < b" dürfen nicht zu "a &lt; b" werden.
    interpolation: { escapeValue: false },
    react: { useSuspense: false },
    initAsync: false,
  })

applyLanguage(i18n.language)
i18n.on('languageChanged', applyLanguage)

/**
 * Wechselt die Sprache und merkt sie sich (localStorage "lager.lang"). Ein Speicherfehler (privater Modus, Quota) ist
 * kein Grund, den Wechsel zu verwerfen: die Sprache gilt dann bis zum Neuladen.
 */
export function setLanguage(language: Language): Promise<unknown> {
  try { localStorage.setItem(LANGUAGE_STORAGE_KEY, language) } catch { /* nur nicht gemerkt */ }
  return i18n.changeLanguage(language)
}

/** Übersetzungsfunktion außerhalb von React (Stores, Fehlertexte, reine Logik): liest die Sprache bei jedem Aufruf neu. */
export const t = i18n.t.bind(i18n)

export { i18n }
export default i18n
