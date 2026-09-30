import { describe, expect, it } from 'vitest'
import {
  areEquivalentGtins,
  computeCheckDigit,
  equivalentGtinForms,
  gtinError,
  gtinHint,
  isValidGtin,
  normalizeGtin,
} from '../../pages/articleEditor/gtin'

// Die Vektoren sind dieselben wie in tests/Lager.Tests/WP20/GtinTests.cs: Frontend und Server müssen gleich urteilen.
describe('GTIN-Helfer (identisch zur Server-Logik)', () => {
  it.each(['4006381333931', '73513537', '036000291452', '10012345678902'])('%s ist gültig (EAN-13, EAN-8, UPC-A, GTIN-14)', (code) => {
    expect(isValidGtin(code)).toBe(true)
    expect(gtinError(code)).toBeNull()
  })

  it.each([
    ['4006381333932', 'Prüfziffer'],
    ['73513538', 'Prüfziffer'],
    ['036000291453', 'Prüfziffer'],
    ['10012345678903', 'Prüfziffer'],
    ['400638133393', 'Prüfziffer'],
    ['40063813339A1', 'Ziffern'],
    ['4006-3813-33931', 'Ziffern'],
    ['４００６３８１３３３９３１', 'Ziffern'],
    ['123456789', 'Stellen'],
    ['12345678901', 'Stellen'],
    ['123456789012345', 'Stellen'],
    ['0000000000000', 'Nullen'],
  ])('%s wird abgelehnt (%s)', (code, reason) => {
    expect(isValidGtin(code)).toBe(false)
    expect(gtinError(code)).toContain(reason)
  })

  it('nennt die erwartete Prüfziffer', () => {
    expect(gtinError('4006381333932')).toBe('Prüfziffer der GTIN stimmt nicht (erwartet 1).')
  })

  it('kein Wert ist kein Fehler, aber auch keine gültige GTIN', () => {
    for (const value of [null, undefined, '', '   ']) {
      expect(normalizeGtin(value)).toBe('')
      expect(gtinError(value)).toBeNull()
      expect(isValidGtin(value)).toBe(false)
    }
  })

  it('entfernt allen Leerraum, ändert sonst nichts', () => {
    expect(normalizeGtin(' 4 006381 333931\t')).toBe('4006381333931')
    expect(normalizeGtin('4006-3813')).toBe('4006-3813')
    expect(isValidGtin('4 006381 333931')).toBe(true)
  })

  it('berechnet die Prüfziffer mit den Gewichten 3 und 1 von rechts', () => {
    expect(computeCheckDigit('400638133393')).toBe(1)
    expect(computeCheckDigit('7351353')).toBe(7)
    expect(computeCheckDigit('03600029145')).toBe(2)
    expect(computeCheckDigit('1001234567890')).toBe(2)
    expect(() => computeCheckDigit('40063813339x')).toThrow()
  })

  it('kennt gleichwertige Schreibweisen (UPC-A = EAN-13 mit führender Null = GTIN-14)', () => {
    const forms = equivalentGtinForms('036000291452')
    expect(forms[0]).toBe('036000291452')
    expect(forms).toEqual(expect.arrayContaining(['0036000291452', '00036000291452']))
    expect(forms.some((f) => f.length === 8)).toBe(false)
    expect(areEquivalentGtins('0036000291452', '036000291452')).toBe(true)
    expect(areEquivalentGtins('4006381333931', '036000291452')).toBe(false)
    expect(equivalentGtinForms('73513537')).toEqual(['73513537', '000073513537', '0000073513537', '00000073513537'])
    expect(equivalentGtinForms('4006381333932')).toEqual([])
  })

  it('der Live-Hinweis nennt die Art der GTIN oder den Grund', () => {
    expect(gtinHint('')).toEqual({ status: 'empty', message: '' })
    expect(gtinHint('4006381333931')).toEqual({ status: 'valid', message: 'Gültige GTIN (EAN-13, Prüfziffer stimmt).' })
    expect(gtinHint('73513537').message).toContain('EAN-8')
    expect(gtinHint('036000291452').message).toContain('UPC-A')
    expect(gtinHint('10012345678902').message).toContain('GTIN-14')
    expect(gtinHint('4006381333932')).toEqual({ status: 'invalid', message: 'Prüfziffer der GTIN stimmt nicht (erwartet 1).' })
  })
})
