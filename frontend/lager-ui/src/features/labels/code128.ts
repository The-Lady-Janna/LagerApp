/// <summary>
/// Code-128-Encoder (Code Set B und C) für die Etiketten im Browser. Spiegelt Lager.Api.Labels.Code128Encoder
/// (Backend, Lieferschein-PDF): dieselben Regeln, dieselben Testvektoren (src/tests/WP26/code128.test.ts und
/// tests/Lager.Tests/WP26/Code128EncoderTests.cs) — für dieselbe Eingabe entstehen dieselben Symbole,
/// dieselbe Prüfsumme und dieselbe Modulfolge.
///
/// Set B deckt ASCII 32..126 ab, Set C kodiert zwei Ziffern je Symbol. Set A, FNC-Zeichen und alles außerhalb von
/// ASCII 32..126 sind nicht vorgesehen: ein Zeichen, das nicht kodierbar ist, ist ein Fehler statt einer stillen Änderung
/// (der Scan muss exakt den Code liefern). Die Wahl zwischen B und C liefert die kürzeste Symbolfolge; bei Gleichstand
/// gilt Start in B vor Start in C und im Set bleiben vor Umschalten.
/// </summary>

import { t } from '../../i18n'

export const START_B = 104
export const START_C = 105
export const CODE_B = 100
export const CODE_C = 99
export const STOP = 106

/** Mindestbreite der Ruhezone links und rechts in Modulen (Vorgabe der Norm: 10 X). */
export const QUIET_ZONE_MODULES = 10

/** Balken-/Lückenbreiten je Symbolwert (0..105: sechs Elemente, 106 = Stopp mit sieben), Balken zuerst (ISO/IEC 15417). */
const PATTERNS: readonly string[] = [
  '212222', '222122', '222221', '121223', '121322', '131222', '122213', '122312', '132212', '221213',
  '221312', '231212', '112232', '122132', '122231', '113222', '123122', '123221', '223211', '221132',
  '221231', '213212', '223112', '312131', '311222', '321122', '321221', '312212', '322112', '322211',
  '212123', '212321', '232121', '111323', '131123', '131321', '112313', '132113', '132311', '211313',
  '231113', '231311', '112133', '112331', '132131', '113123', '113321', '133121', '313121', '211331',
  '231131', '213113', '213311', '213131', '311123', '311321', '331121', '312113', '312311', '332111',
  '314111', '221411', '431111', '111224', '111422', '121124', '121421', '141122', '141221', '112214',
  '112412', '122114', '122411', '142112', '142211', '241211', '221114', '413111', '241112', '134111',
  '111242', '121142', '121241', '114212', '124112', '124211', '411212', '421112', '421211', '212141',
  '214121', '412121', '111143', '111341', '131141', '114113', '114311', '411113', '411311', '113141',
  '114131', '311141', '411131', '211412', '211214', '211232', '2331112',
]

/** Ein kodierter Code-128-Barcode (siehe Code128Barcode im Backend). */
export interface Code128Barcode {
  /** Der kodierte Text (genau das, was ein Scanner wieder ausgibt). */
  text: string
  /** Alle Symbolwerte in Druckreihenfolge: Start (104 = B, 105 = C), Daten (inkl. Umschalter 99/100), Prüfsumme, Stopp (106). */
  symbols: number[]
  /** Prüfsumme (Start + Summe Wert × Position) mod 103; das vorletzte Symbol. */
  checksum: number
  /** Breiten der Balken und Lücken in Modulen, abwechselnd, beginnend mit einem Balken. Ohne Ruhezonen. */
  widths: number[]
  /** Modulfolge als Text: "1" = Balken, "0" = Lücke (ohne Ruhezonen). */
  modules: string
  /** Gesamtbreite in Modulen (ohne Ruhezonen). */
  moduleCount: number
}

/** Der Text lässt sich nicht als Code 128 (Set B/C) kodieren. */
export class Code128Error extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'Code128Error'
  }
}

const isSetBCharacter = (c: string): boolean => c >= ' ' && c <= '~'
const isDigit = (c: string): boolean => c >= '0' && c <= '9'

