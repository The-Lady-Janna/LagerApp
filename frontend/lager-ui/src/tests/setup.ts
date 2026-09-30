import './helpers/germanBrowser'
import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach, beforeEach, vi } from 'vitest'
import { DEFAULT_LANGUAGE, i18n } from '../i18n'

// findBy*/waitFor warten standardmäßig nur 1 s - zu knapp, wenn ein per React.lazy geladener Chunk unter Last
// langsam transformiert wird.
configure({ asyncUtilTimeout: 10_000 })

// Der Axios-Client loggt jede Fehlerantwort per console.error ("API error: …"). In Tests, die absichtlich
// Serverfehler auslösen, ist das nur Rauschen — alle anderen Fehlermeldungen bleiben sichtbar.
// (restoreMocks in vite.config.ts stellt console.error nach jedem Test wieder her.)
beforeEach(() => {
  const original = console.error
  vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
    if (typeof args[0] === 'string' && args[0].startsWith('API error:')) return
    original(...args)
  })
})

// Jeder Test startet mit leerem DOM und leerem localStorage (die Zustand-Stores für Auth,
// Lager-Auswahl und Theme persistieren dorthin) und mit der Standardsprache Deutsch (ein Test, der die Sprache
// umstellt, darf die folgenden nicht beeinflussen).
afterEach(async () => {
  cleanup()
  localStorage.clear()
  if (i18n.language !== DEFAULT_LANGUAGE) await i18n.changeLanguage(DEFAULT_LANGUAGE)
})
