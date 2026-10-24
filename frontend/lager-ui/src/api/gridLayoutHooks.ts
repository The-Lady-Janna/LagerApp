import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiClient } from './client'
import { queryKeys } from '../lib/queryKeys'

/// <summary>
/// Raster-Layout (einfacher Layout-Editor): ein Lager als Tabelle von Feldern. GET liefert das gespeicherte Raster oder die
/// Vorgaben (`exists: false`), PUT ersetzt alle Felder und erzeugt daraus Regale mit Behältern, Wände und Pickpunkte. Danach ist
/// die ganze Lagerstruktur veraltet (Baum, Lagerplätze, Wände, Pickpunkte, Bestand mit den Lagerplatz-Codes).
/// Fehler: 409 `grid_conflict` (jemand anderes hat zwischenzeitlich gespeichert), 409 `grid_cells_in_use` mit `cells`
/// (Felder, deren Behälter noch Bestand, Aufgaben oder Belege haben; siehe blockedCellsOf).
/// </summary>

export type GridCellType =
  | 'Walkway' | 'Wall' | 'Door' | 'Obstacle' | 'Shelf' | 'Start' | 'End'
  | 'Packing' | 'PackingStorage' | 'OutgoingStorage' | 'GoodsReceipt' | 'Returns'

export interface GridCellDto {
  col: number
  row: number
  ref: string
  type: Exclude<GridCellType, 'Walkway'>
  containers: number
  shelfId: string | null
  shelfCode: string | null
  occupiedContainers: number
}

export interface GridLayoutDto {
  warehouseId: string
  exists: boolean
  version: string | null
  cellSizeMm: number
  columns: number
  rows: number
  containerWidthMm: number
  containerDepthMm: number
  containerHeightMm: number
  containerMaxWeightGrams: number
  cells: GridCellDto[]
  managedShelfIds: string[]
  managedWallIds: string[]
  managedPickPointIds: string[]
}

export interface GridCellInput {
  col: number
  row: number
  type: GridCellType
  containers: number
}

export interface SaveGridLayoutRequest {
  cellSizeMm: number
  columns: number
  rows: number
  containerWidthMm: number
  containerDepthMm: number
  containerHeightMm: number
  containerMaxWeightGrams: number
  cells: GridCellInput[]
  expectedVersion: string | null
}

export interface GridApplySummary {
  shelvesCreated: number
  shelvesRemoved: number
  containersCreated: number
  containersRemoved: number
  walls: number
  pickPoints: number
}

export interface SaveGridLayoutResult {
  layout: GridLayoutDto
  summary: GridApplySummary
}

export interface GridBlockedCell {
  col: number
  row: number
  ref: string
  containerCodes: string[]
}

export const useGridLayout = (warehouseId: string | null | undefined) =>
  useQuery({
    queryKey: queryKeys.gridLayout(warehouseId ?? ''),
    queryFn: async () => (await apiClient.get<GridLayoutDto>(`/warehouse/${warehouseId}/grid`)).data,
    enabled: !!warehouseId,
  })

export const useSaveGridLayout = (warehouseId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: SaveGridLayoutRequest) =>
      (await apiClient.put<SaveGridLayoutResult>(`/warehouse/${warehouseId}/grid`, req)).data,
    onSuccess: (result) => {
      qc.setQueryData(queryKeys.gridLayout(warehouseId), result.layout)
      for (const queryKey of [queryKeys.warehouseLayout, queryKeys.storageLocations, queryKeys.walls, queryKeys.pickPoints, queryKeys.stock])
        void qc.invalidateQueries({ queryKey })
    },
  })
}

/** Die Felder aus einer 409-Antwort `grid_cells_in_use` (sonst leer). */
export function blockedCellsOf(error: unknown): GridBlockedCell[] {
  const data = (error as { response?: { data?: unknown } } | null)?.response?.data as { code?: unknown; cells?: unknown } | undefined
  if (!data || data.code !== 'grid_cells_in_use' || !Array.isArray(data.cells)) return []
  return data.cells.filter((c): c is GridBlockedCell =>
    typeof c === 'object' && c !== null && typeof (c as GridBlockedCell).col === 'number' && typeof (c as GridBlockedCell).row === 'number')
}

/** Ergebnis von "Layout löschen": was entfernt wurde (`grid` = ein Raster war gespeichert). */
export interface ClearLayoutResult {
  zones: number
  shelves: number
  storageLocations: number
  walls: number
  pickPoints: number
  grid: boolean
}

/** Löscht das ganze Layout eines Lagers (Struktur, Wände, Pickpunkte, Raster); das Lager bleibt. */
export const useClearLayout = (warehouseId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await apiClient.delete<ClearLayoutResult>(`/warehouse/${warehouseId}/layout`)).data,
    onSuccess: () => {
      for (const queryKey of [queryKeys.gridLayouts, queryKeys.warehouseLayout, queryKeys.storageLocations, queryKeys.walls, queryKeys.pickPoints, queryKeys.stock])
        void qc.invalidateQueries({ queryKey })
    },
  })
}
