import { describe, expect, it } from 'vitest'
import { autoShipmentNumber, checkInboundLines, emptyLine, type InboundLineDraft } from '../../features/traceability/inboundLines'
import { compareExpiry, daysUntilExpiry, describeDays, expiryStatusOf, todayIso, traceabilityPath } from '../../features/traceability/expiry'

// MHD-Logik der Oberfläche: Kalendertage in UTC wie der Server (ReportService), Grenzen kritisch <= 7 und bald <= 30 Tage.
const NOW = new Date('2026-06-15T23:30:00Z') // spät am Abend UTC: die Tage zählen UTC-Kalendertage, nicht 24-h-Blöcke

describe('daysUntilExpiry und expiryStatusOf', () => {
  it('zählt UTC-Kalendertage: heute = 0, gestern = -1, in 30 Tagen = 30 - unabhängig von Uhrzeit und Format', () => {
    expect(daysUntilExpiry('2026-06-15', NOW)).toBe(0)
    expect(daysUntilExpiry('2026-06-15T00:00:00', NOW)).toBe(0)
    expect(daysUntilExpiry('2026-06-15T00:00:00Z', NOW)).toBe(0)
    expect(daysUntilExpiry('2026-06-14', NOW)).toBe(-1)
    expect(daysUntilExpiry('2026-07-15', NOW)).toBe(30)
    expect(daysUntilExpiry('2027-06-15', NOW)).toBe(365)
  })

  it('liest ein fehlendes oder kaputtes MHD als "kein MHD"', () => {
    expect(daysUntilExpiry(null, NOW)).toBeNull()
    expect(daysUntilExpiry(undefined, NOW)).toBeNull()
    expect(daysUntilExpiry('', NOW)).toBeNull()
    expect(daysUntilExpiry('morgen', NOW)).toBeNull()
  })

  it('Status an den Grenzen: gestern abgelaufen, heute und Tag 7 kritisch, Tag 8 und 30 bald, Tag 31 OK', () => {
    const status = (days: number) => expiryStatusOf(days)
    expect(status(-1)).toBe('Expired')
    expect(status(-400)).toBe('Expired')
    expect(status(0)).toBe('Critical')
    expect(status(7)).toBe('Critical')
    expect(status(8)).toBe('Soon')
    expect(status(30)).toBe('Soon')
    expect(status(31)).toBe('Ok')
  })

  it('beschreibt die Tage in Worten', () => {
    expect(describeDays(-5)).toBe('seit 5 Tagen abgelaufen')
    expect(describeDays(-1)).toBe('seit gestern abgelaufen')
    expect(describeDays(0)).toBe('läuft heute ab')
    expect(describeDays(1)).toBe('läuft morgen ab')
    expect(describeDays(12)).toBe('noch 12 Tage')
  })
})

describe('Sortierung und Adressen', () => {
  it('FEFO: frühestes MHD zuerst, ohne MHD zuletzt, gleiches MHD gleich', () => {
    const dates = ['2027-01-01T00:00:00', null, '2026-03-01', '2026-03-01T00:00:00Z', '2026-12-31']
    expect([...dates].sort(compareExpiry)).toEqual(['2026-03-01', '2026-03-01T00:00:00Z', '2026-12-31', '2027-01-01T00:00:00', null])
  })

  it('der Link zur Rückverfolgung kodiert die Chargennummer', () => {
    expect(traceabilityPath('LOT 2026#A+B')).toBe('/traceability?lot=LOT%202026%23A%2BB')
  })

  it('todayIso liefert den Ortstag als yyyy-MM-dd', () => {
    expect(todayIso(new Date(2026, 0, 5, 23, 59))).toBe('2026-01-05')
  })
})

