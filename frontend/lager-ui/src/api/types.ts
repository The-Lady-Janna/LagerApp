export interface DimensionsDto {
  lengthMm: number
  widthMm: number
  heightMm: number
}

export type StackingAxis = 'X' | 'Y' | 'Z'

export interface StackingInfoDto {
  isStackable: boolean
  stackingAxis: StackingAxis
  stackingIncrementMm: number
  maxStackCount: number | null
}

export interface ArticleDto {
  id: string
  sku: string
  name: string
  description: string | null
  dimensions: DimensionsDto
  weightGrams: number
  stacking: StackingInfoDto
  minStock: number
  reorderPoint: number
  maxStock: number
  primarySupplierId: string | null
  purchasePriceCents: number
  alternativeSkus: string[] | null
  validFrom: string | null
  validUntil: string | null
  isBundle: boolean
  bundleComponents: BundleComponentDto[] | null
  /**
   * GTIN/EAN (nur Ziffern, 8/12/13/14 Stellen, gültige Prüfziffer) oder null. Optional getypt, weil ältere Server und Testdaten
   * das Feld nicht senden; der Server liefert es immer.
   */
  gtin?: string | null
  /**
   * Vom Server berechnet: liegt heute im Saison-Fenster (das Ende gilt als Kalendertag inklusive)? false = nicht bestellbar.
   * Fehlt das Feld (ältere Server, Testdaten), rechnet die Oberfläche selbst (pages/articleEditor/season.ts).
   */
  isCurrentlyActive?: boolean
}

export interface BundleComponentDto {
  componentArticleId: string
  /** SKU der Komponente (der Server füllt sie; bei älteren Servern leer). */
  componentSku: string
  quantity: number
}

/** Bundle-Komponente im Request (Speichern): Artikel-Id und Menge (>= 1). */
export interface BundleComponentRequest {
  componentArticleId: string
  quantity: number
}

export interface CreateArticleRequest {
  sku: string
  name: string
  description: string | null
  dimensions: DimensionsDto
  weightGrams: number
  stacking: StackingInfoDto
  minStock?: number
  reorderPoint?: number
  maxStock?: number
  primarySupplierId?: string | null
  purchasePriceCents?: number
  /**
   * Alternativ-SKUs und Saison-Fenster. WICHTIG beim Update (PUT): der Server setzt sie bei
   * fehlendem Wert auf leer/null zurück — der Editor sendet deshalb immer die geladenen Werte mit.
   */
  alternativeSkus?: string[] | null
  validFrom?: string | null
  validUntil?: string | null
  /**
   * Bundle-Komponenten. Beim Update (PUT) bedeutet fehlend/null "unverändert", eine leere Liste "Bundle auflösen".
   * Der Editor sendet die Liste, sobald der Artikel Komponenten hat oder sie bearbeitet wurde.
   */
  bundleComponents?: BundleComponentRequest[] | null
  /**
   * GTIN/EAN. Beim Anlegen: leer/null = keine. Beim Update (PUT): fehlend/null = unverändert, "" = entfernen,
   * sonst die neue GTIN (der Server prüft Ziffern, Länge, Prüfziffer und Eindeutigkeit).
   */
  gtin?: string | null
}

export type UpdateArticleRequest = Omit<CreateArticleRequest, 'sku'>

export interface PositionDto {
  xMm: number
  yMm: number
  zMm: number
}

export interface StorageLocationDto {
  id: string
  shelfId: string
  code: string
  position: PositionDto
  widthMm: number
  depthMm: number
  heightMm: number
  maxWeightGrams: number
}

export interface ShelfDto {
  id: string
  aisleId: string
  code: string
  position: PositionDto
  widthMm: number
  depthMm: number
  heightMm: number
  locations: StorageLocationDto[]
}

export interface AisleDto {
  id: string
  zoneId: string
  code: string
  startPosition: PositionDto
  endPosition: PositionDto
  orientation: 'AlongX' | 'AlongY'
  shelves: ShelfDto[]
}

export interface ZoneDto {
  id: string
  warehouseId: string
  code: string
  name: string
  origin: PositionDto
  aisles: AisleDto[]
}

