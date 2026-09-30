import type { AisleDto, PositionDto, ShelfDto, WarehouseDto, ZoneDto } from '../../api/types'
import type { BinDto } from '../../api/warehouseAdminHooks'
import { installMockApi, type MockApi } from '../helpers/mockApi'

/// <summary>
/// Testdaten der Lagerstruktur (Form der API-DTOs) und eine zustandsbehaftete gemockte API: Anlegen/Ändern/Löschen verändern den
/// "Server"-Baum, das anschließende Neuladen des Layouts liefert ihn — so laufen die Seiten wie gegen einen echten Server.
/// </summary>

const ORIGIN: PositionDto = { xMm: 0, yMm: 0, zMm: 0 }

export function makeBin(id: string, code: string, overrides: Partial<BinDto> = {}): BinDto {
  return {
    id, shelfId: 's1', code, position: { xMm: 0, yMm: 0, zMm: 500 },
    widthMm: 600, depthMm: 600, heightMm: 500, maxWeightGrams: 50_000,
    binType: 'Standard', replenishmentThreshold: 0,
    ...overrides,
  }
}

export function makeShelf(id: string, code: string, locations: BinDto[] = [], overrides: Partial<ShelfDto> = {}): ShelfDto {
  return { id, aisleId: 'a1', code, position: ORIGIN, widthMm: 2_000, depthMm: 600, heightMm: 2_000, locations, ...overrides }
}

export function makeAisle(id: string, code: string, shelves: ShelfDto[] = [], overrides: Partial<AisleDto> = {}): AisleDto {
  return {
    id, zoneId: 'z1', code, startPosition: ORIGIN, endPosition: { xMm: 10_000, yMm: 0, zMm: 0 }, orientation: 'AlongX', shelves,
    ...overrides,
  }
}

export function makeZone(id: string, code: string, name: string, aisles: AisleDto[] = []): ZoneDto {
  return { id, warehouseId: 'w1', code, name, origin: ORIGIN, aisles }
}

export function makeWarehouse(id: string, code: string, name: string, zones: ZoneDto[] = []): WarehouseDto {
  return { id, code, name, zones }
}

/** WH01 > Z-A > A1 > S1 mit den Lagerplätzen S1-01 (Standard) und S1-02 (Hot-Pick, Schwelle 10). */
export function makeTree(): WarehouseDto[] {
  return [
    makeWarehouse('w1', 'WH01', 'Hauptlager', [
      makeZone('z1', 'Z-A', 'Zone A', [
        makeAisle('a1', 'A1', [
          makeShelf('s1', 'S1', [
            makeBin('b1', 'S1-01'),
            makeBin('b2', 'S1-02', { binType: 'HotPick', replenishmentThreshold: 10 }),
          ]),
        ]),
      ]),
    ]),
  ]
}

export interface LayoutApi {
  api: MockApi
  /** Der "Server"-Baum: Tests lesen und ändern ihn. */
  state: { layout: WarehouseDto[] }
}

/** Gemockte API mit GET /warehouse/layout aus `state.layout`; weitere Routen (POST/PUT/DELETE) kommen von den Tests. */
export function installLayoutApi(initial: WarehouseDto[], routes: Parameters<typeof installMockApi>[0] = {}): LayoutApi {
  const state = { layout: structuredClone(initial) }
  const api = installMockApi({
    'GET /warehouse/layout': () => state.layout,
    'GET /warehouse/storage-locations': () => state.layout.flatMap((w) => w.zones).flatMap((z) => z.aisles).flatMap((a) => a.shelves).flatMap((s) => s.locations),
    'GET /warehouse/walls': [],
    'GET /warehouse/pick-points': [],
    ...routes,
  })
  return { api, state }
}
