import { describe, expect, it } from 'vitest'
import { analyze, checkI18n, flatten, formatReport, scanSources } from '../../../scripts/i18n-check.mjs'

// Dieselbe Logik wie `npm run i18n:check` (scripts/i18n-check.mjs): der Test stellt sicher, dass de und en vollständig
// übereinstimmen, dass im Code kein Schlüssel fehlt und keiner ungenutzt in den Sprachdateien liegt.

const locale = (entries: Record<string, Record<string, unknown>>) =>
  Object.fromEntries(Object.entries(entries).map(([ns, json]) => [ns, flatten(json)]))

describe('Sprachdateien (de/en)', () => {
  it('haben dieselben Namespaces und Schlüssel, dieselben Platzhalter und keine leeren Texte', () => {
    const result = checkI18n()
    expect(formatReport(result)).toContain('OK')
    expect(result.problems).toEqual({
      missingLanguage: [], missingNamespace: [], missingKey: [], extraKey: [],
      placeholderMismatch: [], emptyValue: [], undefinedKey: [], unusedKey: [],
    })
    expect(result.ok).toBe(true)
    // Die Standardsprache und die Übersetzung sind beide da; es gibt Namespaces je Bereich.
    expect(result.stats.languages).toEqual(['de', 'en'])
    expect(result.stats.namespaces).toEqual(expect.arrayContaining(['common', 'nav', 'auth', 'errors', 'status', 'articles', 'orders', 'stock', 'inbound', 'picking', 'shipping', 'reports', 'system']))
    expect(result.stats.keys).toBeGreaterThan(1000)
  })
})

describe('Prüflogik (analyze)', () => {
  const de = locale({ common: { save: 'Speichern', hello: 'Hallo {{name}}', items_one: '{{count}} Stück', items_other: '{{count}} Stücke' } })

  it('meldet einen Schlüssel, der in en fehlt, einen überzähligen und abweichende Platzhalter', () => {
    const en = locale({ common: { hello: 'Hello {{user}}', extra: 'Extra', items_one: '{{count}} piece', items_other: '{{count}} pieces' } })
    const result = analyze({ locales: { de, en }, sources: { 'src/a.tsx': "t('common:save'); t('common:hello', { name }); t('common:items', { count })" } })
    expect(result.problems.missingKey).toEqual(['en common:save fehlt'])
    expect(result.problems.extraKey).toEqual(['en common:extra (in de nicht vorhanden)'])
    expect(result.problems.placeholderMismatch).toHaveLength(1)
    expect(result.problems.placeholderMismatch[0]).toContain('common:hello')
    expect(result.ok).toBe(false)
  })

  it('findet im Code benutzte, aber nicht definierte Schlüssel und definierte, aber ungenutzte', () => {
    const en = locale({ common: { save: 'Save', hello: 'Hello {{name}}', items_one: '{{count}} piece', items_other: '{{count}} pieces' } })
    const sources = {
      'src/a.tsx': "t('common:save'); t('common:missing'); <Trans i18nKey=\"common:hello\" />",
      'src/b.ts': "const x = 'common:unknownToo'",
    }
    const result = analyze({ locales: { de, en }, sources })
    expect(result.problems.undefinedKey.map((p) => p.split(' ')[0])).toEqual(['common:missing', 'common:unknownToo'])
    // items (Plural) wird nirgends benutzt
    expect(result.problems.unusedKey).toEqual(['common:items_one', 'common:items_other'])
  })

  it('zählt Plural-Formen und dynamische Schlüssel als benutzt', () => {
    const en = locale({ common: { save: 'Save', hello: 'Hello {{name}}', items_one: '{{count}} piece', items_other: '{{count}} pieces' } })
    const sources = { 'src/a.tsx': "t('common:items', { count }); t(`common:s${'a'}`); t('common:hello')" }
    const result = analyze({ locales: { de, en }, sources })
    expect(result.problems.unusedKey).toEqual([])
    expect(result.problems.undefinedKey).toEqual([])
  })

  it('meldet leere Übersetzungen und fehlende Namespace-Dateien', () => {
    const en = locale({ common: { save: ' ', hello: 'Hello {{name}}', items_one: 'a', items_other: 'b' } })
    const result = analyze({ locales: { de, en: { ...en, stray: new Map([['x', 'y']]) } }, sources: {} })
    expect(result.problems.emptyValue).toEqual(['en common:save'])
    expect(result.problems.extraKey).toContain('en stray.json (in de nicht vorhanden)')
    const withoutNs = analyze({ locales: { de, en: {} }, sources: {} })
    expect(withoutNs.problems.missingNamespace).toEqual(['en/common.json fehlt'])
  })

  it('scanSources: nur bekannte Namespaces zählen, ein nacktes t("key") gilt als common', () => {
    const { exact, prefixes } = scanSources({
      'src/x.tsx': "t('orders:title'); const css = 'a:hover'; t('plain'); t(`status:order.${s}`)",
    }, ['orders', 'status', 'common'])
    expect([...exact.keys()].sort()).toEqual(['common:plain', 'orders:title'])
    expect([...prefixes.keys()]).toEqual(['status:order.'])
  })
})
