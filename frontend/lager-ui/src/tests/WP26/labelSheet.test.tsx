import { describe, expect, it } from 'vitest'
import { render, within } from '@testing-library/react'
import { LabelSheet } from '../../features/labels/LabelSheet'
import { Code128Svg } from '../../features/labels/Code128Svg'
import { QUIET_ZONE_MODULES, barRuns, encodeCode128 } from '../../features/labels/code128'
import { LABEL_FORMATS, expandCopies } from '../../features/labels/labelLayout'
import type { LabelData } from '../../features/labels/labelSelection'

const A4 = LABEL_FORMATS.a4
const SINGLE = LABEL_FORMATS.single

/** Ein Maß aus dem Inline-Style in Millimetern (jsdom darf 0mm als 0px ausgeben; es zählt der Zahlenwert). */
const mm = (element: HTMLElement, property: 'left' | 'top' | 'width' | 'height') => parseFloat(element.style[property])

const bin = (code: string): LabelData => ({ key: `bin:${code}`, kind: 'bin', id: code, code, detail: 'Regal S1' })
const bins = (n: number) => Array.from({ length: n }, (_, i) => bin(`A-01-${String(i + 1).padStart(2, '0')}`))

describe('Code128Svg', () => {
  it('zeichnet ein Rechteck je Balken in Modulen, mit Ruhezone links und rechts', () => {
    const barcode = encodeCode128('ORD-DEMO-01')
    const { container } = render(<Code128Svg barcode={barcode} />)
    const svg = container.querySelector('svg')!
    const rects = [...svg.querySelectorAll('rect')]

    expect(svg.getAttribute('viewBox')).toBe(`0 0 ${barcode.moduleCount + 2 * QUIET_ZONE_MODULES} 1`)
    expect(svg.getAttribute('preserveAspectRatio')).toBe('none')
    expect(svg).toHaveAccessibleName('Code 128: ORD-DEMO-01')
    expect(rects).toHaveLength(43)
    expect(rects.map((r) => ({ start: Number(r.getAttribute('x')) - QUIET_ZONE_MODULES, width: Number(r.getAttribute('width')) }))).toEqual(barRuns(barcode))
    // die Farbe kommt vom Etikett (Schwarz), nicht vom Theme
    expect(rects.every((r) => r.getAttribute('fill') === 'currentColor')).toBe(true)
    expect(svg.getAttribute('data-modules')).toBe(barcode.modules)
  })
})

