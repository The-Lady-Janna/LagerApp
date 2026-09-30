import { describe, expect, it } from 'vitest'
import { formatDate, formatDateOnly, formatDateTime, formatMoney, localDateTimeToUtcIso, parseServerDate } from '../../lib/format'

// Der Server liefert Zeitpunkte als UTC OHNE Zeitzonen-Kennung ("2025-01-01T10:00:00").
// Ohne parseServerDate würde der Browser das als Ortszeit lesen (in Deutschland 1-2 h zu früh).
describe('parseServerDate', () => {
  it('liest ISO ohne Zeitzone als UTC — identisch zur Schreibweise mit Z', () => {
    const ohneZone = parseServerDate('2025-01-01T10:00:00')
    const mitZ = parseServerDate('2025-01-01T10:00:00Z')

    expect(ohneZone?.toISOString()).toBe('2025-01-01T10:00:00.000Z')
    expect(ohneZone?.getTime()).toBe(mitZ?.getTime())
  })

  it('lässt explizite Zeitzonen unangetastet', () => {
    expect(parseServerDate('2025-01-01T12:00:00+02:00')?.toISOString()).toBe('2025-01-01T10:00:00.000Z')
    expect(parseServerDate('2025-01-01T05:00:00-05:00')?.toISOString()).toBe('2025-01-01T10:00:00.000Z')
  })

  it('versteht die .NET-Schreibweise mit 7 Nachkommastellen und Leerzeichen statt T', () => {
    expect(parseServerDate('2026-05-25T18:27:56.6880910')?.toISOString()).toBe('2026-05-25T18:27:56.688Z')
    expect(parseServerDate('2026-05-25 18:27:56.688091')?.toISOString()).toBe('2026-05-25T18:27:56.688Z')
  })

  it('liefert für leere oder ungültige Werte null', () => {
    expect(parseServerDate(null)).toBeNull()
    expect(parseServerDate(undefined)).toBeNull()
    expect(parseServerDate('')).toBeNull()
    expect(parseServerDate('kein Datum')).toBeNull()
  })

  it('deutet ein reines Datum als lokalen Kalendertag (kein Tageswechsel durch die Zeitzone)', () => {
    const d = parseServerDate('2025-03-09')
    expect([d?.getFullYear(), d?.getMonth(), d?.getDate()]).toEqual([2025, 2, 9])
  })
})

describe('formatDateTime / formatDate', () => {
  it('ergibt für "…T10:00:00" und "…T10:00:00Z" dieselbe Ortszeit', () => {
    expect(formatDateTime('2025-01-01T10:00:00')).toBe(formatDateTime('2025-01-01T10:00:00Z'))
    expect(formatDate('2025-01-01T10:00:00')).toBe(formatDate('2025-01-01T10:00:00Z'))
  })

  it('zeigt UTC-Zeitpunkte in der Ortszeit als de-DE (Winter +1 h, Sommer +2 h in Berlin)', () => {
    expect(formatDateTime('2025-01-01T10:00:00', { timeZone: 'Europe/Berlin' })).toBe('01.01.2025, 11:00')
    expect(formatDateTime('2025-07-01T10:00:00Z', { timeZone: 'Europe/Berlin' })).toBe('01.07.2025, 12:00')
  })

  it('formatDate nimmt den Tag des Zeitpunkts in der Ortszeit (Mitternacht wandert über die Tagesgrenze)', () => {
    expect(formatDate('2025-01-01T23:30:00', { timeZone: 'Europe/Berlin' })).toBe('02.01.2025')
    expect(formatDate('2025-01-01T23:30:00', { timeZone: 'UTC' })).toBe('01.01.2025')
  })

  it('zeigt für fehlende Werte den Platzhalter bzw. den gewünschten Text', () => {
    expect(formatDateTime(null)).toBe('—')
    expect(formatDateTime('unsinn')).toBe('—')
    expect(formatDateTime(undefined, { fallback: 'nie' })).toBe('nie')
  })
})

describe('formatDateOnly', () => {
  it('zeigt Kalendertage ohne jede Zeitzonen-Umrechnung', () => {
    expect(formatDateOnly('2026-05-25')).toBe('25.05.2026')
    // Mitternacht ohne Zone (so liefert der Server Datumsfelder) darf nicht auf den Vor-/Folgetag rutschen.
    expect(formatDateOnly('2026-05-25T00:00:00')).toBe('25.05.2026')
    expect(formatDateOnly('2026-05-25T23:59:59')).toBe('25.05.2026')
  })

  it('liefert für leere oder unlesbare Werte den Platzhalter', () => {
    expect(formatDateOnly(null)).toBe('—')
    expect(formatDateOnly('')).toBe('—')
    expect(formatDateOnly('morgen')).toBe('—')
  })
})

describe('formatMoney', () => {
  // Intl setzt zwischen Betrag und Währungszeichen ein geschütztes Leerzeichen.
  const normal = (s: string) => s.replace(/\s/g, ' ')

  it('formatiert Cent als Betrag mit Komma und Währung des Datensatzes', () => {
    expect(normal(formatMoney(1234))).toBe('12,34 €')
    expect(normal(formatMoney(123456, 'EUR'))).toBe('1.234,56 €')
    expect(normal(formatMoney(5000, 'USD'))).toContain('50,00')
  })

  it('wirft bei einer unbekannten Währungskennung nicht, sondern zeigt die Kennung', () => {
    expect(normal(formatMoney(1234, 'E'))).toBe('12,34 E')
  })
})

// Eingaben aus <input type="datetime-local"> sind Ortszeit ohne Zone. Der Server speichert Zeitpunkte als UTC und die
// Anzeige liest Werte ohne Zone als UTC - ein unverändert gesendeter Ortszeit-String würde beim Anzeigen verschoben.
describe('localDateTimeToUtcIso', () => {
  it('sendet die Ortszeit als UTC-Zeitpunkt (unabhängig von der Zeitzone des Rechners)', () => {
    expect(localDateTimeToUtcIso('2026-05-25T18:00')).toBe(new Date(2026, 4, 25, 18, 0).toISOString())
    expect(localDateTimeToUtcIso('2026-05-25T18:00')).toMatch(/Z$/)
  })

  it('Roundtrip: Eingabe -> Server (ohne Zone gespeichert) -> Anzeige zeigt wieder die eingegebene Uhrzeit', () => {
    const gesendet = localDateTimeToUtcIso('2026-05-25T18:00')!
    const vomServer = gesendet.replace(/\.\d+Z$/, '') // Der Server liefert Zeitpunkte ohne Zonen-Kennung zurück.
    expect(formatDateTime(vomServer)).toBe('25.05.2026, 18:00')
  })

  it('liefert für leere oder ungültige Eingaben null', () => {
    expect(localDateTimeToUtcIso('')).toBeNull()
    expect(localDateTimeToUtcIso(null)).toBeNull()
    expect(localDateTimeToUtcIso('kein Datum')).toBeNull()
  })
})