export interface WarehouseDto {
  id: string
  code: string
  name: string
  zones: ZoneDto[]
}

export interface WallDto {
  id: string
  warehouseId: string
  label: string | null
  points: PositionDto[]
  thicknessMm: number
}

export interface CreateWallRequest {
  warehouseId: string
  label: string | null
  points: PositionDto[]
  thicknessMm: number
}

export interface UpdateWallPointsRequest {
  points: PositionDto[]
}

export type PickPointType = 'Start' | 'End' | 'Both'

export interface PickPointDto {
  id: string
  warehouseId: string
  label: string
  type: PickPointType
  position: PositionDto
}

export interface CreatePickPointRequest {
  warehouseId: string
  label: string
  type: PickPointType
  position: PositionDto
}

export interface UpdatePickPointRequest {
  label: string
  type: PickPointType
}

export interface CreateStorageLocationRequest {
  shelfId: string
  code: string
  position: PositionDto
  widthMm: number
  depthMm: number
  heightMm: number
  maxWeightGrams: number
}

export interface StockItemDto {
  id: string
  articleId: string
  articleSku: string
  articleName: string
  storageLocationId: string
  storageLocationCode: string
  quantity: number
  lotNumber: string | null
  expiryDate: string | null
}

export interface StockSummaryDto {
  articleId: string
  articleSku: string
  articleName: string
  totalQuantity: number
  locationCount: number
}

export interface AdjustStockRequest {
  articleId: string
  storageLocationId: string
  delta: number
  lotNumber: string | null
  expiryDate: string | null
}

export interface OrderLineDto {
  id: string
  articleId: string
  articleSku: string
  quantity: number
}

/** Status einer Bestellung (Lebenszyklus: New -> Picking -> Picked -> Packed -> Shipped, Storno aus New/Picking/Picked). */
export type OrderStatus = 'New' | 'Picking' | 'Picked' | 'Packed' | 'Shipped' | 'Cancelled'

/** Alle Status in Lebenszyklus-Reihenfolge (Statusfilter, Sortierung). */
export const ORDER_STATUSES: readonly OrderStatus[] = ['New', 'Picking', 'Picked', 'Packed', 'Shipped', 'Cancelled']

/** Deutsche Standardtexte der Status. Die Oberfläche zeigt sie über status:order.<Status> (locales/<sprache>/status.json); hier dienen sie als Kennung der bekannten Status. */
export const ORDER_STATUS_LABEL: Record<OrderStatus, string> = {
  New: 'Neu',
  Picking: 'In Kommissionierung',
  Picked: 'Kommissioniert',
  Packed: 'Gepackt',
  Shipped: 'Versendet',
  Cancelled: 'Storniert',
}

/** Die Prioritäten 0..3; Anzeigetexte: orders:priority.<Wert> (Normal, Erhöht, Hoch, Dringend). */
export const ORDER_PRIORITIES: readonly number[] = [0, 1, 2, 3]

/** Solange der Auftrag noch Bestand braucht und storniert werden darf (New, Picking, Picked). */
export const CANCELLABLE_ORDER_STATUSES: readonly string[] = ['New', 'Picking', 'Picked']

export interface OrderDto {
  id: string
  orderNumber: string
  customerReference: string | null
  /** Ein Wert aus {@link OrderStatus}; als string typisiert, damit ein neuer Server-Status die Anzeige nicht bricht. */
  status: string
  source: string
  createdAt: string
  lines: OrderLineDto[]
  hasStockNow: boolean
  hasStockAfterFifo: boolean
  /** Kunde und Lieferadresse (optional verknüpft); Name und Adresse kommen aus dem Kundenstamm. */
  customerId?: string | null
  customerName?: string | null
  shippingAddressId?: string | null
  shippingAddress?: CustomerAddressDto | null
  /** 0 (normal) bis 3 (dringend). */
  priority?: number
  /** Gewünschter Termin (UTC-Zeitpunkt); früher Fällige werden zuerst kommissioniert. */
  dueDate?: string | null
  /** Kennung im Quellsystem (externe Bestell-API, Idempotenz). */
  externalReference?: string | null
}

