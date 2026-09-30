import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { isAxiosError } from 'axios'
import { apiClient } from './client'
import { parseChangePasswordResponse } from '../lib/passwordChange'
import { invalidateAfter, queryKeys, type QueryKeyPrefix } from '../lib/queryKeys'
import type {
  AdjustStockRequest,
  ArticleDto,
  CreateArticleRequest,
  CreateOrderRequest,
  CreatePickPointRequest,
  CreateStorageLocationRequest,
  CreateWallRequest,
  GeneratePickListRequest,
  OrderDto,
  PackingPlanDto,
  PickListDto,
  PickPointDto,
  PositionDto,
  ResetPickListsResult,
  ShelfDto,
  StockItemDto,
  StockSummaryDto,
  StorageLocationDto,
  UpdateArticleRequest,
  UpdatePickPointRequest,
  UpdateWallPointsRequest,
  WallDto,
  WarehouseDto,
} from './types'

// Die Query-Keys liegen zentral in lib/queryKeys.ts (hier re-exportiert, damit
// bestehende Imports aus '../api/hooks' weiter funktionieren).
export { queryKeys }

/** Invalidiert mehrere Query-Gruppen; das Promise wartet auf den Refetch der aktiven Queries. */
const invalidateMany = (qc: QueryClient, keys: readonly QueryKeyPrefix[]) =>
  Promise.all(keys.map((queryKey) => qc.invalidateQueries({ queryKey })))

export const useArticles = () =>
  useQuery({
    queryKey: queryKeys.articles,
    queryFn: async () => (await apiClient.get<ArticleDto[]>('/articles')).data,
  })

export const useArticle = (id: string | undefined) =>
  useQuery({
    queryKey: id ? queryKeys.article(id) : [...queryKeys.articles, 'none'],
    queryFn: async () => (await apiClient.get<ArticleDto>(`/articles/${id}`)).data,
    enabled: !!id,
  })

/**
 * Nach dem Anlegen/Ändern eines Artikels veralten neben den Artikelabfragen (Liste, Einzelartikel, Code-Auflösung; alle unter
 * ['articles']) auch Ansichten, die Artikeldaten mitführen: Bestand und Alarme (Name, Schwellen), Bestellvorschläge
 * (Lieferant, Preis) und Bestellungen (Bestandsampel über die Bundle-Komponenten).
 */
const articleDependents: readonly QueryKeyPrefix[] = [queryKeys.articles, queryKeys.stock, queryKeys.purchaseSuggestions, queryKeys.orders]

export const useCreateArticle = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateArticleRequest) =>
      (await apiClient.post<ArticleDto>('/articles', req)).data,
    onSuccess: () => invalidateMany(qc, articleDependents),
  })
}

export const useUpdateArticle = (id: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: UpdateArticleRequest) =>
      (await apiClient.put<ArticleDto>(`/articles/${id}`, req)).data,
    // Nicht auf den Refetch warten (wie bisher): der Editor geht sofort zur Liste, die beim Öffnen ohnehin frisch lädt.
    onSuccess: () => {
      void invalidateMany(qc, [...articleDependents, queryKeys.article(id)])
    },
  })
}

/** Löst einen gescannten/eingegebenen Code (GTIN, SKU, Alternativ-SKU) auf einen Artikel auf: 404 = unbekannt, 409 = mehrdeutig. */
export const fetchArticleByCode = async (code: string) =>
  (await apiClient.get<ArticleDto>(`/articles/by-code/${encodeURIComponent(code.trim())}`)).data

/**
 * Die Artikel, auf die ein Code gleichzeitig passt, wenn die Auflösung mehrdeutig war (HTTP 409, Code <c>ambiguous_code</c>);
 * sonst eine leere Liste. Für die Auswahl-Anzeige nach einem Scan.
 */
export const ambiguousArticleCandidates = (error: unknown): ArticleDto[] => {
  if (!isAxiosError(error) || error.response?.status !== 409) return []
  const candidates = (error.response.data as { candidates?: unknown } | undefined)?.candidates
  return Array.isArray(candidates) ? (candidates as ArticleDto[]) : []
}

/**
 * Artikel zu einem Code (GTIN/EAN, SKU oder Alternativ-SKU; Groß-/Kleinschreibung und Leerraum egal). Ohne Code ruht die Abfrage.
 * "Unbekannt" (404) und "mehrdeutig" (409) sind erwartete Ergebnisse des Scans, die der Aufrufer selbst anzeigt: kein globaler Toast.
 */
