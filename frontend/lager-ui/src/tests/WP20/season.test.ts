import { describe, expect, it } from 'vitest'
import { dateInputValue, hasSeasonWindow, isInSeason, seasonValueFromInput, seasonWindowError, seasonWindowText } from '../../pages/articleEditor/season'

const utc = (iso: string) => new Date(iso)

// Wie tests/Lager.Tests/WP20/ArticleDomainTests.cs: das Ende ist ein Kalendertag und gilt noch vollständig.
describe('Saison-Fenster', () => {
  it('der letzte Gültigkeitstag gilt bis zum Ende dieses Tages', () => {
    const until = '2026-09-30T00:00:00'
    expect(isInSeason(null, until, utc('2026-09-29T12:00:00Z'))).toBe(true)
    expect(isInSeason(null, until, utc('2026-09-30T00:00:00Z'))).toBe(true)
    expect(isInSeason(null, until, utc('2026-09-30T23:59:59Z'))).toBe(true)
    expect(isInSeason(null, until, utc('2026-10-01T00:00:00Z'))).toBe(false)
    // auch mit Zone am Wert (der Server liefert Kind=Utc mit "Z")
    expect(isInSeason(null, '2026-09-30T00:00:00Z', utc('2026-09-30T20:00:00Z'))).toBe(true)
    expect(isInSeason(null, '2026-09-30T00:00:00Z', utc('2026-10-01T00:00:01Z'))).toBe(false)
  })

  it('der Beginn gilt ab dem ersten Tag um 00:00 UTC', () => {
    expect(isInSeason('2026-03-01T00:00:00', null, utc('2026-02-28T23:59:59Z'))).toBe(false)
    expect(isInSeason('2026-03-01T00:00:00', null, utc('2026-03-01T00:00:00Z'))).toBe(true)
    expect(isInSeason('2026-03-01', null, utc('2026-03-01T08:00:00Z'))).toBe(true)
  })

  it('ohne Grenzen ist der Artikel immer bestellbar', () => {
    expect(isInSeason(null, null, utc('1999-01-01T00:00:00Z'))).toBe(true)
    expect(isInSeason('', undefined)).toBe(true)
  })

  it('Ende vor Beginn ist ein Fehler, derselbe Tag ein Ein-Tages-Fenster', () => {
    expect(seasonWindowError('2026-05-05T00:00:00', '2026-05-04T00:00:00')).toContain('nicht vor dem Beginn')
    expect(seasonWindowError('2026-05-05T00:00:00', '2026-05-05T00:00:00')).toBeNull()
    expect(seasonWindowError('2026-05-05T14:00:00', '2026-05-05T00:00:00')).toBeNull() // Uhrzeit egal, es zählt der Tag
    expect(seasonWindowError(null, '2026-05-04T00:00:00')).toBeNull()
    expect(seasonWindowError('2026-05-05T00:00:00', null)).toBeNull()
  })

  it('wandelt zwischen Server-Wert und Datumsfeld um, ohne den Tag zu verschieben', () => {
    expect(dateInputValue('2026-09-30T00:00:00')).toBe('2026-09-30')
    expect(dateInputValue('2026-09-30T00:00:00Z')).toBe('2026-09-30')
    expect(dateInputValue('2026-09-30')).toBe('2026-09-30')
    expect(dateInputValue(null)).toBe('')
    expect(dateInputValue('kaputt')).toBe('')
    expect(seasonValueFromInput('2026-09-30')).toBe('2026-09-30T00:00:00')
    expect(seasonValueFromInput('')).toBeNull()
  })

  it('beschreibt das Fenster', () => {
    expect(seasonWindowText('2026-03-01T00:00:00', '2026-09-30T00:00:00')).toBe('01.03.2026 bis 30.09.2026')
    expect(seasonWindowText('2026-03-01T00:00:00', null)).toBe('ab 01.03.2026')
    expect(seasonWindowText(null, '2026-09-30T00:00:00')).toBe('bis 30.09.2026')
    expect(seasonWindowText(null, null)).toBe('ganzjährig')
    expect(hasSeasonWindow(null, null)).toBe(false)
    expect(hasSeasonWindow(null, '2026-09-30T00:00:00')).toBe(true)
  })
})