export interface CreateOrderRequest {
  orderNumber: string
  customerReference: string | null
  /** Artikel wahlweise per articleId oder sku. */
  lines: { articleId?: string; sku?: string; quantity: number }[]
  customerId?: string | null
  /** Gehört zum Kunden (Liefer- oder Doppeladresse). */
  shippingAddressId?: string | null
  priority?: number
  dueDate?: string | null
  externalReference?: string | null
}

/** Antwort von DELETE /picklists: gelöscht werden nur Listen ohne Buchungswirkung, verpackte/stornierte bleiben (skippedCompleted). */
export interface ResetPickListsResult {
  deleted: number
  skippedCompleted: number
}

export interface GeneratePickListRequest {
  orderIds: string[]
  startPickPointId?: string | null
  endPickPointId?: string | null
}

export interface PickItemDto {
  id: string
  sequenceNumber: number
  orderId: string
  orderNumber: string
  articleId: string
  articleSku: string
  articleName: string
  storageLocationId: string
  storageLocationCode: string
  quantity: number
  picked: boolean
  confirmedQuantity: number | null
  confirmedAt: string | null
}

export interface PickListDto {
  id: string
  pickListNumber: string
  status: string
  assignedTo: string | null
  totalDistanceMm: number
  createdAt: string
  items: PickItemDto[]
  waypoints: PositionDto[]
  pickCartConfigId: string | null
  pickCartConfigName: string | null
}

export interface PickCartConfigDto {
  id: string
  name: string
  levelCount: number
  levelWidthMm: number
  levelDepthMm: number
  levelHeightMm: number
  maxWeightGrams: number
  totalVolumeMm3: number
}

export interface CreatePickCartConfigRequest {
  name: string
  levelCount: number
  levelWidthMm: number
  levelDepthMm: number
  levelHeightMm: number
  maxWeightGrams: number
}

export type UpdatePickCartConfigRequest = CreatePickCartConfigRequest

export interface GenerateCartPickListRequest {
  pickCartConfigId: string
  startPickPointId?: string | null
  endPickPointId?: string | null
  optimizeForBinReuse?: boolean
}

export interface ConfirmPackedItemRequest {
  pickItemId: string
  actualQuantity: number
}

export interface PackPickListRequest {
  items: ConfirmPackedItemRequest[]
}

export interface CartonDto {
  index: number
  cartonType: string
  innerLengthMm: number
  innerWidthMm: number
  innerHeightMm: number
  totalWeightGrams: number
  fillRatio: number
  allocations: CartonAllocationDto[]
}

export interface CartonAllocationDto {
  articleId: string
  articleSku: string
  quantity: number
}

export interface PackingPlanDto {
  orderId: string
  orderNumber: string
  cartons: CartonDto[]
  unpacked: CartonAllocationDto[]
  /** Besonderheiten in Klartext (z. B. Artikel ohne Abmessungen, die nicht verpackt werden konnten); leer/null = nichts aufgefallen. */
  warnings?: string[] | null
}

// ---- Reports / KPIs --------------------------------------------------------

export interface DashboardKpiDto {
  label: string
  value: string
  unit: string | null
  hint: string | null
}

export interface TopArticleDto {
  articleId: string
  sku: string
  name: string
  pickCount: number
  totalQuantity: number
}

export interface TopBinDto {
  binId: string
  binCode: string
  articleCount: number
  totalQuantity: number
}

export interface OrderStatusBucketDto {
  status: string
  count: number
}

export interface DailySeriesPointDto {
  date: string  // "yyyy-MM-dd"
  count: number
}

export interface ReportDashboardDto {
  rangeDays: number
  from: string
  to: string
  headline: DashboardKpiDto[]
  topArticles: TopArticleDto[]
  topBins: TopBinDto[]
  orderStatusBreakdown: OrderStatusBucketDto[]
  pickListsPerDay: DailySeriesPointDto[]
  ordersPerDay: DailySeriesPointDto[]
}

// Welle 8 — Analytics

export interface BinHeatPointDto {
  binId: string
  binCode: string
  pickCount: number
}

