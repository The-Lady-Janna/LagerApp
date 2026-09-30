/// <summary>
/// Eingabegrenzen und Auswertung der Zahlenfelder der Lagerstruktur-Dialoge. Die Werte spiegeln die Validatoren des Servers
/// (WarehouseAdminValidators): Codes bis 64, Namen bis 256 Zeichen, Abmessungen 1..100000 mm, Höchstlast 0..100 t,
/// Nachschub-Schwelle 0..1.000.000. Der Server bleibt maßgeblich; die Dialoge sperren nur offensichtlich Ungültiges.
/// </summary>

export const MAX_CODE_LENGTH = 64
export const MAX_NAME_LENGTH = 256
export const MAX_DIMENSION_MM = 100_000
export const MAX_WEIGHT_KG = 100_000
export const MAX_THRESHOLD = 1_000_000

/** Ganze Zahl aus einem Textfeld (mit optionalem Vorzeichen); alles andere (leer, Dezimalzahl, Text) = null. */
export function parseWholeNumber(text: string): number | null {
  const trimmed = text.trim()
  return /^-?\d+$/.test(trimmed) ? Number(trimmed) : null
}

/** Abmessung in mm: ganze Zahl 1..100000, sonst null. */
export function parseDimension(text: string): number | null {
  const value = parseWholeNumber(text)
  return value !== null && value >= 1 && value <= MAX_DIMENSION_MM ? value : null
}

/** Höchstlast: Kilogramm (auch mit Dezimalstellen, Komma oder Punkt) 0..100000 als ganze Gramm; sonst null. */
export function parseKilogramsAsGrams(text: string): number | null {
  const trimmed = text.trim().replace(',', '.')
  if (!/^\d+(\.\d+)?$/.test(trimmed)) return null
  const kilograms = Number(trimmed)
  return kilograms <= MAX_WEIGHT_KG ? Math.round(kilograms * 1000) : null
}

/** Nachschub-Schwelle: ganze Zahl 0..1000000, sonst null. */
export function parseThreshold(text: string): number | null {
  const value = parseWholeNumber(text)
  return value !== null && value >= 0 && value <= MAX_THRESHOLD ? value : null
}

/** Gramm als Kilogramm-Text für ein Eingabefeld (50000 -> "50", 1500 -> "1.5"). */
export function gramsToKilogramText(grams: number): string {
  return String(grams / 1000)
}
