import { describe, expect, it } from 'vitest'
import { checkAlternativeSku, splitSkuInput } from '../../pages/articleEditor/altSkus'
import { checkBundleComponent, findBundlePath } from '../../pages/articleEditor/bundle'
import { makeArticle } from '../helpers/fixtures'

const bundleOf = (id: string, sku: string, ...componentIds: string[]) =>
  makeArticle({
    id,
    sku,
    isBundle: true,
    bundleComponents: componentIds.map((c) => ({ componentArticleId: c, componentSku: c.toUpperCase(), quantity: 1 })),
  })

describe('Bundle-Guard: Selbstreferenz, Duplikate, Zyklen', () => {
  const a = makeArticle({ id: 'a', sku: 'A' })
  const b = makeArticle({ id: 'b', sku: 'B' })
  const c = makeArticle({ id: 'c', sku: 'C' })

  it('lehnt den Artikel selbst ab', () => {
    const result = checkBundleComponent({ articleId: 'a', componentId: 'a', existing: [], articles: [a, b, c] })
    expect(result).toMatchObject({ ok: false, reason: 'self' })
  })

  it('lehnt eine schon enthaltene Komponente ab', () => {
    const result = checkBundleComponent({ articleId: 'a', componentId: 'b', existing: ['b'], articles: [a, b, c] })
    expect(result).toMatchObject({ ok: false, reason: 'duplicate' })
  })

  it('Bundle A enthält B enthält A: B als Komponente von A wird abgelehnt, mit dem Weg in der Meldung', () => {
    const articles = [a, bundleOf('b', 'B', 'a'), c]

    const result = checkBundleComponent({ articleId: 'a', componentId: 'b', existing: [], articles })

    expect(result).toEqual({ ok: false, reason: 'cycle', message: 'Würde einen Zyklus bilden: A → B → A.' })
  })

  it('erkennt Zyklen über mehrere Ebenen (A → B → C → A)', () => {
    const articles = [a, bundleOf('b', 'B', 'c'), bundleOf('c', 'C', 'a')]

    const result = checkBundleComponent({ articleId: 'a', componentId: 'b', existing: [], articles })

    expect(result).toMatchObject({ ok: false, reason: 'cycle' })
    expect((result as { message: string }).message).toContain('A → B → C → A')
  })

  it('erlaubt gültige Komponenten, auch verschachtelte Bundles ohne Zyklus', () => {
    const articles = [a, bundleOf('b', 'B', 'c'), c]
    expect(checkBundleComponent({ articleId: 'a', componentId: 'b', existing: [], articles })).toEqual({ ok: true })
    expect(checkBundleComponent({ articleId: 'a', componentId: 'c', existing: ['b'], articles })).toEqual({ ok: true })
  })

  it('ein neuer Artikel (ohne Id) kann in keinem Zyklus stehen', () => {
    const articles = [bundleOf('b', 'B', 'a'), a]
    expect(checkBundleComponent({ articleId: undefined, componentId: 'b', existing: [], articles })).toEqual({ ok: true })
  })

  it('findBundlePath endet auch bei Zyklen in den Daten', () => {
    const articles = [bundleOf('x', 'X', 'y'), bundleOf('y', 'Y', 'x'), a]
    expect(findBundlePath('x', 'a', articles)).toBeNull()
    expect(findBundlePath('x', 'y', articles)).toEqual(['x', 'y'])
  })
})

describe('Alternativ-SKUs: Prüfung vor dem Aufnehmen', () => {
  const known = new Map([['ersatz-1', 'ERSATZ-1'], ['ersatz-2', 'ERSATZ-2'], ['art-001', 'ART-001']])
  const check = (value: string, existing: string[] = []) => checkAlternativeSku({ value, ownSku: 'ART-001', existing, knownSkus: known })

  it('nimmt eine bekannte SKU in der Schreibweise des Artikels auf', () => {
    expect(check('  ersatz-1 ')).toEqual({ ok: true, sku: 'ERSATZ-1' })
  })

  it('Existenz-Check: unbekannte SKU wird abgelehnt', () => {
    expect(check('GIBT-ES-NICHT')).toEqual({ ok: false, message: 'Kein Artikel mit der SKU GIBT-ES-NICHT gefunden.' })
  })

  it('lehnt den Artikel selbst und Duplikate ab (Groß-/Kleinschreibung egal)', () => {
    expect(check('art-001')).toMatchObject({ ok: false, message: expect.stringContaining('eigener Ersatz') })
    expect(check('ersatz-1', ['ERSATZ-1'])).toMatchObject({ ok: false, message: expect.stringContaining('schon eingetragen') })
  })

  it('wartet, bis die Artikelliste da ist', () => {
    expect(checkAlternativeSku({ value: 'X', ownSku: 'A', existing: [], knownSkus: null })).toMatchObject({
      ok: false,
      message: expect.stringContaining('noch geladen'),
    })
  })

  it('beachtet die Serverlimits (64 Zeichen, zusammen 1000 Zeichen)', () => {
    expect(check('x'.repeat(65))).toMatchObject({ ok: false, message: expect.stringContaining('64') })
    const full = Array.from({ length: 100 }, (_, i) => `SKU-${String(i).padStart(5, '0')}`) // 100 x 9 Zeichen + 99 Kommas = 999
    expect(checkAlternativeSku({ value: 'ERSATZ-1', ownSku: 'A', existing: full, knownSkus: known }))
      .toMatchObject({ ok: false, message: expect.stringContaining('1000') })
  })

  it('zerlegt eingefügte Listen an Komma, Semikolon und Zeilenumbruch', () => {
    expect(splitSkuInput(' A, B;C\nD ,, ')).toEqual(['A', 'B', 'C', 'D'])
    expect(splitSkuInput('   ')).toEqual([])
  })
})
