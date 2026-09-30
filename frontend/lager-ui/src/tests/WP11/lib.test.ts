import { describe, expect, it } from 'vitest'
import { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import { describeApiError } from '../../lib/apiError'
import { actualQuantity, findDeviations, nextScanCount, pickCountsFor, readPickedQuantities } from '../../lib/pickCounts'
import { invalidateAfter, queryKeys } from '../../lib/queryKeys'

function axiosError(status: number | undefined, data?: unknown): AxiosError {
  const config = { headers: {} } as InternalAxiosRequestConfig
  const response = status === undefined ? undefined : { data, status, statusText: '', headers: {}, config }
  return new AxiosError('Request failed', AxiosError.ERR_BAD_RESPONSE, config, {}, response)
}

describe('pickCounts', () => {
  const items = [
    { id: 'i1', quantity: 5 },
    { id: 'i2', quantity: 2 },
  ]

  it('eine nicht gezählte Position gilt als wie geplant bestätigt', () => {
    expect(actualQuantity({}, items[0])).toBe(5)
    expect(actualQuantity({ i1: 3 }, items[0])).toBe(3)
    expect(actualQuantity({ i1: 0 }, items[0])).toBe(0) // 0 ist eine echte Zählung (nichts gefunden), kein "leer"
  })

  it('der Scan zählt ab 0 statt bei der Soll-Menge weiter', () => {
    expect(nextScanCount(undefined)).toBe(1)
    expect(nextScanCount(0)).toBe(1)
    expect(nextScanCount(4)).toBe(5)
  })

  it('findDeviations liefert nur gezählte Positionen mit abweichender Menge', () => {
    const result = findDeviations(items, { i1: 3, i2: 2 })
    expect(result).toEqual([{ item: items[0], actual: 3 }])
    expect(findDeviations(items, {})).toEqual([])
  })

  it('pickCountsFor übernimmt nur Zählstände der Positionen dieser Liste', () => {
    expect(pickCountsFor(items, { i1: 3, fremd: 9 })).toEqual({ i1: 3 })
  })

  it('readPickedQuantities prüft den Router-State wie Fremdeingaben', () => {
    const state = { pickedQuantities: { i1: 3, i2: -1, i3: 4, i4: 1.5, i5: '2' } }
    expect(readPickedQuantities(state, [{ id: 'i1' }, { id: 'i2' }, { id: 'i4' }, { id: 'i5' }])).toEqual({ i1: 3 })
    expect(readPickedQuantities(null, items)).toEqual({})
    expect(readPickedQuantities({ pickedQuantities: 'x' }, items)).toEqual({})
    expect(readPickedQuantities(undefined, items)).toEqual({})
  })

  it('readPickedQuantities kappt eine Zählung über dem Soll auf das Soll (der Server lehnt alles außerhalb von 0..Soll ab)', () => {
    expect(readPickedQuantities({ pickedQuantities: { i1: 9, i2: 2 } }, items)).toEqual({ i1: 5, i2: 2 })
  })
})

describe('describeApiError', () => {
  it('nimmt die Meldung aus { error } des Servers', () => {
    expect(describeApiError(axiosError(400, { error: 'Bestand unzureichend' }))).toBe('Bestand unzureichend (HTTP 400)')
  })

  it('bevorzugt die lesbare Meldung vor einem Fehler-Code', () => {
    const error = axiosError(409, { error: 'concurrency_conflict', message: 'Der Datensatz wurde zwischenzeitlich geändert.' })
    expect(describeApiError(error)).toBe('Der Datensatz wurde zwischenzeitlich geändert. (HTTP 409)')
  })

  it('liest die erste Validierungsmeldung aus ProblemDetails', () => {
    const error = axiosError(400, { title: 'One or more validation errors occurred.', errors: { Name: ['Name ist erforderlich'] } })
    expect(describeApiError(error)).toContain('One or more validation errors occurred.')
    expect(describeApiError(axiosError(400, { errors: { Name: ['Name ist erforderlich'] } }))).toContain('Name ist erforderlich')
  })

  it('fällt ohne Body auf den HTTP-Status zurück', () => {
    expect(describeApiError(axiosError(undefined))).toBe('Keine Verbindung zum Server.')
    expect(describeApiError(axiosError(403))).toBe('Keine Berechtigung für diese Aktion.')
    expect(describeApiError(axiosError(500))).toBe('Serverfehler (HTTP 500).')
    expect(describeApiError(axiosError(418), )).toBe('Aktion fehlgeschlagen. (HTTP 418)')
  })

  it('behandelt Nicht-Axios-Fehler', () => {
    expect(describeApiError(new Error('kaputt'))).toBe('kaputt')
    expect(describeApiError('irgendwas', 'Speichern fehlgeschlagen.')).toBe('Speichern fehlgeschlagen.')
  })
})

describe('queryKeys', () => {
  it('Detail-Keys beginnen mit dem Basis-Key, damit ein Invalidate des Basis-Keys sie trifft', () => {
    expect(queryKeys.article('a1').slice(0, 1)).toEqual(queryKeys.articles)
    expect(queryKeys.pickList('pl1').slice(0, 1)).toEqual(queryKeys.pickLists)
    expect(queryKeys.stockSummary.slice(0, 1)).toEqual(queryKeys.stock)
    expect(queryKeys.stockAlerts.slice(0, 1)).toEqual(queryKeys.stock)
    expect(queryKeys.reportDashboard(30).slice(0, 1)).toEqual(queryKeys.reports)
    expect(queryKeys.liveStatus.slice(0, 1)).toEqual(queryKeys.reports)
    expect(queryKeys.inboundShipment('x').slice(0, 1)).toEqual(queryKeys.inbound)
    expect(queryKeys.inventoryCount('x').slice(0, 1)).toEqual(queryKeys.inventory)
  })

  it('Packen invalidiert Picklisten, Bestellungen, Bestand und Reports; Bestandsbewegungen Bestand und Reports', () => {
    expect(invalidateAfter.packed).toEqual([queryKeys.pickLists, queryKeys.orders, queryKeys.stock, queryKeys.reports])
    expect(invalidateAfter.stockChange).toEqual([queryKeys.stock, queryKeys.reports])
    expect(invalidateAfter.pickListChange).toEqual([queryKeys.pickLists, queryKeys.orders])
  })
})