export const useArticleByCode = (code: string | null | undefined) => {
  const trimmed = code?.trim() ?? ''
  return useQuery({
    queryKey: [...queryKeys.articles, 'by-code', trimmed],
    queryFn: () => fetchArticleByCode(trimmed),
    enabled: trimmed !== '',
    meta: { silent: true },
  })
}

export const useWarehouseLayout = () =>
  useQuery({
    queryKey: queryKeys.warehouseLayout,
    queryFn: async () => (await apiClient.get<WarehouseDto[]>('/warehouse/layout')).data,
  })

export const useStorageLocations = () =>
  useQuery({
    queryKey: queryKeys.storageLocations,
    queryFn: async () =>
      (await apiClient.get<StorageLocationDto[]>('/warehouse/storage-locations')).data,
  })

export const useCreateStorageLocation = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateStorageLocationRequest) =>
      (await apiClient.post<StorageLocationDto>('/warehouse/storage-locations', req)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.storageLocations })
      qc.invalidateQueries({ queryKey: queryKeys.warehouseLayout })
    },
  })
}

export const useMoveStorageLocation = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, position }: { id: string; position: PositionDto }) =>
      (await apiClient.put<StorageLocationDto>(`/warehouse/storage-locations/${id}/position`, { position })).data,
    onMutate: async ({ id, position }) => {
      await qc.cancelQueries({ queryKey: queryKeys.storageLocations })
      const prev = qc.getQueryData<StorageLocationDto[]>(queryKeys.storageLocations)
      qc.setQueryData<StorageLocationDto[]>(queryKeys.storageLocations, (old) =>
        old?.map((b) => (b.id === id ? { ...b, position } : b))
      )
      return { prev }
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prev) qc.setQueryData(queryKeys.storageLocations, ctx.prev)
    },
    onSettled: () => {
      qc.invalidateQueries({ queryKey: queryKeys.storageLocations })
      qc.invalidateQueries({ queryKey: queryKeys.warehouseLayout })
    },
  })
}

export const useMoveShelf = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, position }: { id: string; position: PositionDto }) =>
      (await apiClient.put<ShelfDto>(`/warehouse/shelves/${id}/position`, { position })).data,
    onMutate: async ({ id, position }) => {
      await qc.cancelQueries({ queryKey: queryKeys.warehouseLayout })
      await qc.cancelQueries({ queryKey: queryKeys.storageLocations })
      const prevLayout = qc.getQueryData<WarehouseDto[]>(queryKeys.warehouseLayout)
      const prevLocs = qc.getQueryData<StorageLocationDto[]>(queryKeys.storageLocations)

      let dx = 0, dy = 0, dz = 0
      qc.setQueryData<WarehouseDto[]>(queryKeys.warehouseLayout, (old) =>
        old?.map((w) => ({
          ...w,
          zones: w.zones.map((z) => ({
            ...z,
            aisles: z.aisles.map((a) => ({
              ...a,
              shelves: a.shelves.map((s) => {
                if (s.id !== id) return s
                dx = position.xMm - s.position.xMm
                dy = position.yMm - s.position.yMm
                dz = position.zMm - s.position.zMm
                return { ...s, position }
              }),
            })),
          })),
        }))
      )

      qc.setQueryData<StorageLocationDto[]>(queryKeys.storageLocations, (old) =>
        old?.map((b) =>
          b.shelfId === id
            ? { ...b, position: { xMm: b.position.xMm + dx, yMm: b.position.yMm + dy, zMm: b.position.zMm + dz } }
            : b
        )
      )
      return { prevLayout, prevLocs }
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.prevLayout) qc.setQueryData(queryKeys.warehouseLayout, ctx.prevLayout)
      if (ctx?.prevLocs) qc.setQueryData(queryKeys.storageLocations, ctx.prevLocs)
    },
    onSettled: () => {
      qc.invalidateQueries({ queryKey: queryKeys.storageLocations })
      qc.invalidateQueries({ queryKey: queryKeys.warehouseLayout })
    },
  })
}

export interface CreateShelfRequest {
  aisleId: string
  code: string
  position: PositionDto
  widthMm: number
  depthMm: number
  heightMm: number
  initialBinCount: number
  binWidthMm: number
  binDepthMm: number
  binHeightMm: number
  binMaxWeightGrams: number
}

export const useCreateShelf = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateShelfRequest) =>
      (await apiClient.post<ShelfDto>('/warehouse/shelves', req)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.storageLocations })
      qc.invalidateQueries({ queryKey: queryKeys.warehouseLayout })
    },
  })
}

export interface AddBinToShelfRequest {
  code: string
  widthMm: number
  depthMm: number
  heightMm: number
  maxWeightGrams: number
}

