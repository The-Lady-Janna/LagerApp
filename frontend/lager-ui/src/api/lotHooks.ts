import { useQuery } from '@tanstack/react-query'
import { isAxiosError } from 'axios'
import { apiClient } from './client'

/// <summary>
/// Chargen/MHD (WP23): Typen und Hooks der MHD-Warnliste (GET /api/reports/expiring) und der Charge-Rückverfolgung
/// (GET /api/reports/charge/{lot}). Eigene Datei statt api/hooks.ts und api/types.ts (Feature-Konvention, siehe
/// src/features/README.md). Beide Abfragen liegen unter dem Präfix ['reports'] — jede Bestandsänderung, die die Reports
/// invalidiert (lib/queryKeys.ts, invalidateAfter.stockChange), lädt sie damit neu.
/// </summary>

/** Status einer Zeile der MHD-Warnliste (vom Server; die Bestandsansicht kennt zusätzlich "Ok"). */
export type ExpiringStatus = 'Expired' | 'Critical' | 'Soon'

/** Eine Bestandszeile (Charge) mit abgelaufenem oder bald ablaufendem MHD. */
export interface ExpiringStockDto {
  stockItemId: string
  articleId: string
  articleSku: string
  articleName: string
  binId: string
  binCode: string
  lotNumber: string | null
  quantity: number
  expiryDate: string
  /** Kalendertage in UTC bis zum Ablauf; negativ = abgelaufen, 0 = läuft heute ab. */
  daysUntilExpiry: number
  status: ExpiringStatus
}

export interface ChargeInboundDto {
  shipmentId: string
  shipmentNumber: string
  articleId: string
  articleSku: string
  targetBinId: string
  targetBinCode?: string | null
  quantity: number
  expiryDate: string | null
  receivedAt: string
  /** Status der Lieferung: nur "Received" ist gebucht. */
  status?: 'Draft' | 'Received' | 'Cancelled'
}

export interface ChargeStockDto {
  stockItemId: string
  articleId: string
  articleSku: string
  binId: string
  binCode: string
  quantity: number
  expiryDate: string | null
}

export interface ChargeMovementDto {
  at: string
  /** Vorzeichenbehaftet: negativ = Abgang. */
  quantityDelta: number
  /** StockMovementReason: Inbound, Pick, Return, Inventory, ReplenishmentOut, ReplenishmentIn, Adjust, BinMove, ReturnB, ReturnScrap. */
  reason: string
  binId: string
  binCode?: string | null
  articleId?: string | null
  articleSku?: string | null
  /** Art des auslösenden Vorgangs: InboundShipment, PickList, InventoryCount, ReturnShipment, ReplenishmentTask, ManualAdjust. */
  referenceType: string | null
  referenceId: string | null
}

/** Eine möglicherweise betroffene Bestellung (abgeleitet aus den Pick-Buchungen der Charge). */
export interface ChargeOrderDto {
  orderId: string
  orderNumber: string
  customerReference: string | null
  status: string
  pickListId: string
  pickListNumber: string | null
  pickedAt: string
}

export interface ChargeTraceDto {
  lotNumber: string
  inbounds: ChargeInboundDto[]
  currentStock: ChargeStockDto[]
  movements: ChargeMovementDto[]
  currentStockTotal: number
  inboundTotal: number
  /** Fehlt bei älteren Servern. */
  orders?: ChargeOrderDto[] | null
}

export const lotQueryKeys = {
  expiring: (days: number) => ['reports', 'expiring', days] as const,
  chargeTrace: (lot: string) => ['reports', 'charge', lot] as const,
}

/** Frist-Auswahl der MHD-Karte (Tage). */
export const EXPIRING_DAY_OPTIONS = [7, 30, 90] as const

/** MHD-Warnliste: abgelaufene und innerhalb von `days` Tagen ablaufende Chargen, nach MHD sortiert. */
export const useExpiringStock = (days: number) =>
  useQuery({
    queryKey: lotQueryKeys.expiring(days),
    queryFn: async () => (await apiClient.get<ExpiringStockDto[]>(`/reports/expiring?days=${days}`)).data,
  })

/**
 * Chargennummern mit "/" lassen sich über die Pfad-Route des Servers nicht abfragen (der Server löst den Schrägstrich als
 * Pfadtrenner auf) - die Oberfläche fragt sie gar nicht erst an und weist darauf hin.
 */
export function isTraceableLot(lot: string): boolean {
  return lot.trim() !== '' && !lot.includes('/')
}

/**
 * Rückverfolgung einer Charge. Eine unbekannte Charge (404) ist kein Fehler, sondern das Ergebnis null ("nichts gefunden");
 * andere Fehler bleiben Fehler der Query. Ohne (abfragbare) Chargennummer läuft keine Abfrage.
 */
export const useChargeTrace = (lot: string) => {
  const trimmed = lot.trim()
  return useQuery({
    queryKey: lotQueryKeys.chargeTrace(trimmed),
    queryFn: async (): Promise<ChargeTraceDto | null> => {
      try {
        return (await apiClient.get<ChargeTraceDto>(`/reports/charge/${encodeURIComponent(trimmed)}`)).data
      } catch (error) {
        if (isAxiosError(error) && error.response?.status === 404) return null
        throw error
      }
    },
    enabled: isTraceableLot(trimmed),
  })
}