export interface DeadStockArticleDto {
  articleId: string
  sku: string
  name: string
  totalQuantity: number
  locationCount: number
  daysSinceLastMovement: number | null
}

export interface AbcArticleDto {
  articleId: string
  sku: string
  name: string
  pickCount: number
  totalQuantity: number
  sharePercent: number
  class: 'A' | 'B' | 'C'
}

export interface LiveStatusDto {
  ordersOpen: number
  ordersInPicking: number
  ordersPicked: number
  ordersPacked: number
  picklistsPending: number
  picklistsInProgress: number
  picklistsPickedReadyToPack: number
  picklistsCompletedToday: number
  inventoryCountsOpen: number
  replenishmentTasksOpen: number
  stockAlertsCritical: number
  stockAlertsWarning: number
  at: string
}

// Welle 4 — Suppliers / PurchaseOrders / Returns / Users / Auth

export interface SupplierDto {
  id: string
  code: string
  name: string
  contactEmail: string | null
  contactPhone: string | null
  notes: string | null
  leadTimeDays: number
  minOrderValueCents: number
  currency: string
  isActive: boolean
  createdAt: string
}

export interface CreateSupplierRequest {
  code: string
  name: string
  contactEmail?: string | null
  contactPhone?: string | null
  notes?: string | null
  leadTimeDays?: number
  minOrderValueCents?: number
  currency?: string
}

export interface UpdateSupplierRequest {
  name: string
  contactEmail: string | null
  contactPhone: string | null
  notes: string | null
  leadTimeDays: number
  minOrderValueCents: number
  currency: string
}

export interface PurchaseOrderLineDto {
  id: string
  articleId: string
  articleSku: string
  orderedQty: number
  receivedQty: number
  unitPriceCents: number
}

export interface PurchaseOrderDto {
  id: string
  poNumber: string
  supplierId: string
  supplierName: string | null
  status: 'Draft' | 'Sent' | 'PartiallyReceived' | 'Received' | 'Cancelled'
  currency: string
  notes: string | null
  createdAt: string
  sentAt: string | null
  expectedDate: string | null
  receivedAt: string | null
  totalValueCents: number
  lines: PurchaseOrderLineDto[]
}

/** Wareneingang (Entwurf) aus den offenen Mengen einer Bestellung: alle Zeilen gehen auf den Ziel-Lagerplatz; gebucht wird erst im Wareneingang. */
export interface CreateInboundFromPurchaseOrderRequest {
  targetBinId: string
  notes?: string | null
}

export interface CreatePurchaseOrderRequest {
  supplierId: string
  expectedDate?: string | null
  notes?: string | null
  lines: { articleId: string; orderedQty: number; unitPriceCents?: number }[]
}

export interface PurchaseSuggestionLineDto {
  articleId: string
  sku: string
  name: string
  currentStock: number
  minStock: number
  reorderPoint: number
  maxStock: number
  suggestedOrderQty: number
}

export interface PurchaseSuggestionDto {
  supplierId: string | null
  supplierName: string | null
  leadTimeDays: number
  lines: PurchaseSuggestionLineDto[]
}

export interface ReturnLineDto {
  id: string
  articleId: string
  articleSku: string
  quantity: number
  lotNumber: string | null
  qcResult: 'Pending' | 'Sellable' | 'BGrade' | 'Defect' | 'Destroy'
  targetBinId: string | null
  qcNotes: string | null
}

export interface ReturnShipmentDto {
  id: string
  rmaNumber: string
  orderId: string | null
  customerReference: string | null
  notes: string | null
  status: 'Draft' | 'Processed' | 'Cancelled'
  createdAt: string
  processedAt: string | null
  lines: ReturnLineDto[]
}

export interface CreateReturnShipmentRequest {
  orderId?: string | null
  customerReference?: string | null
  notes?: string | null
  lines: { articleId: string; quantity: number; lotNumber?: string | null }[]
}

export interface SetQcRequest {
  result: 'Sellable' | 'BGrade' | 'Defect' | 'Destroy'
  targetBinId?: string | null
  notes?: string | null
}

