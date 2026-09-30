import { describe, expect, it } from 'vitest'
import {
  CODE_B, CODE_C, Code128Error, START_B, START_C, STOP, barRuns, canEncodeCode128, code128Problem, encodeCode128,
} from '../../features/labels/code128'

/// Dieselben Testvektoren wie das Backend (tests/Lager.Tests/WP26/Code128EncoderTests.cs): für dieselbe Eingabe müssen
/// Symbolfolge, Prüfsumme und Modulfolge identisch sein. Die Werte stammen aus einer unabhängigen Referenzimplementierung.
const VECTORS = [
  { text: 'ORD-DEMO-01', symbols: [104, 47, 50, 36, 13, 36, 37, 45, 47, 13, 16, 17, 11, 106], checksum: 11, modules: '110100100001000111011011000101110101100010001001101110010110001000100011010001011101100010001110110100110111001001110110010011100110110001001001100011101011' },
  { text: 'A-01-01', symbols: [104, 33, 13, 16, 17, 13, 16, 17, 44, 106], checksum: 44, modules: '1101001000010100011000100110111001001110110010011100110100110111001001110110010011100110100011011101100011101011' },
  { text: 'Hello, World!', symbols: [104, 40, 69, 76, 76, 79, 12, 0, 55, 79, 82, 76, 68, 1, 76, 106], checksum: 76, modules: '1101001000011000101000101100100001100101000011001010000100011110101011001110011011001100111010001101000111101010010011110110010100001000010011011001101100110010100001100011101011' },
  { text: '1234567890', symbols: [105, 12, 34, 56, 78, 90, 85, 106], checksum: 85, modules: '110100111001011001110010001011000111000101101100001010011011110110100111100101100011101011' },
  { text: '12', symbols: [105, 12, 14, 106], checksum: 14, modules: '1101001110010110011100100110011101100011101011' },
  { text: 'ART-123456', symbols: [104, 33, 50, 52, 13, 99, 12, 34, 56, 50, 106], checksum: 50, modules: '110100100001010001100011000101110110111000101001101110010111011110101100111001000101100011100010110110001011101100011101011' },
  { text: 'A123456B', symbols: [104, 33, 99, 12, 34, 56, 100, 34, 80, 106], checksum: 80, modules: '1101001000010100011000101110111101011001110010001011000111000101101011110111010001011000101001111001100011101011' },
  { text: '12345', symbols: [104, 17, 99, 23, 45, 53, 106], checksum: 53, modules: '1101001000010011100110101110111101110110111010111011000110111011101100011101011' },
]

/** Liest eine Symbolfolge nach den Regeln von Code 128 (Start, Umschalter, Prüfsumme, Stopp) zurück in Text. */
function decodeSymbols(symbols: readonly number[]): string {
  expect(symbols.at(-1)).toBe(STOP)
  let sum = symbols[0]
  for (let i = 1; i < symbols.length - 2; i++) sum += symbols[i] * i
  expect(symbols.at(-2)).toBe(sum % 103)

  let set: 'B' | 'C' = symbols[0] === START_B ? 'B' : 'C'
  expect([START_B, START_C]).toContain(symbols[0])
  let text = ''
  for (const value of symbols.slice(1, -2)) {
    if (set === 'B' && value === CODE_C) set = 'C'
    else if (set === 'C' && value === CODE_B) set = 'B'
    else if (set === 'C') text += String(value).padStart(2, '0')
    else text += String.fromCharCode(value + 32)
  }
  return text
}

