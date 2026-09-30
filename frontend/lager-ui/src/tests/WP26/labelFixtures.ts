import type { StorageLocationDto, WarehouseDto } from '../../api/types'

const POSITION = { xMm: 0, yMm: 0, zMm: 0 }

function bin(id: string, shelfId: string, code: string): StorageLocationDto {
  return { id, shelfId, code, position: POSITION, widthMm: 600, depthMm: 600, heightMm: 500, maxWeightGrams: 50_000 }
}

/**
 * Ein Lager-Layout für die Etiketten-Tests: zwei Lager. WH1 hat Gang A mit den Regalen S1 (A-01-10, A-01-2, A-01-1) und
 * S2 (A-02-1) sowie Gang B mit Regal S3 (B-01-1); WH2 hat Gang C mit Regal S4 (C-01-1). Die Codes sind absichtlich nicht
 * in Sortierreihenfolge, "A-01-2" kommt natürlich vor "A-01-10".
 */
export function makeLayout(): WarehouseDto[] {
  const shelf = (id: string, aisleId: string, code: string, locations: StorageLocationDto[]) => ({
    id, aisleId, code, position: POSITION, widthMm: 8000, depthMm: 600, heightMm: 2000, locations,
  })
  const aisle = (id: string, zoneId: string, code: string, shelves: ReturnType<typeof shelf>[]) => ({
    id, zoneId, code, startPosition: POSITION, endPosition: POSITION, orientation: 'AlongX' as const, shelves,
  })
  return [
    {
      id: 'w1', code: 'WH1', name: 'Hauptlager',
      zones: [{
        id: 'z1', warehouseId: 'w1', code: 'Z1', name: 'Zone 1', origin: POSITION,
        aisles: [
          aisle('a1', 'z1', 'A', [
            shelf('s1', 'a1', 'S1', [bin('b10', 's1', 'A-01-10'), bin('b2', 's1', 'A-01-2'), bin('b1', 's1', 'A-01-1')]),
            shelf('s2', 'a1', 'S2', [bin('b21', 's2', 'A-02-1')]),
          ]),
          aisle('a2', 'z1', 'B', [shelf('s3', 'a2', 'S3', [bin('b31', 's3', 'B-01-1')])]),
        ],
      }],
    },
    {
      id: 'w2', code: 'WH2', name: 'Außenlager',
      zones: [{
        id: 'z2', warehouseId: 'w2', code: 'Z2', name: 'Zone 2', origin: POSITION,
        aisles: [aisle('a3', 'z2', 'C', [shelf('s4', 'a3', 'S4', [bin('b41', 's4', 'C-01-1')])])],
      }],
    },
  ]
}