describe('LabelSheet: A4-Bogen 3 × 8', () => {
  const cells = (container: HTMLElement) => [...container.querySelectorAll<HTMLElement>('.label-cell')]
  const pagesOf = (container: HTMLElement) => [...container.querySelectorAll<HTMLElement>('.label-page')]

  it('24 Etiketten füllen genau eine Seite, das 25. beginnt die zweite', () => {
    const one = render(<LabelSheet labels={bins(24)} format={A4} />)
    expect(pagesOf(one.container)).toHaveLength(1)
    expect(cells(one.container)).toHaveLength(24)
    one.unmount()

    const two = render(<LabelSheet labels={bins(25)} format={A4} />)
    const pages = pagesOf(two.container)
    expect(pages).toHaveLength(2)
    expect(cells(pages[0])).toHaveLength(24)
    expect(cells(pages[1])).toHaveLength(1)
  })

  it('setzt die Etiketten im 3-Spalten-Raster auf den Bogen (Maße in mm) und kodiert jeden Code', () => {
    const { container } = render(<LabelSheet labels={bins(4)} format={A4} />)
    const [first, second, third, fourth] = cells(container)

    expect(mm(first, 'left')).toBe(0)
    expect(mm(first, 'top')).toBe(4.5)
    expect(mm(first, 'width')).toBe(70)
    expect(mm(first, 'height')).toBe(36)
    expect(mm(second, 'left')).toBe(70)
    expect(mm(third, 'left')).toBe(140)
    expect(mm(fourth, 'left')).toBe(0)
    expect(mm(fourth, 'top')).toBe(40.5)

    // Klartext und Barcode stimmen mit dem Code überein; darunter das Kleingedruckte
    expect(within(first).getByText('A-01-01')).toHaveClass('label-code')
    expect(within(first).getByText('Regal S1')).toBeInTheDocument()
    expect(first.querySelector('svg')!.getAttribute('data-modules')).toBe(encodeCode128('A-01-01').modules)
    expect(fourth.querySelector('svg')!.getAttribute('data-modules')).toBe(encodeCode128('A-01-04').modules)
  })

  it('Kopien landen nebeneinander: 3 Etiketten × 8 Kopien = 24 = eine volle Seite', () => {
    const { container } = render(<LabelSheet labels={expandCopies(bins(3), 8)} format={A4} />)

    expect(pagesOf(container)).toHaveLength(1)
    expect(cells(container).map((c) => c.dataset.code).slice(0, 9)).toEqual([
      'A-01-01', 'A-01-01', 'A-01-01', 'A-01-01', 'A-01-01', 'A-01-01', 'A-01-01', 'A-01-01', 'A-01-02',
    ])
  })

  it('eine Startposition lässt die ersten Plätze des Bogens frei', () => {
    const { container } = render(<LabelSheet labels={bins(3)} format={A4} startPosition={23} />)
    const pages = pagesOf(container)

    expect(pages).toHaveLength(2)
    expect(cells(pages[0]).map((c) => c.dataset.code)).toEqual(['A-01-01', 'A-01-02'])
    expect(mm(cells(pages[0])[0], 'left')).toBe(70)          // Platz 23 = Zeile 8, Spalte 2
    expect(mm(cells(pages[0])[0], 'top')).toBe(256.5)
    expect(mm(cells(pages[0])[1], 'left')).toBe(140)
    expect(cells(pages[1]).map((c) => c.dataset.code)).toEqual(['A-01-03'])
    expect(mm(cells(pages[1])[0], 'left')).toBe(0)
  })

  it('legt die Seitengröße für den Druck fest (@page) und benennt die Seiten', () => {
    const { container } = render(<LabelSheet labels={bins(25)} format={A4} />)

    expect(container.querySelector('style')!.textContent).toContain('@page { size: 210mm 297mm; margin: 0; }')
    expect(pagesOf(container)[1]).toHaveAccessibleName('Seite 2 von 2')
    expect(container.querySelector('.label-sheets')).toHaveAttribute('data-pages', '2')
  })
})

describe('LabelSheet: Einzeletikett 50 × 30 mm', () => {
  it('jedes Etikett ist eine eigene Seite von 50 × 30 mm; @page passt sich an', () => {
    const { container } = render(<LabelSheet labels={bins(3)} format={SINGLE} />)
    const pages = [...container.querySelectorAll<HTMLElement>('.label-page')]

    expect(pages).toHaveLength(3)
    expect(mm(pages[0], 'width')).toBe(50)
    expect(container.querySelectorAll('.label-cell')).toHaveLength(3)
    expect(container.querySelector('style')!.textContent).toContain('@page { size: 50mm 30mm; margin: 0; }')
    const cell = pages[0].querySelector<HTMLElement>('.label-cell')!
    expect(mm(cell, 'left')).toBe(0)
    expect(mm(cell, 'width')).toBe(50)
    expect(mm(cell, 'height')).toBe(30)
  })

  it('zeigt Artikelname und Kundenreferenz als Zeilen unter dem Code', () => {
    const labels: LabelData[] = [
      { key: 'article:1', kind: 'article', id: '1', code: 'ART-1', title: 'Schraube M8' },
      { key: 'order:1', kind: 'order', id: '1', code: 'ORD-1', detail: 'Kunde: K-1' },
    ]
    const { container } = render(<LabelSheet labels={labels} format={SINGLE} />)

    expect(within(container).getByText('Schraube M8')).toHaveClass('label-title')
    expect(within(container).getByText('Kunde: K-1')).toHaveClass('label-detail')
  })

  it('ein Code, den Code 128 nicht darstellen kann, erscheint als Text mit Hinweis statt eines falschen Barcodes', () => {
    const { container } = render(<LabelSheet labels={[bin('Größe-1')]} format={SINGLE} />)

    expect(container.querySelector('svg')).toBeNull()
    expect(within(container).getByText('Code nicht als Code 128 darstellbar')).toBeInTheDocument()
    expect(within(container).getByText('Größe-1')).toHaveClass('label-code')
  })

  it('ohne Etiketten gibt es keine Seite', () => {
    const { container } = render(<LabelSheet labels={[]} format={A4} />)

    expect(container.querySelectorAll('.label-page')).toHaveLength(0)
  })
})