export interface UserDto {
  id: string
  username: string
  email: string | null
  displayName: string | null
  roles: string[]
  isActive: boolean
  mustChangePassword: boolean
  lastLoginAt: string | null
  createdAt: string
}

export interface CreateUserRequest {
  username: string
  password: string
  roles: string[]
  email?: string | null
  displayName?: string | null
  mustChangePassword?: boolean
}

export interface UpdateUserRequest {
  roles: string[]
  email?: string | null
  displayName?: string | null
}

export interface AdminResetPasswordRequest {
  newPassword: string
  mustChangeOnNextLogin?: boolean
}

export interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
}

/**
 * Passwort-Regeln des Servers (PasswordPolicy in Lager.Application/Auth): mindestens 10 Zeichen, höchstens 72 Bytes (UTF-8),
 * nicht gleich dem Benutzernamen (und beim Wechsel nicht gleich dem bisherigen Passwort). Die Oberfläche nennt sie in den Hinweisen;
 * über die Länge hinaus entscheidet der Server.
 */
export const PASSWORD_MIN_LENGTH = 10
export const PASSWORD_MAX_BYTES = 72

// Welle 3a-Backend Wareneingang + Inventur DTOs

export interface InboundLineDto {
  id: string
  articleId: string
  articleSku: string
  targetBinId: string
  targetBinCode: string
  quantity: number
  lotNumber: string | null
  expiryDate: string | null
}

export interface InboundShipmentDto {
  id: string
  shipmentNumber: string
  supplierReference: string | null
  notes: string | null
  status: 'Draft' | 'Received' | 'Cancelled'
  createdAt: string
  receivedAt: string | null
  lines: InboundLineDto[]
}

export interface CreateInboundShipmentRequest {
  shipmentNumber?: string
  supplierReference?: string | null
  notes?: string | null
  lines: { articleId: string; targetBinId: string; quantity: number; lotNumber?: string | null; expiryDate?: string | null }[]
}

export interface InventoryLineDto {
  id: string
  binId: string
  binCode: string
  articleId: string
  articleSku: string
  expectedQty: number
  countedQty: number | null
  diff: number
  reason: string | null
}

export interface InventoryCountDto {
  id: string
  name: string
  status: 'Open' | 'Reconciled' | 'Cancelled'
  createdAt: string
  reconciledAt: string | null
  lines: InventoryLineDto[]
}

export interface StartInventoryRequest {
  name: string
  onlyForBinId?: string | null
}

export interface SetCountRequest {
  countedQty: number
  reason?: string | null
}

export interface StockAlertDto {
  articleId: string
  articleSku: string
  articleName: string
  totalQuantity: number
  minStock: number
  reorderPoint: number
  maxStock: number
  severity: 'critical' | 'warning'
}

// Welle 4 Rest — Bestandsbewertung

export interface StockValuationLineDto {
  articleId: string
  sku: string
  name: string
  quantity: number
  unitPriceCents: number
  totalValueCents: number
  /** Menge ohne Ledger-Historie, die zum aktuellen Stammpreis bewertet wurde (Fallback); 0 = Wert vollständig aus den Kosten-Snapshots. */
  fallbackQuantity: number
}

export interface StockValuationDto {
  at: string
  articleCount: number
  totalQuantity: number
  totalValueCents: number
  currency: string
  lines: StockValuationLineDto[]
  /** Anteil von totalValueCents, der aus dem Stammpreis-Fallback stammt. */
  fallbackValueCents: number
  /** Kurzbeschreibung der Bewertungsmethode. */
  basis: string
}

// Picker-Performance

export interface PickerPerformanceRowDto {
  picker: string
  pickListsCompleted: number
  itemsPicked: number
  avgDistanceMeters: number
  avgDurationMinutes: number | null
}

export interface PickerPerformanceDto {
  from: string
  to: string
  rows: PickerPerformanceRowDto[]
}

// Welle 5 — Customers

export interface CustomerAddressDto {
  id: string
  kind: 'Shipping' | 'Billing' | 'Both'
  label: string
  street: string
  street2: string | null
  zip: string
  city: string
  country: string
}

