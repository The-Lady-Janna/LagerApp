/// <summary>
/// Zentrale Query-Keys (TanStack Query). Alle Hooks in api/hooks.ts und alle
/// Invalidierungen greifen auf DIESE Definitionen zu — ein Key als Literal an
/// mehreren Stellen war die Ursache für eine wirkungslose Invalidierung (der
/// Retouren-QC invalidierte einen Key, den es nie gab).
///
/// TanStack matcht Keys als PRÄFIX in Richtung "Query-Key beginnt mit
/// Filter-Key": `invalidateQueries({ queryKey: ['stock'] })` trifft auch
/// ['stock', 'summary'] und ['stock', 'alerts'], aber ein Filter
/// ['returns', id] trifft die Listen-Query ['returns'] NICHT. Invalidiert wird
/// deshalb immer mit dem kurzen Basis-Key (z. B. queryKeys.returns).
/// </summary>
export const queryKeys = {
  // Stammdaten
  articles: ['articles'] as const,
  article: (id: string) => ['articles', id] as const,
  suppliers: ['suppliers'] as const,
  supplierList: (includeInactive: boolean) => ['suppliers', { includeInactive }] as const,
  customers: ['customers'] as const,
  customerList: (includeInactive: boolean) => ['customers', { includeInactive }] as const,
  users: ['users'] as const,
  cartConfigs: ['cart-configs'] as const,

  // Lagerlayout
  warehouseLayout: ['warehouse', 'layout'] as const,
  storageLocations: ['warehouse', 'locations'] as const,
  walls: ['warehouse', 'walls'] as const,
  pickPoints: ['warehouse', 'pick-points'] as const,

  // Bestand
  stock: ['stock'] as const,
  stockSummary: ['stock', 'summary'] as const,
  stockAlerts: ['stock', 'alerts'] as const,

  // Bestellungen, Picken, Packen
  orders: ['orders'] as const,
  order: (id: string) => ['orders', id] as const,
  pickLists: ['picklists'] as const,
  pickList: (id: string) => ['picklists', id] as const,
  packingPlan: (orderId: string) => ['packing', orderId] as const,
  waves: ['waves'] as const,

  // Beschaffung, Wareneingang, Inventur, Retouren, Versand
  purchaseOrders: ['purchase-orders'] as const,
  purchaseOrder: (id: string) => ['purchase-orders', id] as const,
  purchaseSuggestions: ['purchase-orders', 'suggestions'] as const,
  inbound: ['inbound'] as const,
  inboundShipment: (id: string) => ['inbound', id] as const,
  inventory: ['inventory'] as const,
  inventoryCount: (id: string) => ['inventory', id] as const,
  returns: ['returns'] as const,
  shipments: ['shipments'] as const,
  carriers: ['shipments', 'carriers'] as const,
  replenishment: ['replenishment'] as const,
  replenishmentTasks: (openOnly: boolean) => ['replenishment', { openOnly }] as const,
  slotting: ['slotting'] as const,
  slottingSuggestions: (rangeDays: number) => ['slotting', 'suggestions', rangeDays] as const,
  putawaySuggestions: (articleId: string | null, quantity: number) =>
    ['slotting', 'putaway', articleId, quantity] as const,

  // Reports (alle unter ['reports'], damit EIN Invalidate sie alle trifft)
  reports: ['reports'] as const,
  reportDashboard: (rangeDays: number) => ['reports', 'dashboard', rangeDays] as const,
  binHeatmap: (rangeDays: number) => ['reports', 'bin-heatmap', rangeDays] as const,
  deadStock: (days: number) => ['reports', 'dead-stock', days] as const,
  abcAnalysis: (rangeDays: number) => ['reports', 'abc-analysis', rangeDays] as const,
  liveStatus: ['reports', 'live-status'] as const,
  stockValuation: ['reports', 'stock-valuation'] as const,
  pickerPerformance: (rangeDays: number) => ['reports', 'picker-performance', rangeDays] as const,

  // Audit
  audit: ['audit'] as const,
  auditEntityTypes: ['audit', 'entity-types'] as const,
  auditList: (entity: string, id: string, take: number) => ['audit', 'list', entity, id, take] as const,
} as const

/** Ein Query-Key bzw. Key-Präfix (readonly, damit die `as const`-Tupel oben passen). */
export type QueryKeyPrefix = readonly unknown[]

/// <summary>
/// Welche Query-Gruppen nach welcher Mutation veralten. Die Hooks invalidieren
/// genau diese Listen; die Tests (src/tests/WP11) sichern sie ab.
/// </summary>
export const invalidateAfter = {
  /** Bestandsbewegung (Korrektur, Wareneingang, Inventur, Retoure): Bestand inkl. Summary/Alarme sowie Reports (Lagerwert, Dead-Stock, Dashboard). */
  stockChange: [queryKeys.stock, queryKeys.reports],
  /** Picklisten erzeugt/geändert: Liste der Picklisten und der Bestellstatus. */
  pickListChange: [queryKeys.pickLists, queryKeys.orders],
  /** Packen bucht den Bestand ab und schließt Picklisten/Bestellungen ab. */
  packed: [queryKeys.pickLists, queryKeys.orders, queryKeys.stock, queryKeys.reports],
} as const satisfies Record<string, readonly QueryKeyPrefix[]>
