import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { buildZpl, fetchZpl, zplFileName, zplPath } from '../../api/labelHooks'
import { fail, installMockApi, type MockApi } from '../helpers/mockApi'

describe('ZPL-Pfade und Dateinamen', () => {
  it('baut den Endpunkt mit Id und (ab zwei Kopien) ?copies=n', () => {
    expect(zplPath('bin', 'b1')).toBe('/labels/bin/b1.zpl')
    expect(zplPath('article', 'a-1', 1)).toBe('/labels/article/a-1.zpl')
    expect(zplPath('order', 'o1', 5)).toBe('/labels/order/o1.zpl?copies=5')
  })

  it('ein Etikett bekommt den Namen wie beim Server, mehrere einen Sammelnamen; Pfadzeichen im Code fallen weg', () => {
    expect(zplFileName('bin', [{ id: 'b1', code: 'A-01-01' }])).toBe('bin-A-01-01.zpl')
    expect(zplFileName('article', [{ id: 'a1', code: 'ART 1/2' }])).toBe('article-ART_1_2.zpl')
    expect(zplFileName('order', [{ id: 'o1', code: '../x' }])).toBe('order-.._x.zpl')
    expect(zplFileName('bin', [{ id: '1', code: 'A' }, { id: '2', code: 'B' }])).toBe('etiketten-lagerplaetze-2.zpl')
    expect(zplFileName('article', Array.from({ length: 5 }, (_, i) => ({ id: String(i), code: `S${i}` })))).toBe('etiketten-artikel-5.zpl')
    expect(zplFileName('order', [{ id: '1', code: 'A' }, { id: '2', code: 'B' }])).toBe('etiketten-bestellungen-2.zpl')
  })
})

describe('ZPL laden', () => {
  let api: MockApi
  const zpl = (code: string) => `^XA\n^FD${code}^FS\n^XZ\n`

  beforeEach(() => {
    api = installMockApi({
      'GET /labels/bin/b1.zpl': zpl('A-01-1'),
      'GET /labels/bin/b2.zpl': zpl('A-01-2'),
      'GET /labels/bin/b3.zpl': '^XA\n^FDA-01-3^FS\n^XZ',      // ohne abschließenden Zeilenumbruch
      'GET /labels/bin/bad.zpl': fail(404),
    })
  })
  afterEach(() => api.restore())

  it('lädt ein Etikett als Text und schickt die Kopienzahl als Query mit', async () => {
    expect(await fetchZpl('bin', 'b1')).toBe(zpl('A-01-1'))
    await fetchZpl('bin', 'b2', 4)

    expect(api.requests[0]).toMatchObject({ method: 'GET', path: '/labels/bin/b1.zpl', query: '' })
    expect(api.requests[1]).toMatchObject({ path: '/labels/bin/b2.zpl', query: 'copies=4' })
  })

  it('setzt die ZPL-Texte in der Reihenfolge der Auswahl zu einer Datei zusammen (jedes Etikett auf eigenen Zeilen)', async () => {
    const items = [{ id: 'b3', code: 'A-01-3' }, { id: 'b1', code: 'A-01-1' }, { id: 'b2', code: 'A-01-2' }]

    const combined = await buildZpl('bin', items, 2)

    expect(combined).toBe(`^XA\n^FDA-01-3^FS\n^XZ\n${zpl('A-01-1')}${zpl('A-01-2')}`)
    expect(api.calls('GET', '/labels/bin/b1.zpl')[0].query).toBe('copies=2')
    expect(api.requests).toHaveLength(3)
  })

  it('schlägt ein Etikett fehl, gibt es keine Datei (lieber gar nichts als eine unvollständige Auswahl)', async () => {
    await expect(buildZpl('bin', [{ id: 'b1', code: 'A' }, { id: 'bad', code: 'B' }], 1)).rejects.toMatchObject({ response: { status: 404 } })
  })

  it('lädt mehr Etiketten, als gleichzeitig laufen, und behält die Reihenfolge der Auswahl', async () => {
    const ids = Array.from({ length: 20 }, (_, i) => `n${i}`)
    for (const [i, id] of ids.entries()) api.setRoute(`GET /labels/bin/${id}.zpl`, zpl(`CODE-${i}`))

    const combined = await buildZpl('bin', ids.map((id) => ({ id, code: id })), 1)

    expect(combined).toBe(ids.map((_, i) => zpl(`CODE-${i}`)).join(''))
  })
})
