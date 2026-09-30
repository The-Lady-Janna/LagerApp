import { memo, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import './labels.css'
import { Code128Svg } from './Code128Svg'
import { canEncodeCode128, encodeCode128, type Code128Barcode } from './code128'
import { paginate, slotOrigin, type LabelFormat } from './labelLayout'
import type { LabelData } from './labelSelection'

interface Props {
  /** Die zu druckenden Etiketten (Kopien schon eingerechnet), in Druckreihenfolge. */
  labels: readonly LabelData[]
  format: LabelFormat
  /** Erste belegte Position auf dem ersten Bogen (1-basiert; angebrochener Bogen). */
  startPosition?: number
}

/**
 * Die Seiten mit den Etiketten: je Seite ein Bogen (A4: 24 Etiketten in 3 × 8) bzw. ein Einzeletikett, jedes Etikett mit
 * Code 128 als Inline-SVG, dem Code im Klartext und optionalen Zeilen darunter. Dieselbe Darstellung dient als
 * Vorschau am Bildschirm und als Druckvorlage: die Maße stehen in Millimetern, `@page` (Seitengröße, ohne Rand) kommt aus
 * dem gewählten Format und gilt nur, solange die Seite offen ist.
 */
export const LabelSheet = memo(function LabelSheet({ labels, format, startPosition = 1 }: Props) {
  const { t } = useTranslation()
  const pages = useMemo(() => paginate(labels, format, startPosition), [labels, format, startPosition])
  // Der Barcode hängt nur am Code: bei vielen Kopien wird jeder Code einmal kodiert.
  const barcodes = useMemo(() => {
    const byCode = new Map<string, Code128Barcode | null>()
    for (const label of labels) if (!byCode.has(label.code)) byCode.set(label.code, canEncodeCode128(label.code) ? encodeCode128(label.code) : null)
    return byCode
  }, [labels])

  return (
    <div className="label-sheets" data-format={format.id} data-pages={pages.length}>
      <style>{`@page { size: ${format.pageWidthMm}mm ${format.pageHeightMm}mm; margin: 0; }`}</style>
      {pages.map((slots, pageIndex) => (
        <section
          key={pageIndex}
          className="label-page"
          aria-label={t('labels:sheet.page', { n: pageIndex + 1, total: pages.length })}
          // Etwas niedriger als die Seite: exakt gleich hohe Blöcke erzeugen in manchen Browsern eine leere Folgeseite.
          style={{ width: `${format.pageWidthMm}mm`, height: `calc(${format.pageHeightMm}mm - 0.5mm)` }}
        >
          {slots.map((label, slot) => {
            if (!label) return null
            const { leftMm, topMm } = slotOrigin(format, slot)
            const barcode = barcodes.get(label.code)
            return (
              <div
                key={slot}
                className={`label-cell label-cell--${label.kind}`}
                data-code={label.code}
                style={{ left: `${leftMm}mm`, top: `${topMm}mm`, width: `${format.labelWidthMm}mm`, height: `${format.labelHeightMm}mm` }}
              >
                {barcode
                  ? <Code128Svg barcode={barcode} className="label-barcode" />
                  : <div className="label-barcode label-barcode--missing">{t('labels:sheet.missing')}</div>}
                <div className={label.code.length > 18 ? 'label-code label-code--long' : 'label-code'}>{label.code}</div>
                {label.title && <div className="label-title">{label.title}</div>}
                {label.detail && <div className="label-detail">{label.detail}</div>}
              </div>
            )
          })}
        </section>
      ))}
    </div>
  )
})
