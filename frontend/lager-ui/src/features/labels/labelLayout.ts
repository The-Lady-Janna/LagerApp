import { QUIET_ZONE_MODULES, type Code128Barcode } from './code128'

/// <summary>
/// Geometrie der Druckformate (alles in Millimetern): Einzeletikett für Rollen-/Etikettendrucker und der A4-Bogen mit
/// Rasteretiketten. Reine Funktionen ohne DOM — die Vorschau (LabelSheet.tsx) und die Tests nutzen dieselben Zahlen.
/// </summary>

export type LabelFormatId = 'single' | 'a4'

export interface LabelFormat {
  id: LabelFormatId
  /** Anzeigename in der Auswahl. */
  name: string
  pageWidthMm: number
  pageHeightMm: number
  columns: number
  rows: number
  labelWidthMm: number
  labelHeightMm: number
  /** Abstand des Rasters vom linken/oberen Seitenrand. */
  marginLeftMm: number
  marginTopMm: number
  /** Lücke zwischen zwei Etiketten (0 bei Bögen ohne Zwischenraum). */
  gapXMm: number
  gapYMm: number
}

/**
 * Die Formate. Der A4-Bogen ist der handelsübliche Bogen mit 24 Etiketten zu 70 × 36 mm ohne Zwischenraum
 * (3 Spalten × 8 Zeilen, oben und unten je 4,5 mm frei). Andere Raster (mit Zwischenräumen, andere Größe) lassen sich
 * hier anpassen; die Vorschau und der Druck richten sich nach diesen Werten.
 */
export const LABEL_FORMATS: Readonly<Record<LabelFormatId, LabelFormat>> = {
  single: {
    id: 'single', name: 'Einzeletikett 50 × 30 mm (Rolle)',
    pageWidthMm: 50, pageHeightMm: 30, columns: 1, rows: 1, labelWidthMm: 50, labelHeightMm: 30,
    marginLeftMm: 0, marginTopMm: 0, gapXMm: 0, gapYMm: 0,
  },
  a4: {
    id: 'a4', name: 'A4-Bogen 3 × 8 (24 Etiketten à 70 × 36 mm)',
    pageWidthMm: 210, pageHeightMm: 297, columns: 3, rows: 8, labelWidthMm: 70, labelHeightMm: 36,
    marginLeftMm: 0, marginTopMm: 4.5, gapXMm: 0, gapYMm: 0,
  },
}

/** Obergrenze der Kopienzahl je Etikett in der Oberfläche (der Server erlaubt für ZPL bis 500). */
export const MAX_COPIES = 100

/**
 * Höchstzahl der Etiketten (samt Kopien) in einem Druckauftrag im Browser (rund 40 Bögen). Darüber wäre die Vorschau mit
 * zehntausenden SVG-Elementen zäh und ein Papierdruck kaum gewollt; für große Mengen dient der ZPL-Download (Kopien per ^PQ im Drucker).
 */
export const MAX_PRINT_LABELS = 1000

/** Innenabstand im Etikett (Rand um Barcode und Text). */
export const LABEL_PADDING_MM = 2

/** Kleinste Modulbreite, bei der ein Barcode noch zuverlässig lesbar ist (7,5 mil, üblicher Richtwert für Code 128). */
export const MIN_MODULE_WIDTH_MM = 0.19

/** Etiketten je Seite (24 beim A4-Bogen, 1 beim Einzeletikett). */
export const labelsPerPage = (format: LabelFormat): number => format.columns * format.rows

/** Die Kopienzahl als ganze Zahl zwischen 1 und {@link MAX_COPIES} (ungültige Eingaben werden zu 1). */
export function clampCopies(value: number): number {
  if (!Number.isFinite(value)) return 1
  return Math.min(MAX_COPIES, Math.max(1, Math.trunc(value)))
}

/** Die erste belegte Position auf dem Bogen (1-basiert): nur bei Bögen mit mehreren Etiketten sinnvoll, sonst immer 1. */
export function clampStartPosition(value: number, format: LabelFormat): number {
  const perPage = labelsPerPage(format)
  if (perPage <= 1 || !Number.isFinite(value)) return 1
  return Math.min(perPage, Math.max(1, Math.trunc(value)))
}

/** Jedes Etikett `copies`-mal, die Kopien eines Etiketts direkt hintereinander (so liegen sie beim Schneiden zusammen). */
export function expandCopies<T>(items: readonly T[], copies: number): T[] {
  const count = clampCopies(copies)
  return items.flatMap((item) => Array.from({ length: count }, () => item))
}

/**
 * Verteilt die Etiketten auf Seiten: je Seite `labelsPerPage` Plätze, von links nach rechts und oben nach unten. Auf der
 * ersten Seite bleiben `startPosition - 1` Plätze leer (angebrochener Bogen); leere Plätze sind `null`. Die letzte Seite
 * ist nicht aufgefüllt. Ohne Etiketten gibt es keine Seite.
 */
export function paginate<T>(items: readonly T[], format: LabelFormat, startPosition = 1): (T | null)[][] {
  if (items.length === 0) return []
  const perPage = labelsPerPage(format)
  const skipped = clampStartPosition(startPosition, format) - 1
  const slots: (T | null)[] = [...Array.from({ length: skipped }, () => null), ...items]
  const pages: (T | null)[][] = []
  for (let i = 0; i < slots.length; i += perPage) pages.push(slots.slice(i, i + perPage))
  return pages
}

/** Anzahl der Seiten für so viele Etiketten (inkl. der frei gelassenen Plätze am Anfang). */
export function pageCount(labelCount: number, format: LabelFormat, startPosition = 1): number {
  if (labelCount <= 0) return 0
  return Math.ceil((clampStartPosition(startPosition, format) - 1 + labelCount) / labelsPerPage(format))
}

/** Linke obere Ecke des Platzes `slot` (0-basiert, zeilenweise) auf der Seite. */
export function slotOrigin(format: LabelFormat, slot: number): { leftMm: number; topMm: number } {
  const column = slot % format.columns
  const row = Math.floor(slot / format.columns)
  return {
    leftMm: format.marginLeftMm + column * (format.labelWidthMm + format.gapXMm),
    topMm: format.marginTopMm + row * (format.labelHeightMm + format.gapYMm),
  }
}

/** Breite des Barcodes im Etikett in Millimetern (Etikettenbreite abzüglich Innenabstand). */
export const barcodeWidthMm = (format: LabelFormat): number => format.labelWidthMm - 2 * LABEL_PADDING_MM

/**
 * Breite eines Moduls (des schmalsten Balkens) in Millimetern, wenn der Barcode samt Ruhezonen die volle Breite des
 * Etiketts ausfüllt (so zeichnet ihn die Vorschau). Je länger der Code, desto schmaler die Balken.
 */
export function moduleWidthMm(barcode: Pick<Code128Barcode, 'moduleCount'>, format: LabelFormat): number {
  return barcodeWidthMm(format) / (barcode.moduleCount + 2 * QUIET_ZONE_MODULES)
}

/** Ist der Barcode auf diesem Format zu dicht (Modul schmaler als {@link MIN_MODULE_WIDTH_MM}) und deshalb unsicher lesbar? */
export const isBarcodeTooDense = (barcode: Pick<Code128Barcode, 'moduleCount'>, format: LabelFormat): boolean =>
  moduleWidthMm(barcode, format) < MIN_MODULE_WIDTH_MM