export interface CustomerDto {
  id: string
  code: string
  name: string
  email: string | null
  phone: string | null
  notes: string | null
  currency: string
  defaultDiscountPercent: number
  isActive: boolean
  createdAt: string
  addresses: CustomerAddressDto[]
}

export interface CreateCustomerRequest { code: string; name: string; currency?: string }
export interface UpdateCustomerRequest {
  name: string
  email: string | null
  phone: string | null
  notes: string | null
  currency: string
  defaultDiscountPercent: number
}
export interface AddAddressRequest {
  kind: 'Shipping' | 'Billing' | 'Both'
  label: string
  street: string
  street2?: string | null
  zip: string
  city: string
  country?: string
}

// Welle 5 — Bundle / Alternativen / Saison sind im ArticleDto bereits drin
export interface BundleComponentDto {
  componentArticleId: string
  componentSku: string
  quantity: number
}

// Welle 7 — Shipments

/** Ein Carrier. Nur konfigurierte (isConfigured) lassen sich für neue Sendungen wählen; note erklärt bei den übrigen, warum nicht. */
export interface CarrierDto { code: string; displayName: string; isConfigured: boolean; note: string | null }

export interface ShipmentDto {
  id: string
  shipmentNumber: string
  orderId: string
  orderNumber: string | null
  pickListId: string | null
  carrierCode: string
  trackingNumber: string | null
  trackingUrl: string | null
  weightGrams: number
  lengthMm: number
  widthMm: number
  heightMm: number
  costCents: number
  notes: string | null
  status: 'Ready' | 'Labeled' | 'Shipped' | 'Delivered' | 'Cancelled'
  createdAt: string
  labeledAt: string | null
  shippedAt: string | null
  deliveredAt: string | null
  /** Empfänger = Lieferadresse der Bestellung (leer ohne Kunde/Adresse an der Bestellung). */
  recipientName?: string | null
  recipientStreet?: string | null
  recipientStreet2?: string | null
  recipientZip?: string | null
  recipientCity?: string | null
  recipientCountry?: string | null
}

export interface CreateShipmentRequest {
  orderId: string
  pickListId?: string | null
  carrierCode: string
  lengthMm: number
  widthMm: number
  heightMm: number
  weightGrams: number
  notes?: string | null
}

export interface AssignTrackingRequest {
  /** Pflicht bei einem Carrier ohne API (manuell); ein konfigurierter Carrier erzeugt das Label selbst. */
  trackingNumber?: string | null
  /** Nur absolute http(s)-Adresse. */
  trackingUrl?: string | null
  costCents?: number
}

// Welle 6 — Waves / Replenishment / Slotting / Putaway

export interface PickWaveDto {
  id: string
  waveNumber: string
  description: string | null
  status: 'Open' | 'Released' | 'Completed' | 'Cancelled'
  cutoffAt: string | null
  createdAt: string
  releasedAt: string | null
  completedAt: string | null
  orderIds: string[]
  pickListIds: string[]
}

export interface CreatePickWaveRequest {
  description?: string | null
  cutoffAt?: string | null
  orderIds: string[]
}

export interface ReleaseWaveRequest {
  startPickPointId?: string | null
  endPickPointId?: string | null
  splitByZone?: boolean
}

export interface ReplenishmentTaskDto {
  id: string
  articleId: string
  articleSku: string
  sourceBinId: string
  sourceBinCode: string
  targetBinId: string
  targetBinCode: string
  suggestedQty: number
  completedQty: number | null
  status: 'Open' | 'Completed' | 'Cancelled'
  createdAt: string
  completedAt: string | null
}

export interface PutawaySuggestionDto {
  binId: string
  binCode: string
  binType: 'Standard' | 'HotPick' | 'Reserve'
  currentQuantityOfArticle: number
  otherArticlesInBin: number
  capacityScore: number
  reason: string
}

export interface SlottingSuggestionDto {
  articleId: string
  articleSku: string
  articleName: string
  pickFrequency: number
  currentBinId: string
  currentBinCode: string
  currentDistanceMm: number
  suggestedBinId: string
  suggestedBinCode: string
  suggestedDistanceMm: number
  estimatedSavingsMm: number
}
