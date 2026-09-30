import { describe, expect, it } from 'vitest'
import { encodeCode128 } from '../../features/labels/code128'
import {
  LABEL_FORMATS, MAX_COPIES, MIN_MODULE_WIDTH_MM, clampCopies, clampStartPosition, expandCopies, isBarcodeTooDense, labelsPerPage,
  moduleWidthMm, pageCount, paginate, slotOrigin,
} from '../../features/labels/labelLayout'

const A4 = LABEL_FORMATS.a4
const SINGLE = LABEL_FORMATS.single
const items = (n: number) => Array.from({ length: n }, (_, i) => `L${i + 1}`)

describe('Etiketten je Seite', () => {
  it('der A4-Bogen 3 × 8 fasst 24 Etiketten, das Einzeletikett eines je Seite', () => {
    expect(A4.columns).toBe(3)
    expect(A4.rows).toBe(8)
    expect(labelsPerPage(A4)).toBe(24)
    expect(labelsPerPage(SINGLE)).toBe(1)
    // das Raster füllt den Bogen: 3 × 70 = 210 mm Breite, 8 × 36 = 288 mm plus 2 × 4,5 mm Rand = 297 mm
    expect(A4.columns * A4.labelWidthMm + A4.marginLeftMm * 2).toBe(A4.pageWidthMm)
    expect(A4.rows * A4.labelHeightMm + A4.marginTopMm * 2).toBe(A4.pageHeightMm)
  })

  it('verteilt Etiketten auf Seiten zu je 24: 24 → 1 Seite, 25 → 2, 48 → 2, 49 → 3', () => {
    expect(paginate(items(0), A4)).toEqual([])
    expect(paginate(items(1), A4)).toHaveLength(1)
    expect(paginate(items(24), A4)).toHaveLength(1)
    expect(paginate(items(25), A4)).toHaveLength(2)
    expect(paginate(items(48), A4)).toHaveLength(2)
    expect(paginate(items(49), A4)).toHaveLength(3)
    expect(paginate(items(25), A4).map((page) => page.length)).toEqual([24, 1])
    expect(paginate(items(3), SINGLE)).toHaveLength(3)
    expect(pageCount(0, A4)).toBe(0)
    expect(pageCount(24, A4)).toBe(1)
    expect(pageCount(25, A4)).toBe(2)
    expect(pageCount(7, SINGLE)).toBe(7)
  })

  it('eine Startposition lässt die ersten Plätze frei (angebrochener Bogen) und verschiebt den Seitenumbruch', () => {
    const pages = paginate(items(3), A4, 23)

    expect(pages).toHaveLength(2)
    expect(pages[0]).toHaveLength(24)
    expect(pages[0].slice(0, 22).every((slot) => slot === null)).toBe(true)
    expect(pages[0].slice(22)).toEqual(['L1', 'L2'])
    expect(pages[1]).toEqual(['L3'])
    expect(pageCount(3, A4, 23)).toBe(2)
    expect(pageCount(2, A4, 23)).toBe(1)
    expect(pageCount(24, A4, 2)).toBe(2)              // 23 freie Plätze passen nicht mehr: ein Etikett rutscht auf Seite 2
    expect(pageCount(23, A4, 2)).toBe(1)
    // Einzeletikett: eine Startposition gibt es nicht
    expect(paginate(items(2), SINGLE, 5)).toEqual([['L1'], ['L2']])
  })

  it('klemmt Kopienzahl und Startposition auf den erlaubten Bereich', () => {
    expect(clampCopies(0)).toBe(1)
    expect(clampCopies(Number.NaN)).toBe(1)
    expect(clampCopies(-4)).toBe(1)
    expect(clampCopies(2.9)).toBe(2)
    expect(clampCopies(10_000)).toBe(MAX_COPIES)
    expect(clampStartPosition(0, A4)).toBe(1)
    expect(clampStartPosition(99, A4)).toBe(24)
    expect(clampStartPosition(Number.NaN, A4)).toBe(1)
    expect(clampStartPosition(7, SINGLE)).toBe(1)
  })

  it('Kopien folgen direkt aufeinander (A A B B), sodass sie beim Schneiden zusammenliegen', () => {
    expect(expandCopies(['A', 'B'], 3)).toEqual(['A', 'A', 'A', 'B', 'B', 'B'])
    expect(expandCopies(['A'], 0)).toEqual(['A'])
    // 9 Bin-Etiketten × 3 Kopien = 27 Etiketten = 2 Bögen
    expect(pageCount(expandCopies(items(9), 3).length, A4)).toBe(2)
  })

  it('setzt die Plätze zeilenweise auf den Bogen: links nach rechts, oben nach unten', () => {
    expect(slotOrigin(A4, 0)).toEqual({ leftMm: 0, topMm: 4.5 })
    expect(slotOrigin(A4, 1)).toEqual({ leftMm: 70, topMm: 4.5 })
    expect(slotOrigin(A4, 2)).toEqual({ leftMm: 140, topMm: 4.5 })
    expect(slotOrigin(A4, 3)).toEqual({ leftMm: 0, topMm: 4.5 + 36 })
    expect(slotOrigin(A4, 23)).toEqual({ leftMm: 140, topMm: 4.5 + 7 * 36 })
    expect(slotOrigin(SINGLE, 0)).toEqual({ leftMm: 0, topMm: 0 })
  })
})

describe('Lesbarkeit: Modulbreite', () => {
  it('ein Bin-Code passt bequem auf beide Formate', () => {
    const barcode = encodeCode128('A-01-01')
    // 112 Module + 20 Ruhezone auf 46 mm (50 mm Etikett) bzw. 66 mm (70 mm Etikett)
    expect(moduleWidthMm(barcode, SINGLE)).toBeCloseTo(46 / 132, 5)
    expect(moduleWidthMm(barcode, A4)).toBeCloseTo(66 / 132, 5)
    expect(isBarcodeTooDense(barcode, SINGLE)).toBe(false)
    expect(isBarcodeTooDense(barcode, A4)).toBe(false)
  })

  it('ein längerer Code wird auf dem kleinen Etikett zu dicht, auf dem A4-Bogen noch nicht; ein sehr langer auf beiden', () => {
    const medium = encodeCode128('ORD-2026-0001-ABCDEFGH')                          // 22 Zeichen, 277 Module
    const veryLong = encodeCode128('LAGERPLATZ-HALLE-2-REGAL-14-FACH-0007-X')       // 39 Zeichen, 464 Module

    expect(medium.moduleCount).toBe(277)
    expect(moduleWidthMm(medium, SINGLE)).toBeCloseTo(46 / 297, 5)                   // 0,155 mm < 0,19 mm
    expect(moduleWidthMm(medium, A4)).toBeCloseTo(66 / 297, 5)                       // 0,222 mm
    expect(isBarcodeTooDense(medium, SINGLE)).toBe(true)
    expect(isBarcodeTooDense(medium, A4)).toBe(false)

    expect(veryLong.moduleCount).toBe(464)
    expect(MIN_MODULE_WIDTH_MM).toBe(0.19)
    expect(isBarcodeTooDense(veryLong, SINGLE)).toBe(true)
    expect(isBarcodeTooDense(veryLong, A4)).toBe(true)                               // 66 / 484 = 0,136 mm
  })
})
