import { t } from '../../i18n'

/// <summary>
/// Alternativ-SKUs (Ersatzartikel bei Out-of-Stock) im Editor: Eingabe prüfen, bevor sie als Chip aufgenommen wird.
/// Die Grenzen entsprechen dem Server-Validator (je höchstens 64 Zeichen, kein Komma, höchstens 500 Einträge, zusammen höchstens
/// 1000 Zeichen im gespeicherten Text).
/// </summary>

const MAX_SKU_LENGTH = 64
const MAX_ITEMS = 500
/** Spaltenbreite von Articles.AlternativeSkusCsv: alle SKUs zusammen, mit Komma verbunden. */
const MAX_STORED_LENGTH = 1000

export type AltSkuCheck = { ok: true; sku: string } | { ok: false; message: string }

/** Zerlegt eine Eingabe/Einfügung ("A, B; C") in einzelne SKUs (Trennzeichen: Komma, Semikolon, Zeilenumbruch), getrimmt, ohne Leere. */
export function splitSkuInput(text: string): string[] {
  return text.split(/[,;\n\r]+/).map((s) => s.trim()).filter((s) => s !== '')
}

interface CheckInput {
  value: string
  /** SKU des bearbeiteten Artikels (er kann nicht sein eigener Ersatz sein); beim Neuanlegen die bisher getippte SKU. */
  ownSku: string
  /** Bereits aufgenommene Alternativ-SKUs. */
  existing: readonly string[]
  /** Alle bekannten SKUs: kleingeschrieben → SKU in der Schreibweise des Artikels; null = die Artikelliste ist noch nicht da. */
  knownSkus: ReadonlyMap<string, string> | null
}

/**
 * Prüft eine einzelne SKU: nicht leer, nicht zu lang, nicht der Artikel selbst, nicht doppelt (Groß-/Kleinschreibung egal) und
 * — der Existenz-Check — ein Artikel mit dieser SKU muss es geben. Bei Erfolg die SKU in der Schreibweise des Artikels.
 */
export function checkAlternativeSku({ value, ownSku, existing, knownSkus }: CheckInput): AltSkuCheck {
  const sku = value.trim()
  if (sku === '') return { ok: false, message: t('articles:altSku.enter') }
  if (sku.length > MAX_SKU_LENGTH) return { ok: false, message: t('articles:altSku.tooLong', { max: MAX_SKU_LENGTH }) }

  const lower = sku.toLowerCase()
  if (ownSku.trim().toLowerCase() === lower) return { ok: false, message: t('articles:altSku.self') }
  if (existing.some((e) => e.toLowerCase() === lower)) return { ok: false, message: t('articles:altSku.duplicate', { sku }) }
  if (existing.length >= MAX_ITEMS) return { ok: false, message: t('articles:altSku.tooMany', { max: MAX_ITEMS }) }
  const storedLength = (existing.length === 0 ? 0 : existing.join(',').length + 1) + sku.length
  if (storedLength > MAX_STORED_LENGTH) {
    return { ok: false, message: t('articles:altSku.totalTooLong', { max: MAX_STORED_LENGTH }) }
  }

  if (knownSkus === null) return { ok: false, message: t('articles:altSku.loading') }
  const known = knownSkus.get(lower)
  if (known === undefined) return { ok: false, message: t('articles:altSku.notFound', { sku }) }
  return { ok: true, sku: known }
}
