import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { apiClient } from './client'
import { queryKeys, type QueryKeyPrefix } from '../lib/queryKeys'
import type { AisleDto, PositionDto, ShelfDto, StorageLocationDto, WarehouseDto, ZoneDto } from './types'

/// <summary>
/// Hooks und Typen der Lager-Stammdaten-Pflege (Feature "Lagerstruktur", WP22): Lager, Zonen, Gänge, Regale und Lagerplätze
/// anlegen, ändern und löschen. Liegt bewusst neben api/hooks.ts (das bleibt unverändert); Regal anlegen, Lagerplatz
/// anlegen und löschen gibt es dort schon (useCreateShelf, useAddBinToShelf, useDeleteBin).
/// Fehler des Servers (409 duplicate_code, warehouse_not_empty, in_use) kommen mit verständlicher Meldung in `error`
/// (api/errors.ts, ErrorBanner).
/// </summary>

// ---- Typen ----------------------------------------------------------------------------------------------------------

export type BinType = 'Standard' | 'HotPick' | 'Reserve'

export const BIN_TYPES: readonly BinType[] = ['Standard', 'HotPick', 'Reserve']

// Die Anzeigenamen der Bin-Typen stehen in locales/<sprache>/status.json (status:binType.<Typ>).

/** Ein Lagerplatz, wie ihn die API liefert: `StorageLocationDto` (api/types.ts) kennt Bin-Typ und Nachschub-Schwelle noch nicht. */
export interface BinDto extends StorageLocationDto {
  binType?: string
  replenishmentThreshold?: number
}

/** Bin-Typ eines Lagerplatzes; unbekannt oder fehlend (älterer Server) = Standard. */
export function binTypeOf(bin: StorageLocationDto): BinType {
  const type = (bin as BinDto).binType
  return BIN_TYPES.find((t) => t === type) ?? 'Standard'
}

/** Nachschub-Schwelle eines Lagerplatzes (0, wenn nicht gesetzt). */
export function thresholdOf(bin: StorageLocationDto): number {
  return (bin as BinDto).replenishmentThreshold ?? 0
}

export type AisleOrientation = 'AlongX' | 'AlongY'

export interface CreateWarehouseRequest { code: string; name: string }
export type UpdateWarehouseRequest = CreateWarehouseRequest

export interface CreateZoneRequest { warehouseId: string; code: string; name: string; origin?: PositionDto | null }
export interface UpdateZoneRequest { code: string; name: string; origin?: PositionDto | null }

export interface CreateAisleRequest {
  zoneId: string
  code: string
  startPosition?: PositionDto | null
  endPosition?: PositionDto | null
  orientation?: AisleOrientation | null
}
export interface UpdateAisleRequest {
  code: string
  startPosition?: PositionDto | null
  endPosition?: PositionDto | null
  orientation?: AisleOrientation | null
}

export interface UpdateShelfRequest { code: string; widthMm: number; depthMm: number; heightMm: number }

export interface UpdateStorageLocationRequest {
  code: string
  widthMm: number
  depthMm: number
  heightMm: number
  maxWeightGrams: number
}

export interface SetBinTypeRequest { binType: BinType; replenishmentThreshold: number }

// ---- Invalidierung --------------------------------------------------------------------------------------------------

/**
 * Was nach einer Änderung an der Lagerstruktur veraltet: der Baum und die Lagerplatz-Liste, Wände und Pickpunkte (gehen mit
 * einem gelöschten Lager) und der Bestand (zeigt die Lagerplatz-Codes; Löschen entfernt leere Bestandszeilen).
 */
const structureKeys: readonly QueryKeyPrefix[] = [
  queryKeys.warehouseLayout,
  queryKeys.storageLocations,
  queryKeys.walls,
  queryKeys.pickPoints,
  queryKeys.stock,
]

const invalidateStructure = (qc: QueryClient) =>
  Promise.all(structureKeys.map((queryKey) => qc.invalidateQueries({ queryKey })))

/** Baut einen Mutations-Hook, der nach Erfolg die Lagerstruktur neu lädt. */
function useStructureMutation<TVars, TResult>(mutationFn: (vars: TVars) => Promise<TResult>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn,
    onSuccess: () => invalidateStructure(qc),
  })
}

// ---- Lager ----------------------------------------------------------------------------------------------------------

export const useCreateWarehouse = () =>
  useStructureMutation(async (req: CreateWarehouseRequest) => (await apiClient.post<WarehouseDto>('/warehouse', req)).data)

export const useUpdateWarehouse = () =>
  useStructureMutation(async ({ id, req }: { id: string; req: UpdateWarehouseRequest }) =>
    (await apiClient.put<WarehouseDto>(`/warehouse/${id}`, req)).data)

export const useDeleteWarehouse = () =>
  useStructureMutation(async (id: string) => { await apiClient.delete(`/warehouse/${id}`) })

// ---- Zone -----------------------------------------------------------------------------------------------------------

export const useCreateZone = () =>
  useStructureMutation(async (req: CreateZoneRequest) => (await apiClient.post<ZoneDto>('/warehouse/zones', req)).data)

export const useUpdateZone = () =>
  useStructureMutation(async ({ id, req }: { id: string; req: UpdateZoneRequest }) =>
    (await apiClient.put<ZoneDto>(`/warehouse/zones/${id}`, req)).data)

export const useDeleteZone = () =>
  useStructureMutation(async (id: string) => { await apiClient.delete(`/warehouse/zones/${id}`) })

// ---- Gang -----------------------------------------------------------------------------------------------------------

export const useCreateAisle = () =>
  useStructureMutation(async (req: CreateAisleRequest) => (await apiClient.post<AisleDto>('/warehouse/aisles', req)).data)

export const useUpdateAisle = () =>
  useStructureMutation(async ({ id, req }: { id: string; req: UpdateAisleRequest }) =>
    (await apiClient.put<AisleDto>(`/warehouse/aisles/${id}`, req)).data)

export const useDeleteAisle = () =>
  useStructureMutation(async (id: string) => { await apiClient.delete(`/warehouse/aisles/${id}`) })

// ---- Regal ----------------------------------------------------------------------------------------------------------

export const useUpdateShelf = () =>
  useStructureMutation(async ({ id, req }: { id: string; req: UpdateShelfRequest }) =>
    (await apiClient.put<ShelfDto>(`/warehouse/shelves/${id}`, req)).data)

export const useDeleteShelf = () =>
  useStructureMutation(async (id: string) => { await apiClient.delete(`/warehouse/shelves/${id}`) })

// ---- Lagerplatz -----------------------------------------------------------------------------------------------------

export const useUpdateBin = () =>
  useStructureMutation(async ({ id, req }: { id: string; req: UpdateStorageLocationRequest }) =>
    (await apiClient.put<StorageLocationDto>(`/warehouse/storage-locations/${id}`, req)).data)

/** Bin-Typ und Nachschub-Schwelle (Hot-Pick-Plätze brauchen eine Schwelle, sonst ignoriert sie der Nachschub-Scan). */
export const useSetBinType = () =>
  useStructureMutation(async ({ id, req }: { id: string; req: SetBinTypeRequest }) =>
    (await apiClient.put<StorageLocationDto>(`/warehouse/storage-locations/${id}/bin-type`, req)).data)