describe('Code-128-Encoder (Frontend)', () => {
  it.each(VECTORS)('kodiert "$text" wie der Backend-Encoder (Symbole, Prüfsumme, Modulfolge)', ({ text, symbols, checksum, modules }) => {
    const barcode = encodeCode128(text)

    expect(barcode.text).toBe(text)
    expect(barcode.symbols).toEqual(symbols)
    expect(barcode.checksum).toBe(checksum)
    expect(barcode.symbols.at(-2)).toBe(checksum)
    expect(barcode.modules).toBe(modules)
    expect(barcode.moduleCount).toBe(modules.length)
    expect(barcode.widths.reduce((a, b) => a + b, 0)).toBe(modules.length)
  })

  it('ORD-DEMO-01: Start B, elf Zeichen, Prüfsumme 11, Stopp — 14 Symbole, 156 Module', () => {
    const barcode = encodeCode128('ORD-DEMO-01')

    expect(barcode.symbols[0]).toBe(START_B)
    expect(barcode.symbols.at(-1)).toBe(STOP)
    expect(barcode.symbols).toHaveLength(14)
    expect(barcode.moduleCount).toBe(13 * 11 + 13)
    // von Hand: 104 + 47*1 + 50*2 + 36*3 + 13*4 + 36*5 + 37*6 + 45*7 + 47*8 + 13*9 + 16*10 + 17*11 = 1968; 1968 mod 103 = 11
    expect(1968 % 103).toBe(11)
    expect(barcode.checksum).toBe(11)
  })

  it('nutzt die Normmuster für Start, Stopp und bekannte Zeichen', () => {
    expect(encodeCode128('A').modules.startsWith('11010010000')).toBe(true)      // Start B
    expect(encodeCode128('12').modules.startsWith('11010011100')).toBe(true)     // Start C
    expect(encodeCode128('A').modules.endsWith('1100011101011')).toBe(true)      // Stopp (13 Module)
    expect(encodeCode128(' ').modules.slice(11, 22)).toBe('11011001100')         // Wert 0 = Leerzeichen
    expect(encodeCode128('A').modules.slice(11, 22)).toBe('10100011000')         // Wert 33 = "A"
    expect(encodeCode128('0').modules.slice(11, 22)).toBe('10011101100')         // Wert 16 = "0"
  })

  it('jedes Symbol hat 11 Module, drei Balken, gerade Zahl schwarzer Module und kommt nur einmal vor', () => {
    const patterns = new Map<string, string>()
    for (let c = 32; c <= 126; c++) patterns.set(`B${c - 32}`, encodeCode128(String.fromCharCode(c)).modules.slice(11, 22))
    for (let pair = 95; pair <= 99; pair++) patterns.set(`C${pair}`, encodeCode128(String(pair)).modules.slice(11, 22))
    patterns.set('CodeB', encodeCode128('A123456B').modules.slice(66, 77))
    patterns.set('StartB', encodeCode128('A').modules.slice(0, 11))
    patterns.set('StartC', encodeCode128('12').modules.slice(0, 11))

    for (const [name, bits] of patterns) {
      expect(bits, name).toHaveLength(11)
      expect([...bits].filter((b) => b === '1').length % 2, `${name}: gerade Zahl schwarzer Module`).toBe(0)
      expect(bits.match(/1+/g)?.length, `${name}: drei Balken`).toBe(3)
    }
    expect(new Set(patterns.values()).size).toBe(patterns.size)
    expect(patterns.size).toBe(95 + 5 + 3)
    // Ziffernpaare 00..94 sind dieselben Symbole wie die B-Zeichen gleichen Werts, "Code C" (99) dasselbe wie das Paar 99
    for (let pair = 0; pair <= 94; pair++) expect(encodeCode128(String(pair).padStart(2, '0')).modules.slice(11, 22)).toBe(patterns.get(`B${pair}`))
    expect(encodeCode128('A123456B').modules.slice(22, 33)).toBe(patterns.get('C99'))
  })

  it('wählt die kürzeste Folge und bleibt bei Gleichstand in Set B', () => {
    expect(encodeCode128('12').symbols.slice(0, 2)).toEqual([START_C, 12])
    // "A1234B": Wechsel nach C und zurück kostet zwei Symbole und spart zwei: Gleichstand, also in B bleiben
    expect(encodeCode128('A1234B').symbols.slice(0, 7)).not.toContain(CODE_C)
    expect(encodeCode128('A123456B').symbols).toContain(CODE_C)
    expect(encodeCode128('123456').symbols[0]).toBe(START_C)
    expect(encodeCode128('1234567').symbols[0]).toBe(START_B)   // ungerade: erste Ziffer in B, Rest in C
  })

  it('Balken sind die schwarzen Läufe der Modulfolge', () => {
    const barcode = encodeCode128('ORD-DEMO-01')
    const runs = [...barcode.modules.matchAll(/1+/g)].map((m) => ({ start: m.index, width: m[0].length }))

    expect(barRuns(barcode)).toEqual(runs)
    expect(runs).toHaveLength(43)   // 13 Symbole x 3 Balken + 4 im Stopp
  })

  it('die Symbole ergeben für viele Zufallstexte wieder genau den Text (Prüfsumme stimmt, nie länger als reines Set B)', () => {
    // deterministischer Zufall (LCG), stark ziffernlastig, damit alle Wechsel vorkommen
    let seed = 26
    const next = (max: number) => {
      seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0
      return (seed >>> 8) % max
    }
    const alphabet = '0123456789012345678901234567890123456789ABCxyz-/. ~'
    for (let round = 0; round < 500; round++) {
      const length = 1 + next(24)
      const text = Array.from({ length }, () => alphabet[next(alphabet.length)]).join('')
      const barcode = encodeCode128(text)

      expect(decodeSymbols(barcode.symbols)).toBe(text)
      expect(barcode.symbols.length).toBeLessThanOrEqual(text.length + 3)
      expect(barcode.moduleCount).toBe(11 * (barcode.symbols.length - 1) + 13)
    }
  })

  it.each(['', 'Größe', 'Tab\there', 'Zeile\nzwei', '\u007f', 'Emoji 😀'])('lehnt "%s" ab (nicht in Set B/C darstellbar)', (text) => {
    expect(canEncodeCode128(text)).toBe(false)
    expect(code128Problem(text)).toEqual(expect.any(String))
    expect(() => encodeCode128(text)).toThrow(Code128Error)
  })

  it('nennt das störende Zeichen und akzeptiert die Grenzen ASCII 32 und 126', () => {
    expect(code128Problem('BIN-Ü')).toContain('U+00DC')
    expect(code128Problem(null)).not.toBeNull()
    expect(canEncodeCode128(undefined)).toBe(false)
    expect(canEncodeCode128(' ~')).toBe(true)
    expect(code128Problem(' ~')).toBeNull()
  })
})
