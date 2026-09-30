import { QUIET_ZONE_MODULES, barRuns, type Code128Barcode } from './code128'

interface Props {
  barcode: Code128Barcode
  className?: string
}

/// <summary>
/// Ein Code-128-Barcode als Inline-SVG: ein Rechteck je Balken, Einheit ist ein Modul (viewBox-Breite = Module samt
/// Ruhezonen, Höhe 1). Die Größe bestimmt allein das CSS (Breite/Höhe des Elements); preserveAspectRatio="none" füllt
/// die Fläche, alle Balken skalieren waagerecht gleich, die Höhe ist beliebig. Die Balkenfarbe ist currentColor —
/// das Etikett setzt sie fest auf Schwarz (auch im Dark-Mode), sonst wäre der Code nicht lesbar.
/// </summary>
export function Code128Svg({ barcode, className }: Props) {
  const totalModules = barcode.moduleCount + 2 * QUIET_ZONE_MODULES
  return (
    <svg
      className={className}
      viewBox={`0 0 ${totalModules} 1`}
      preserveAspectRatio="none"
      shapeRendering="crispEdges"
      role="img"
      aria-label={`Code 128: ${barcode.text}`}
      data-modules={barcode.modules}
    >
      {barRuns(barcode).map(({ start, width }) => (
        <rect key={start} x={QUIET_ZONE_MODULES + start} y={0} width={width} height={1} fill="currentColor" />
      ))}
    </svg>
  )
}
