import { formatNumber } from '../../lib/format'
import { t } from '../../i18n'

/// <summary>
/// Erfassungsmaske Wareneingang: die Zeilen der Maske, ihre Prüfung vor dem Senden und der Request-Body. Reine Logik ohne
/// React, damit sie sich einzeln testen lässt. Die Regeln entsprechen dem Server (Zeilen werden dort beim Anlegen noch einmal
/// geprüft): Menge 1 bis 1.000.000, Charge höchstens 64 Zeichen, MHD nach dem Jahr 2000, dieselbe Charge im selben Lagerplatz
/// nur mit einem MHD. Dazu kommen die Regeln der Maske: kein MHD in der Vergangenheit (Neuware) und Charge + MHD als Pflicht
/// bei Artikeln, die schon mit MHD geführt werden.
/// </summary>

/** Höchstmenge je Zeile (wie der Server). */
export const MAX_QUANTITY = 1_000_000
/** Längste Chargennummer (Spaltenbreite). */
export const MAX_LOT_LENGTH = 64

/** Eine Zeile der Maske; alle Felder sind Formularwerte (Text), das MHD als "yyyy-MM-dd" oder leer. */
export interface InboundLineDraft {
  articleId: string
  targetBinId: string
  quantity: number
  lotNumber: string
  expiryDate: string
}

/** Eine geprüfte Zeile im Request-Body von POST /api/inbound. */
export interface InboundLinePayload {
  articleId: string
  targetBinId: string
  quantity: number
  lotNumber: string | null
  expiryDate: string | null
}

export function emptyLine(): InboundLineDraft {
  return { articleId: '', targetBinId: '', quantity: 1, lotNumber: '', expiryDate: '' }
}

/** Eine Zeile, in die der Nutzer noch nichts eingetragen hat (die Menge ist mit 1 vorbelegt und zählt nicht). */
export function isUntouched(line: InboundLineDraft): boolean {
  return !line.articleId && !line.targetBinId && !line.lotNumber.trim() && !line.expiryDate
}

export interface InboundLineContext {
  /** Heute als "yyyy-MM-dd" (Ortszeit); ein MHD davor ist bei Neuware ein Fehler. */
  today: string
  /** Führt der Artikel schon Chargen mit MHD (Bestand mit MHD)? Dann sind Charge und MHD Pflicht. */
  tracksExpiry: (articleId: string) => boolean
}

export interface InboundLineCheck {
  /** Meldungen für den Nutzer, jede mit Zeilennummer; leer = alles in Ordnung. */
  problems: string[]
  /** Die zu sendenden Zeilen (nur befüllte, in Reihenfolge der Maske); nur gültig, wenn problems leer ist. */
  lines: InboundLinePayload[]
}

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/

function isRealDate(value: string): boolean {
  const m = ISO_DATE.exec(value)
  if (!m) return false
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])]
  const date = new Date(Date.UTC(y, mo - 1, d))
  return date.getUTCFullYear() === y && date.getUTCMonth() === mo - 1 && date.getUTCDate() === d
}

/**
 * Prüft die Zeilen der Maske. Leere Zeilen werden übergangen; eine teilweise befüllte Zeile ist ein Fehler (sie würde sonst
 * still wegfallen). Ohne eine einzige befüllte Zeile gibt es keinen Wareneingang.
 */
export function checkInboundLines(drafts: readonly InboundLineDraft[], ctx: InboundLineContext): InboundLineCheck {
  const problems: string[] = []
  const lines: InboundLinePayload[] = []
  // (Artikel|Bin|Charge) -> [MHD, Zeilennummer der ersten Nennung]: eine Charge hat je Lagerplatz genau ein MHD.
  const expiryOfLot = new Map<string, { expiry: string; no: number }>()

  drafts.forEach((line, index) => {
    if (isUntouched(line)) return
    const no = index + 1
    const before = problems.length
    const lot = line.lotNumber.trim()

    if (!line.articleId) problems.push(t('traceability:lines.article', { no }))
    if (!line.targetBinId) problems.push(t('traceability:lines.bin', { no }))
    if (!Number.isInteger(line.quantity) || line.quantity < 1 || line.quantity > MAX_QUANTITY) {
      problems.push(t('traceability:lines.quantity', { no, max: formatNumber(MAX_QUANTITY) }))
    }
    if (lot.length > MAX_LOT_LENGTH) problems.push(t('traceability:lines.lotLength', { no, max: MAX_LOT_LENGTH }))

    if (line.expiryDate) {
      if (!isRealDate(line.expiryDate)) problems.push(t('traceability:lines.expiryInvalid', { no }))
      else if (line.expiryDate < '2000-01-01') problems.push(t('traceability:lines.expiryYear', { no }))
      else if (line.expiryDate < ctx.today) problems.push(t('traceability:lines.expiryPast', { no }))
    }

    if (line.articleId && ctx.tracksExpiry(line.articleId)) {
      if (!lot) problems.push(t('traceability:lines.lotRequired', { no }))
      if (!line.expiryDate) problems.push(t('traceability:lines.expiryRequired', { no }))
    }

    if (lot && line.articleId && line.targetBinId) {
      const key = `${line.articleId}|${line.targetBinId}|${lot}`
      const known = expiryOfLot.get(key)
      if (known && known.expiry !== line.expiryDate) {
        problems.push(t('traceability:lines.lotExpiryConflict', { no, lot, other: known.no }))
      } else if (!known) {
        expiryOfLot.set(key, { expiry: line.expiryDate, no })
      }
    }

    if (problems.length === before) {
      lines.push({
        articleId: line.articleId,
        targetBinId: line.targetBinId,
        quantity: line.quantity,
        lotNumber: lot || null,
        expiryDate: line.expiryDate || null,
      })
    }
  })

  if (problems.length === 0 && lines.length === 0) problems.push(t('traceability:lines.none'))
  return { problems, lines }
}

/** Lieferschein-Nummer für eine Lieferung ohne eigene Angabe ("Auto wenn leer"): WE-yyyyMMdd-HHmmss (Ortszeit). */
export function autoShipmentNumber(now: Date = new Date()): string {
  const p = (n: number) => String(n).padStart(2, '0')
  return `WE-${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}${p(now.getSeconds())}`
}