export const useAddBinToShelf = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ shelfId, req }: { shelfId: string; req: AddBinToShelfRequest }) =>
      (await apiClient.post<StorageLocationDto>(`/warehouse/shelves/${shelfId}/bins`, req)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.storageLocations })
      qc.invalidateQueries({ queryKey: queryKeys.warehouseLayout })
    },
  })
}

export const useDeleteBin = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/warehouse/storage-locations/${id}`)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.storageLocations })
      qc.invalidateQueries({ queryKey: queryKeys.warehouseLayout })
    },
  })
}

export const useWalls = () =>
  useQuery({
    queryKey: queryKeys.walls,
    queryFn: async () => (await apiClient.get<WallDto[]>('/warehouse/walls')).data,
  })

export const useCreateWall = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateWallRequest) =>
      (await apiClient.post<WallDto>('/warehouse/walls', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.walls }),
  })
}

export const useUpdateWallPoints = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: UpdateWallPointsRequest }) =>
      (await apiClient.put<WallDto>(`/warehouse/walls/${id}/points`, req)).data,
    onMutate: async ({ id, req }) => {
      await qc.cancelQueries({ queryKey: queryKeys.walls })
      const prev = qc.getQueryData<WallDto[]>(queryKeys.walls)
      qc.setQueryData<WallDto[]>(queryKeys.walls, (old) =>
        old?.map((w) => (w.id === id ? { ...w, points: req.points } : w))
      )
      return { prev }
    },
    onError: (_e, _v, ctx) => { if (ctx?.prev) qc.setQueryData(queryKeys.walls, ctx.prev) },
    onSettled: () => qc.invalidateQueries({ queryKey: queryKeys.walls }),
  })
}

export const useDeleteWall = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/warehouse/walls/${id}`)
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.walls }),
  })
}

export const usePickPoints = () =>
  useQuery({
    queryKey: queryKeys.pickPoints,
    queryFn: async () => (await apiClient.get<PickPointDto[]>('/warehouse/pick-points')).data,
  })

export const useCreatePickPoint = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreatePickPointRequest) =>
      (await apiClient.post<PickPointDto>('/warehouse/pick-points', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.pickPoints }),
  })
}

export const useUpdatePickPoint = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: UpdatePickPointRequest }) =>
      (await apiClient.put<PickPointDto>(`/warehouse/pick-points/${id}`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.pickPoints }),
  })
}

export const useMovePickPoint = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, position }: { id: string; position: PositionDto }) =>
      (await apiClient.put<PickPointDto>(`/warehouse/pick-points/${id}/position`, { position })).data,
    onMutate: async ({ id, position }) => {
      await qc.cancelQueries({ queryKey: queryKeys.pickPoints })
      const prev = qc.getQueryData<PickPointDto[]>(queryKeys.pickPoints)
      qc.setQueryData<PickPointDto[]>(queryKeys.pickPoints, (old) =>
        old?.map((p) => (p.id === id ? { ...p, position } : p))
      )
      return { prev }
    },
    onError: (_e, _v, ctx) => { if (ctx?.prev) qc.setQueryData(queryKeys.pickPoints, ctx.prev) },
    onSettled: () => qc.invalidateQueries({ queryKey: queryKeys.pickPoints }),
  })
}

