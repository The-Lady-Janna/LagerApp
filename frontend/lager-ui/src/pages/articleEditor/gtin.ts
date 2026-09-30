import { t } from '../../i18n'

/// <summary>
/// GTIN/EAN: dieselbe Logik wie der Server (src/Lager.Domain/Articles/Gtin.cs) — gleiche Regeln, gleiche Meldungen, damit der
/// Editor eine falsche GTIN schon beim Tippen meldet und der Server dieselbe Eingabe genauso beurteilt.
///
/// Nur Ziffern, 8 (EAN-8), 12 (UPC-A), 13 (EAN-13) oder 14 (GTIN-14) Stellen, letzte Stelle = Prüfziffer nach GS1
/// (Modulo 10, Gewichte 3 und 1 von rechts). Leerraum wird vor der Prüfung entfernt ("4 006381 333931").
/// </summary>

/** Erlaubte Stellenzahlen einschließlich Prüfziffer. */
export const GTIN_LENGTHS: readonly number[] = [8, 12, 13, 14]

/** Bezeichnung der GTIN-Art zur Stellenzahl (für die Anzeige). */
const GTIN_KIND: Readonly<Record<number, string>> = {
  8: 'EAN-8',
  12: 'UPC-A',
  13: 'EAN-13',
  14: 'GTIN-14',
}

const ASCII_DIGITS = /^[0-9]+$/

/**
 * Entfernt allen Leerraum (auch innerhalb). Leer oder nur Leerraum ergibt '' (= keine GTIN).
 * Sonst bleibt alles stehen: Bindestriche oder Buchstaben fallen bei der Prüfung durch.
 */
export function normalizeGtin(value: string | null | undefined): string {
  return (value ?? '').replace(/\s+/g, '')
}

/** Prüfziffer zu den Stellen OHNE Prüfziffer (7, 11, 12 oder 13 Ziffern). */
export function computeCheckDigit(digitsWithoutCheckDigit: string): number {
  if (!ASCII_DIGITS.test(digitsWithoutCheckDigit)) throw new Error('Nur Ziffern erlaubt.')
  let sum = 0
  let weight = 3
  for (let i = digitsWithoutCheckDigit.length - 1; i >= 0; i--) {
    sum += Number(digitsWithoutCheckDigit[i]) * weight
    weight = weight === 3 ? 1 : 3
  }
  return (10 - (sum % 10)) % 10
}

/**
 * Die Fehlermeldung zu einer GTIN (Sprache der Oberfläche) oder null, wenn sie gültig ist. Kein Wert (leer/Leerraum) ist kein Fehler
 * (= keine GTIN gesetzt).
 */
export function gtinError(value: string | null | undefined): string | null {
  const gtin = normalizeGtin(value)
  if (gtin === '') return null

  if (!ASCII_DIGITS.test(gtin)) return t('articles:gtin.digitsOnly')
  if (!GTIN_LENGTHS.includes(gtin.length)) return t('articles:gtin.length', { count: gtin.length })
  if (/^0+$/.test(gtin)) return t('articles:gtin.zeros')

  const expected = computeCheckDigit(gtin.slice(0, -1))
  if (Number(gtin[gtin.length - 1]) !== expected) return t('articles:gtin.checkDigit', { expected })
  return null
}

/** Ist der Wert nach dem Normalisieren eine gültige GTIN? Leer ist keine GTIN (false). */
export function isValidGtin(value: string | null | undefined): boolean {
  return normalizeGtin(value) !== '' && gtinError(value) === null
}

/**
 * Alle Schreibweisen derselben GTIN (mit führenden Nullen auf 8/12/13/14 Stellen aufgefüllt bzw. gekürzt): UPC-A
 * "036000291452" = EAN-13 "0036000291452" = GTIN-14 "00036000291452". Zuerst der Wert selbst; ungültig ergibt [].
 */
export function equivalentGtinForms(value: string | null | undefined): string[] {
  const gtin = normalizeGtin(value)
  if (gtin === '' || gtinError(gtin) !== null) return []

  const core = gtin.replace(/^0+/, '')
  const forms = [gtin]
  for (const length of GTIN_LENGTHS) {
    if (length < core.length) continue
    const form = core.padStart(length, '0')
    if (!forms.includes(form)) forms.push(form)
  }
  return forms
}

/** Sind beide Werte dieselbe (gültige) GTIN, ggf. in unterschiedlicher Länge? */
export function areEquivalentGtins(a: string | null | undefined, b: string | null | undefined): boolean {
  const other = normalizeGtin(b)
  return other !== '' && equivalentGtinForms(a).includes(other)
}

export interface GtinHint {
  /** empty = nichts eingegeben, valid = gültig, invalid = Fehler (Text in message). */
  status: 'empty' | 'valid' | 'invalid'
  message: string
}

/** Der Live-Hinweis unter dem GTIN-Feld: gültig (mit Art der GTIN) oder der Grund, warum nicht. */
export function gtinHint(value: string | null | undefined): GtinHint {
  const gtin = normalizeGtin(value)
  if (gtin === '') return { status: 'empty', message: '' }
  const error = gtinError(gtin)
  if (error) return { status: 'invalid', message: error }
  return { status: 'valid', message: t('articles:gtin.valid', { kind: GTIN_KIND[gtin.length] }) }
}