/** Lässt sich der Text kodieren? (nicht leer, nur ASCII 32..126) */
export function canEncodeCode128(text: string | null | undefined): text is string {
  if (!text) return false
  for (const c of text) if (!isSetBCharacter(c)) return false
  return true
}

/** Der erste nicht kodierbare Zeichen-Grund als Meldung (Sprache der Oberfläche), oder null, wenn der Text kodierbar ist. */
export function code128Problem(text: string | null | undefined): string | null {
  if (!text) return t('labels:code128.empty')
  for (const c of text) {
    if (!isSetBCharacter(c)) {
      const point = (c.codePointAt(0) ?? 0).toString(16).toUpperCase().padStart(4, '0')
      return t('labels:code128.char', { char: c, point })
    }
  }
  return null
}

/**
 * Kodiert den Text. Wirft {@link Code128Error} bei leerem Text oder einem Zeichen außerhalb von ASCII 32..126
 * (vorher mit {@link canEncodeCode128} prüfen, wenn ohne Barcode weitergearbeitet werden soll).
 */
export function encodeCode128(text: string): Code128Barcode {
  const problem = code128Problem(text)
  if (problem) throw new Code128Error(problem)

  const n = text.length
  const pairAt = (i: number) => i + 1 < n && isDigit(text[i]) && isDigit(text[i + 1])

  // cost[i][set]: kleinste Zahl weiterer Symbole (ohne Start, Prüfsumme, Stopp) für text[i..], wenn Set B (0) bzw. C (1)
  // aktiv ist. Rückwärts gefüllt; die geschlossene Form löst die gegenseitige Abhängigkeit von B(i) und C(i) ohne Iteration
  // (ein Wechsel in ein Set, das sofort wieder verlassen wird, lohnt nie).
  const cost: [number, number][] = Array.from({ length: n + 1 }, () => [0, 0])
  for (let i = n - 1; i >= 0; i--) {
    if (pairAt(i)) {
      const pair = 1 + cost[i + 2][1]
      cost[i][0] = 1 + Math.min(cost[i + 1][0], pair)
      cost[i][1] = Math.min(pair, 1 + cost[i][0])
    } else {
      cost[i][0] = 1 + cost[i + 1][0]
      cost[i][1] = 1 + cost[i][0]
    }
  }

  let set = cost[0][0] <= cost[0][1] ? 0 : 1
  const symbols: number[] = [set === 0 ? START_B : START_C]
  let index = 0
  while (index < n) {
    if (set === 0) {
      // Bei Gleichstand im Set bleiben (Zeichen kodieren), sonst nach C wechseln.
      if (1 + cost[index + 1][0] <= 1 + cost[index][1]) {
        symbols.push(text.charCodeAt(index) - 32)
        index++
      } else {
        symbols.push(CODE_C)
        set = 1
      }
    } else {
      const pair = pairAt(index) ? 1 + cost[index + 2][1] : Number.POSITIVE_INFINITY
      if (pair <= 1 + cost[index][0]) {
        symbols.push(Number(text.slice(index, index + 2)))
        index += 2
      } else {
        symbols.push(CODE_B)
        set = 0
      }
    }
  }

  // Prüfsumme: Startwert + Summe (Wert × Position ab 1) mod 103 (Start und Stopp zählen nicht als Position).
  let sum = symbols[0]
  for (let i = 1; i < symbols.length; i++) sum += symbols[i] * i
  const checksum = sum % 103
  symbols.push(checksum, STOP)

  const widths: number[] = []
  for (const symbol of symbols) for (const digit of PATTERNS[symbol]) widths.push(Number(digit))

  const modules = widths.map((width, i) => (i % 2 === 0 ? '1' : '0').repeat(width)).join('')
  return { text, symbols, checksum, widths, modules, moduleCount: modules.length }
}

/** Die schwarzen Balken als (Startmodul, Breite in Modulen), von links nach rechts (ohne Ruhezone). */
export function barRuns(barcode: Pick<Code128Barcode, 'widths'>): { start: number; width: number }[] {
  const runs: { start: number; width: number }[] = []
  let position = 0
  barcode.widths.forEach((width, i) => {
    if (i % 2 === 0) runs.push({ start: position, width })
    position += width
  })
  return runs
}