export const useDeletePickPoint = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/warehouse/pick-points/${id}`)
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.pickPoints }),
  })
}

export const useStock = () =>
  useQuery({
    queryKey: queryKeys.stock,
    queryFn: async () => (await apiClient.get<StockItemDto[]>('/stock')).data,
  })

export const useStockSummary = () =>
  useQuery({
    queryKey: queryKeys.stockSummary,
    queryFn: async () => (await apiClient.get<StockSummaryDto[]>('/stock/summary')).data,
  })

export const useAdjustStock = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: AdjustStockRequest) =>
      (await apiClient.post<StockItemDto>('/stock/adjust', req)).data,
    // Präfix ['stock'] trifft auch Summary und Alarme; Reports (Lagerwert, Dead-Stock) hängen am Bestand.
    onSuccess: () => invalidateMany(qc, invalidateAfter.stockChange),
  })
}

export const useOrders = () =>
  useQuery({
    queryKey: queryKeys.orders,
    queryFn: async () => (await apiClient.get<OrderDto[]>('/orders')).data,
  })

export const useCreateManualOrder = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateOrderRequest) =>
      (await apiClient.post<OrderDto>('/orders/manual', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.orders }),
  })
}

/**
 * Storniert eine Bestellung (Rolle Manager; erlaubt aus New, Picking und Picked). Aus einer Pickliste verschwinden die
 * Positionen der Bestellung, eine leere Liste wird storniert, aus einer offenen Welle fliegt sie heraus: neben den
 * Bestellungen veralten deshalb Picklisten und Wellen. Der Bestand ändert sich nicht (gebucht wird erst beim Verpacken).
 */
export const useCancelOrder = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await apiClient.post<OrderDto>(`/orders/${id}/cancel`)).data,
    onSuccess: () => invalidateMany(qc, [queryKeys.orders, queryKeys.pickLists, queryKeys.waves]),
  })
}

export const useGeneratePickList = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: GeneratePickListRequest) =>
      (await apiClient.post<PickListDto>('/picklists/generate', req)).data,
    onSuccess: (pl) => {
      qc.setQueryData(queryKeys.pickList(pl.id), pl)
      return invalidateMany(qc, invalidateAfter.pickListChange)
    },
  })
}

export interface RecalculatePickListRequest {
  startPickPointId?: string | null
  endPickPointId?: string | null
}

export const useRecalculatePickList = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: RecalculatePickListRequest }) =>
      (await apiClient.post<PickListDto>(`/picklists/${id}/recalculate`, req)).data,
    onSuccess: (pl) => {
      qc.setQueryData(queryKeys.pickList(pl.id), pl)
      qc.invalidateQueries({ queryKey: queryKeys.pickLists })
    },
  })
}

export const usePickList = (id: string | undefined) =>
  useQuery({
    queryKey: id ? queryKeys.pickList(id) : [...queryKeys.pickLists, 'none'],
    queryFn: async () => (await apiClient.get<PickListDto>(`/picklists/${id}`)).data,
    enabled: !!id,
  })

export const usePickLists = () =>
  useQuery({
    queryKey: queryKeys.pickLists,
    queryFn: async () => (await apiClient.get<PickListDto[]>('/picklists')).data,
  })

export const useResetPickLists = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await apiClient.delete<ResetPickListsResult>('/picklists')).data,
    onSuccess: () => invalidateMany(qc, invalidateAfter.pickListChange),
  })
}

export interface ReseedResponse {
  message: string
  counts: {
    articles: number
    shelves: number
    bins: number
    stockItems: number
    orders: number
    walls: number
    pickPoints: number
  }
}

// ---- Pickwagen-Konfiguration ------------------------------------------------

export const useCartConfigs = () =>
  useQuery({
    queryKey: queryKeys.cartConfigs,
    queryFn: async () => (await apiClient.get<import('./types').PickCartConfigDto[]>('/cart-configs')).data,
  })

export const useCreateCartConfig = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: import('./types').CreatePickCartConfigRequest) =>
      (await apiClient.post<import('./types').PickCartConfigDto>('/cart-configs', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.cartConfigs }),
  })
}

export const useUpdateCartConfig = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: import('./types').UpdatePickCartConfigRequest }) =>
      (await apiClient.put<import('./types').PickCartConfigDto>(`/cart-configs/${id}`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.cartConfigs }),
  })
}

export const useDeleteCartConfig = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => { await apiClient.delete(`/cart-configs/${id}`) },
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.cartConfigs }),
  })
}

export const useGenerateCartPickList = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: import('./types').GenerateCartPickListRequest) =>
      (await apiClient.post<PickListDto>('/picklists/generate-cart', req)).data,
    onSuccess: (pl) => {
      qc.setQueryData(queryKeys.pickList(pl.id), pl)
      return invalidateMany(qc, invalidateAfter.pickListChange)
    },
  })
}

export const usePackPickList = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: import('./types').PackPickListRequest }) =>
      (await apiClient.post<PickListDto>(`/picklists/${id}/pack`, req)).data,
    // Packen bucht den Bestand ab: neben Picklisten und Orders müssen Bestand und Reports neu geladen werden.
    onSuccess: (pl) => {
      qc.setQueryData(queryKeys.pickList(pl.id), pl)
      return invalidateMany(qc, invalidateAfter.packed)
    },
  })
}

export const useMarkPicked = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) =>
      (await apiClient.post<PickListDto>(`/picklists/${id}/mark-picked`)).data,
    onSuccess: (pl) => {
      qc.setQueryData(queryKeys.pickList(pl.id), pl)
      qc.invalidateQueries({ queryKey: queryKeys.pickLists })
    },
  })
}

export const useReseed = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await apiClient.post<ReseedResponse>('/admin/reseed')).data,
    onSuccess: () => {
      // wipe every cache — almost every page's data just changed
      qc.invalidateQueries()
    },
  })
}

export interface BulkSeedResponse {
  message: string
  articles: number
  stockItems: number
  orders: number
}

export const useBulkSeed = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (count: number) =>
      (await apiClient.post<BulkSeedResponse>(`/admin/seed-bulk?count=${count}`)).data,
    onSuccess: () => qc.invalidateQueries(),
  })
}

export const usePackingPlan = (orderId: string | undefined) =>
  useQuery({
    queryKey: orderId ? queryKeys.packingPlan(orderId) : ['packing', 'none'],
    queryFn: async () => (await apiClient.post<PackingPlanDto>(`/packing/${orderId}/plan`)).data,
    enabled: !!orderId,
  })

// ---- Reports ---------------------------------------------------------------

import type { ReportDashboardDto } from './types'

export const useReportDashboard = (rangeDays: number) =>
  useQuery({
    queryKey: queryKeys.reportDashboard(rangeDays),
    queryFn: async () =>
      (await apiClient.get<ReportDashboardDto>(`/reports/dashboard?range=${rangeDays}`)).data,
  })

export const useBinHeatmap = (rangeDays: number, enabled = true) =>
  useQuery({
    queryKey: queryKeys.binHeatmap(rangeDays),
    queryFn: async () =>
      (await apiClient.get<import('./types').BinHeatPointDto[]>(`/reports/bin-heatmap?range=${rangeDays}`)).data,
    enabled,
  })

export const useDeadStock = (days: number) =>
  useQuery({
    queryKey: queryKeys.deadStock(days),
    queryFn: async () =>
      (await apiClient.get<import('./types').DeadStockArticleDto[]>(`/reports/dead-stock?days=${days}`)).data,
  })

export const useAbcAnalysis = (rangeDays: number) =>
  useQuery({
    queryKey: queryKeys.abcAnalysis(rangeDays),
    queryFn: async () =>
      (await apiClient.get<import('./types').AbcArticleDto[]>(`/reports/abc-analysis?range=${rangeDays}`)).data,
  })

export const useLiveStatus = (pollMs = 10000) =>
  useQuery({
    queryKey: queryKeys.liveStatus,
    queryFn: async () =>
      (await apiClient.get<import('./types').LiveStatusDto>(`/reports/live-status`)).data,
    refetchInterval: pollMs,
  })

// ---- Welle 4 — Suppliers ---------------------------------------------------

import type {
  AdminResetPasswordRequest, ChangePasswordRequest,
  CreateInboundFromPurchaseOrderRequest, CreateInboundShipmentRequest, CreateReturnShipmentRequest,
  CreateSupplierRequest, CreateUserRequest, InboundShipmentDto, InventoryCountDto,
  PurchaseOrderDto, PurchaseSuggestionDto, ReturnShipmentDto, SetCountRequest,
  SetQcRequest, StartInventoryRequest, StockAlertDto, SupplierDto,
  UpdateSupplierRequest, UpdateUserRequest, UserDto,
} from './types'

export const useSuppliers = (includeInactive = false) =>
  useQuery({
    queryKey: queryKeys.supplierList(includeInactive),
    queryFn: async () => (await apiClient.get<SupplierDto[]>(`/suppliers?includeInactive=${includeInactive}`)).data,
  })

export const useCreateSupplier = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateSupplierRequest) => (await apiClient.post<SupplierDto>('/suppliers', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.suppliers }),
  })
}

export const useUpdateSupplier = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: UpdateSupplierRequest }) =>
      (await apiClient.put<SupplierDto>(`/suppliers/${id}`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.suppliers }),
  })
}

export const useToggleSupplier = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, active }: { id: string; active: boolean }) =>
      (await apiClient.post<SupplierDto>(`/suppliers/${id}/${active ? 'activate' : 'deactivate'}`)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.suppliers }),
  })
}

// ---- Welle 4 — Purchase Orders ---------------------------------------------

export const usePurchaseOrders = () =>
  useQuery({
    queryKey: queryKeys.purchaseOrders,
    queryFn: async () => (await apiClient.get<PurchaseOrderDto[]>('/purchase-orders')).data,
  })

export const usePurchaseOrder = (id: string | undefined) =>
  useQuery({
    queryKey: queryKeys.purchaseOrder(id ?? ''),
    queryFn: async () => (await apiClient.get<PurchaseOrderDto>(`/purchase-orders/${id}`)).data,
    enabled: !!id,
  })

export const usePurchaseSuggestions = () =>
  useQuery({
    queryKey: queryKeys.purchaseSuggestions,
    queryFn: async () => (await apiClient.get<PurchaseSuggestionDto[]>('/purchase-orders/suggestions')).data,
  })

// ---- Welle 4 — Returns -----------------------------------------------------

export const useReturns = () =>
  useQuery({
    queryKey: queryKeys.returns,
    queryFn: async () => (await apiClient.get<ReturnShipmentDto[]>('/returns')).data,
  })

export const useCreateReturn = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateReturnShipmentRequest) => (await apiClient.post<ReturnShipmentDto>('/returns', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.returns }),
  })
}

export const useSetReturnQc = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, lineId, req }: { id: string; lineId: string; req: SetQcRequest }) =>
      (await apiClient.put<ReturnShipmentDto>(`/returns/${id}/lines/${lineId}/qc`, req)).data,
    // Die Zeilen der Retouren-Seite kommen aus der LISTEN-Query ['returns']. Ein Key ['returns', id]
    // (den es nie gab) trifft sie nicht — TanStack matcht Präfixe nur vom Filter zum Query-Key.
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.returns }),
  })
}

export const useProcessReturn = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await apiClient.post<ReturnShipmentDto>(`/returns/${id}/process`)).data,
    // Verarbeiten bucht verkaufsfähige Ware zurück in den Bestand.
    onSuccess: () => invalidateMany(qc, [queryKeys.returns, ...invalidateAfter.stockChange]),
  })
}

// ---- Welle 4 — Users (Admin) -----------------------------------------------

export const useUsersList = () =>
  useQuery({
    queryKey: queryKeys.users,
    queryFn: async () => (await apiClient.get<UserDto[]>('/users')).data,
  })

export const useCreateUser = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateUserRequest) => (await apiClient.post<UserDto>('/users', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.users }),
  })
}

export const useUpdateUser = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: UpdateUserRequest }) =>
      (await apiClient.put<UserDto>(`/users/${id}`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.users }),
  })
}

export const useResetUserPassword = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: AdminResetPasswordRequest }) =>
      (await apiClient.post<UserDto>(`/users/${id}/reset-password`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.users }),
  })
}

export const useToggleUser = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, active }: { id: string; active: boolean }) =>
      (await apiClient.post<UserDto>(`/users/${id}/${active ? 'activate' : 'deactivate'}`)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.users }),
  })
}

// ---- Welle 4 — Eigenes Passwort ändern -------------------------------------

export const useChangeMyPassword = () =>
  useMutation({
    // Server-Vertrag: 204 ohne Body (alt) ODER 200 + { token, expiresAt, user }
    // (neu). Rückgabe: die neue Session oder null (→ neu anmelden).
    mutationFn: async (req: ChangePasswordRequest) => {
      const res = await apiClient.post('/auth/change-password', req)
      return parseChangePasswordResponse(res.status, res.data)
    },
  })

// ---- Welle 3a — Stock Alerts -----------------------------------------------

export const useStockAlerts = () =>
  useQuery({
    queryKey: queryKeys.stockAlerts,
    queryFn: async () => (await apiClient.get<StockAlertDto[]>('/stock/alerts')).data,
  })

// ---- Welle 3a — Inbound (Wareneingang) -------------------------------------

export const useInboundList = () =>
  useQuery({
    queryKey: queryKeys.inbound,
    queryFn: async () => (await apiClient.get<InboundShipmentDto[]>('/inbound')).data,
  })

export const useInbound = (id: string | undefined) =>
  useQuery({
    queryKey: queryKeys.inboundShipment(id ?? ''),
    queryFn: async () => (await apiClient.get<InboundShipmentDto>(`/inbound/${id}`)).data,
    enabled: !!id,
  })

export const useCreateInbound = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateInboundShipmentRequest) =>
      (await apiClient.post<InboundShipmentDto>('/inbound', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.inbound }),
  })
}

export const useReceiveInbound = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await apiClient.post<InboundShipmentDto>(`/inbound/${id}/receive`)).data,
    // Wareneingang bucht Bestand: Bestand + Reports neu laden (Präfix ['inbound'] trifft Liste und Detail). Gehört die Lieferung
    // zu einer Bestellung, schreibt das Buchen die empfangenen Mengen dort fort (Status, Bestellvorschläge): Bestellungen ebenfalls.
    onSuccess: () => invalidateMany(qc, [queryKeys.inbound, queryKeys.purchaseOrders, ...invalidateAfter.stockChange]),
  })
}

// ---- Welle 3a — Inventory (Inventur) ---------------------------------------

export const useInventoryList = () =>
  useQuery({
    queryKey: queryKeys.inventory,
    queryFn: async () => (await apiClient.get<InventoryCountDto[]>('/inventory')).data,
  })

export const useInventory = (id: string | undefined) =>
  useQuery({
    queryKey: queryKeys.inventoryCount(id ?? ''),
    queryFn: async () => (await apiClient.get<InventoryCountDto>(`/inventory/${id}`)).data,
    enabled: !!id,
  })

export const useStartInventory = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: StartInventoryRequest) =>
      (await apiClient.post<InventoryCountDto>('/inventory/start', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.inventory }),
  })
}

export const useSetInventoryCount = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, lineId, req }: { id: string; lineId: string; req: SetCountRequest }) =>
      (await apiClient.put<InventoryCountDto>(`/inventory/${id}/lines/${lineId}`, req)).data,
    onSuccess: (i) => qc.invalidateQueries({ queryKey: queryKeys.inventoryCount(i.id) }),
  })
}

export const useReconcileInventory = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await apiClient.post<InventoryCountDto>(`/inventory/${id}/reconcile`)).data,
    // Inventurabgleich korrigiert den Bestand: Bestand + Reports neu laden (Präfix ['inventory'] trifft Liste und Detail).
    onSuccess: () => invalidateMany(qc, [queryKeys.inventory, ...invalidateAfter.stockChange]),
  })
}

// ---- Welle 4 Rest — Bestandsbewertung --------------------------------------

import type { AddAddressRequest, CreateCustomerRequest, CustomerDto, StockValuationDto, UpdateCustomerRequest } from './types'

export const useStockValuation = () =>
  useQuery({
    queryKey: queryKeys.stockValuation,
    queryFn: async () => (await apiClient.get<StockValuationDto>('/reports/stock-valuation')).data,
  })

export const usePickerPerformance = (rangeDays: number) =>
  useQuery({
    queryKey: queryKeys.pickerPerformance(rangeDays),
    queryFn: async () =>
      (await apiClient.get<import('./types').PickerPerformanceDto>(`/reports/picker-performance?range=${rangeDays}`)).data,
  })

// ---- Welle 4 PO/Returns — additional action hooks --------------------------

export const useCreatePurchaseOrder = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: import('./types').CreatePurchaseOrderRequest) =>
      (await apiClient.post<PurchaseOrderDto>('/purchase-orders', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.purchaseOrders }),
  })
}

export const useSendPurchaseOrder = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await apiClient.post<PurchaseOrderDto>(`/purchase-orders/${id}/send`)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.purchaseOrders }),
  })
}

/**
 * Reine Statuskorrektur an einer Bestellzeile (POST …/lines/{lineId}/receive): bucht KEINEN Bestand. Den regulären Empfang legt
 * {@link useCreateInboundFromPurchaseOrder} an; die Bestellungen-Seite nutzt diesen Hook nicht.
 */
export const useReceivePoLine = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, lineId, qty }: { id: string; lineId: string; qty: number }) =>
      (await apiClient.post<PurchaseOrderDto>(`/purchase-orders/${id}/lines/${lineId}/receive`, { receivedQty: qty })).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.purchaseOrders }),
  })
}

/**
 * Legt aus den offenen Mengen einer versendeten Bestellung einen Wareneingang (Entwurf) an (POST …/create-inbound, Rolle Receiver).
 * Das bucht noch nichts: Bestand und Bestellung ändern sich erst, wenn die Lieferung im Wareneingang gebucht wird.
 */
export const useCreateInboundFromPurchaseOrder = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: CreateInboundFromPurchaseOrderRequest }) =>
      (await apiClient.post<InboundShipmentDto>(`/purchase-orders/${id}/create-inbound`, req)).data,
    // Der Entwurf erscheint in der Wareneingangs-Liste (Präfix ['inbound']).
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.inbound }),
  })
}

// ---- Welle 5 — Customers ---------------------------------------------------

export const useCustomers = (includeInactive = false) =>
  useQuery({
    queryKey: queryKeys.customerList(includeInactive),
    queryFn: async () => (await apiClient.get<CustomerDto[]>(`/customers?includeInactive=${includeInactive}`)).data,
  })

export const useCreateCustomer = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateCustomerRequest) => (await apiClient.post<CustomerDto>('/customers', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.customers }),
  })
}

export const useUpdateCustomer = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: UpdateCustomerRequest }) =>
      (await apiClient.put<CustomerDto>(`/customers/${id}`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.customers }),
  })
}

export const useAddCustomerAddress = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: AddAddressRequest }) =>
      (await apiClient.post<CustomerDto>(`/customers/${id}/addresses`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.customers }),
  })
}

// ---- Welle 7 — Shipments / Carriers ----------------------------------------

import type { AssignTrackingRequest, CarrierDto, CreateShipmentRequest, ShipmentDto } from './types'

export const useCarriers = () =>
  useQuery({
    queryKey: queryKeys.carriers,
    queryFn: async () => (await apiClient.get<CarrierDto[]>('/shipments/carriers')).data,
  })

export const useShipments = () =>
  useQuery({
    queryKey: queryKeys.shipments,
    queryFn: async () => (await apiClient.get<ShipmentDto[]>('/shipments')).data,
  })

export const useCreateShipment = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreateShipmentRequest) => (await apiClient.post<ShipmentDto>('/shipments', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.shipments }),
  })
}

export const useAssignTracking = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: AssignTrackingRequest }) =>
      (await apiClient.post<ShipmentDto>(`/shipments/${id}/tracking`, req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.shipments }),
  })
}

export const useMarkShipped = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await apiClient.post<ShipmentDto>(`/shipments/${id}/ship`)).data,
    // Sind alle Sendungen raus, geht die Bestellung auf Shipped (und der Live-Status der Reports ändert sich).
    onSuccess: () => invalidateMany(qc, [queryKeys.shipments, queryKeys.orders, queryKeys.reports]),
  })
}

export const useMarkDelivered = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await apiClient.post<ShipmentDto>(`/shipments/${id}/delivered`)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.shipments }),
  })
}

// ---- Welle 6 — Waves / Replenishment / Slotting / Putaway -----------------

import type {
  CreatePickWaveRequest, PickWaveDto, PutawaySuggestionDto, ReleaseWaveRequest,
  ReplenishmentTaskDto, SlottingSuggestionDto,
} from './types'

export const useWaves = () =>
  useQuery({
    queryKey: queryKeys.waves,
    queryFn: async () => (await apiClient.get<PickWaveDto[]>('/pick-waves')).data,
  })

export const useCreateWave = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (req: CreatePickWaveRequest) =>
      (await apiClient.post<PickWaveDto>('/pick-waves', req)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.waves }),
  })
}

export const useReleaseWave = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, req }: { id: string; req: ReleaseWaveRequest }) =>
      (await apiClient.post<PickWaveDto>(`/pick-waves/${id}/release`, req)).data,
    // Freigeben erzeugt eine Pickliste und setzt die Bestellungen auf Picking: Wellen, Picklisten UND Bestellungen neu
    // laden (sonst bliebe eine freigegebene Bestellung bis zu 30 s als Kandidat für die nächste Welle sichtbar).
    onSuccess: () => invalidateMany(qc, [queryKeys.waves, ...invalidateAfter.pickListChange]),
  })
}

export const useCancelWave = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => apiClient.post(`/pick-waves/${id}/cancel`),
    // Ein Abbruch storniert die noch unbegonnenen Picklisten der Welle und gibt ihre Bestellungen wieder frei.
    onSuccess: () => invalidateMany(qc, [queryKeys.waves, ...invalidateAfter.pickListChange]),
  })
}

// ---- Replenishment ---------------------------------------------------------

export const useReplenishmentTasks = (openOnly = false) =>
  useQuery({
    queryKey: queryKeys.replenishmentTasks(openOnly),
    queryFn: async () =>
      (await apiClient.get<ReplenishmentTaskDto[]>(`/replenishment${openOnly ? '/open' : ''}`)).data,
  })

export const useScanReplenishment = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (onlyForBinId: string | null) =>
      (await apiClient.post<ReplenishmentTaskDto[]>('/replenishment/scan', { onlyForBinId })).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: queryKeys.replenishment }),
  })
}

export const useCompleteReplenishment = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, actualQty }: { id: string; actualQty: number }) =>
      (await apiClient.post<ReplenishmentTaskDto>(`/replenishment/${id}/complete`, { actualQty })).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: queryKeys.replenishment })
      qc.invalidateQueries({ queryKey: queryKeys.stock })
    },
  })
}

// ---- Slotting + Putaway ----------------------------------------------------

export const useSlottingSuggestions = (rangeDays = 30) =>
  useQuery({
    queryKey: queryKeys.slottingSuggestions(rangeDays),
    queryFn: async () =>
      (await apiClient.get<SlottingSuggestionDto[]>(`/slotting/suggestions?rangeDays=${rangeDays}`)).data,
  })

export const usePutawaySuggestions = (articleId: string | null, quantity: number) =>
  useQuery({
    queryKey: queryKeys.putawaySuggestions(articleId, quantity),
    queryFn: async () =>
      (await apiClient.get<PutawaySuggestionDto[]>(`/slotting/putaway?articleId=${articleId}&quantity=${quantity}`)).data,
    enabled: !!articleId && quantity > 0,
  })
