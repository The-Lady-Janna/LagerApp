import type {
  ArticleDto,
  PickItemDto,
  PickListDto,
  PurchaseOrderDto,
  PurchaseSuggestionDto,
  ReturnShipmentDto,
  SupplierDto,
} from '../../api/types'

/// <summary>
/// Testdaten-Bausteine in der Form der API-DTOs. Typisiert gegen api/types.ts:
/// ändert sich ein DTO, schlägt hier die Typprüfung an.
/// </summary>

export function makeArticle(overrides: Partial<ArticleDto> = {}): ArticleDto {
  return {
    id: 'a1',
    sku: 'ART-001',
    name: 'Schraube M8',
    description: null,
    dimensions: { lengthMm: 10, widthMm: 10, heightMm: 40 },
    weightGrams: 12,
    stacking: { isStackable: false, stackingAxis: 'Z', stackingIncrementMm: 0, maxStackCount: null },
    minStock: 5,
    reorderPoint: 10,
    maxStock: 100,
    primarySupplierId: null,
    purchasePriceCents: 250,
    alternativeSkus: null,
    validFrom: null,
    validUntil: null,
    isBundle: false,
    bundleComponents: null,
    ...overrides,
  }
}

export function makeSupplier(overrides: Partial<SupplierDto> = {}): SupplierDto {
  return {
    id: 's1',
    code: 'SUP-A',
    name: 'Lieferant A',
    contactEmail: 'a@example.test',
    contactPhone: '111',
    notes: 'Notiz A',
    leadTimeDays: 3,
    minOrderValueCents: 5000,
    currency: 'EUR',
    isActive: true,
    createdAt: '2025-01-01T10:00:00',
    ...overrides,
  }
}

export function makePickItem(overrides: Partial<PickItemDto> = {}): PickItemDto {
  return {
    id: 'i1',
    sequenceNumber: 1,
    orderId: 'o1',
    orderNumber: 'ORD-1',
    articleId: 'a1',
    articleSku: 'ART-001',
    articleName: 'Schraube M8',
    storageLocationId: 'b1',
    storageLocationCode: 'A-01-01',
    quantity: 5,
    picked: false,
    confirmedQuantity: null,
    confirmedAt: null,
    ...overrides,
  }
}

export function makePickList(overrides: Partial<PickListDto> = {}): PickListDto {
  return {
    id: 'pl1',
    pickListNumber: 'PL-0001',
    status: 'Pending',
    assignedTo: null,
    totalDistanceMm: 12000,
    createdAt: '2025-01-01T10:00:00',
    items: [makePickItem()],
    waypoints: [],
    pickCartConfigId: null,
    pickCartConfigName: null,
    ...overrides,
  }
}

export function makeReturn(overrides: Partial<ReturnShipmentDto> = {}): ReturnShipmentDto {
  return {
    id: 'r1',
    rmaNumber: 'RMA-0001',
    orderId: null,
    customerReference: 'K-1',
    notes: null,
    status: 'Draft',
    createdAt: '2025-01-01T10:00:00',
    processedAt: null,
    lines: [
      {
        id: 'rl1',
        articleId: 'a1',
        articleSku: 'ART-001',
        quantity: 2,
        lotNumber: null,
        qcResult: 'Pending',
        targetBinId: null,
        qcNotes: null,
      },
    ],
    ...overrides,
  }
}

export function makeSuggestion(overrides: Partial<PurchaseSuggestionDto> = {}): PurchaseSuggestionDto {
  return {
    supplierId: 's1',
    supplierName: 'Lieferant A',
    leadTimeDays: 3,
    lines: [{ articleId: 'a1', sku: 'ART-001', name: 'Schraube M8', currentStock: 2, minStock: 5, reorderPoint: 10, maxStock: 100, suggestedOrderQty: 20 }],
    ...overrides,
  }
}

export function makePurchaseOrder(overrides: Partial<PurchaseOrderDto> = {}): PurchaseOrderDto {
  return {
    id: 'po1',
    poNumber: 'PO-0001',
    supplierId: 's1',
    supplierName: 'Lieferant A',
    status: 'Sent',
    currency: 'EUR',
    notes: null,
    createdAt: '2025-01-01T23:30:00',
    sentAt: null,
    expectedDate: '2026-05-25T00:00:00',
    receivedAt: null,
    totalValueCents: 123456,
    lines: [],
    ...overrides,
  }
}