describe('Wareneingangs-Zeilen prüfen', () => {
  const ctx = { today: '2026-06-15', tracksExpiry: (articleId: string) => articleId === 'tracked' }
  const line = (overrides: Partial<InboundLineDraft> = {}): InboundLineDraft => ({
    articleId: 'a1', targetBinId: 'b1', quantity: 5, lotNumber: '', expiryDate: '', ...overrides,
  })

  it('übernimmt Lot und MHD in den Request-Body und macht leere Felder zu null; Charge wird getrimmt', () => {
    const check = checkInboundLines([line({ lotNumber: ' LOT-A ', expiryDate: '2026-06-15' }), line({ articleId: 'a2', quantity: 2 })], ctx)

    expect(check.problems).toEqual([])
    expect(check.lines).toEqual([
      { articleId: 'a1', targetBinId: 'b1', quantity: 5, lotNumber: 'LOT-A', expiryDate: '2026-06-15' },
      { articleId: 'a2', targetBinId: 'b1', quantity: 2, lotNumber: null, expiryDate: null },
    ])
  })

  it('MHD heute ist erlaubt, gestern nicht (Neuware darf nicht abgelaufen sein)', () => {
    expect(checkInboundLines([line({ expiryDate: '2026-06-15' })], ctx).problems).toEqual([])
    expect(checkInboundLines([line({ expiryDate: '2026-06-14' })], ctx).problems)
      .toEqual(['Zeile 1: MHD liegt in der Vergangenheit — Neuware darf nicht abgelaufen sein.'])
  })

  it('lehnt ein unmögliches Datum und eine Menge außerhalb 1 bis 1.000.000 ab', () => {
    expect(checkInboundLines([line({ expiryDate: '2026-02-30' })], ctx).problems).toEqual(['Zeile 1: MHD ist kein gültiges Datum.'])
    for (const quantity of [0, -3, 1.5, 1_000_001]) {
      expect(checkInboundLines([line({ quantity })], ctx).problems).toHaveLength(1)
    }
    expect(checkInboundLines([line({ lotNumber: 'L'.repeat(65) })], ctx).problems).toEqual(['Zeile 1: Die Charge darf höchstens 64 Zeichen haben.'])
  })

  it('bei einem Artikel mit MHD-Pflege sind Charge und MHD Pflicht; bei anderen bleiben sie optional', () => {
    expect(checkInboundLines([line({ articleId: 'tracked' })], ctx).problems).toEqual([
      'Zeile 1: Der Artikel wird mit Charge und MHD geführt — Charge angeben.',
      'Zeile 1: Der Artikel wird mit Charge und MHD geführt — MHD angeben.',
    ])
    expect(checkInboundLines([line({ articleId: 'tracked', lotNumber: 'L1', expiryDate: '2027-01-01' })], ctx).problems).toEqual([])
    expect(checkInboundLines([line({ articleId: 'other' })], ctx).problems).toEqual([])
  })

  it('eine halb befüllte Zeile ist ein Fehler statt still zu verschwinden; eine unberührte Zeile wird übergangen', () => {
    const check = checkInboundLines([line(), line({ articleId: '', lotNumber: 'L-ohne-Artikel' }), emptyLine()], ctx)

    expect(check.problems).toEqual(['Zeile 2: Artikel wählen.'])
    expect(checkInboundLines([emptyLine()], ctx).problems).toEqual(['Mindestens eine Position mit Artikel, Ziel-Bin und Menge erfassen.'])
  })

  it('dieselbe Charge im selben Bin mit zwei MHD ist ein Fehler, mit gleichem MHD (oder anderem Bin) nicht', () => {
    const mismatch = checkInboundLines([line({ lotNumber: 'L', expiryDate: '2026-08-01' }), line({ lotNumber: 'L', expiryDate: '2026-09-01' })], ctx)
    expect(mismatch.problems).toEqual(['Zeile 2: Charge L steht in Zeile 1 mit anderem MHD — eine Charge hat je Lagerplatz genau ein MHD.'])

    expect(checkInboundLines([line({ lotNumber: 'L', expiryDate: '2026-08-01' }), line({ lotNumber: 'L', expiryDate: '2026-08-01' })], ctx).problems).toEqual([])
    expect(checkInboundLines([line({ lotNumber: 'L', expiryDate: '2026-08-01' }), line({ targetBinId: 'b2', lotNumber: 'L', expiryDate: '2026-09-01' })], ctx).problems).toEqual([])
  })

  it('die automatische Lieferschein-Nummer trägt Datum und Uhrzeit', () => {
    expect(autoShipmentNumber(new Date(2026, 5, 15, 9, 5, 7))).toBe('WE-20260615-090507')
  })
})
