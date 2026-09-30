import { useMutation, useQuery } from '@tanstack/react-query'
import { apiClient } from './client'
import type { ArticleDto, OrderDto, WarehouseDto } from './types'
import { saveBlob } from '../lib/download'
import { queryKeys } from '../lib/queryKeys'

/// <summary>
/// API der Etiketten-Seite (src/features/labels): die Datenabfragen für die Auswahl und der ZPL-Download über die
/// vorhandenen Endpunkte GET /api/labels/{bin|article|order}/{id}.zpl (Rolle Picker). Die Abfragen nutzen dieselben
/// Query-Keys wie die Hooks in hooks.ts, teilen sich also den Cache mit Artikelliste, Bestellungen und Lager-Layout;
/// `enabled` verhindert, dass die Seite Daten lädt, die zur gewählten Etikettenart gar nicht gebraucht werden.
/// </summary>

/** Die drei Etikettenarten des Servers (Pfadteil der ZPL-Endpunkte). */
export type LabelKind = 'bin' | 'article' | 'order'

/** Obergrenze der Kopienzahl beim Server (ZPL ^PQ); sonst 400. */
export const ZPL_MAX_COPIES = 500

/** So viele ZPL-Dateien werden gleichzeitig geladen (Sammeldruck ruft je Etikett einen Endpunkt auf). */
const ZPL_CONCURRENCY = 6

export const useLabelLayout = (enabled: boolean) =>
  useQuery({
    queryKey: queryKeys.warehouseLayout,
    queryFn: async () => (await apiClient.get<WarehouseDto[]>('/warehouse/layout')).data,
    enabled,
  })

export const useLabelArticles = (enabled: boolean) =>
  useQuery({
    queryKey: queryKeys.articles,
    queryFn: async () => (await apiClient.get<ArticleDto[]>('/articles')).data,
    enabled,
  })

export const useLabelOrders = (enabled: boolean) =>
  useQuery({
    queryKey: queryKeys.orders,
    queryFn: async () => (await apiClient.get<OrderDto[]>('/orders')).data,
    enabled,
  })

/** Pfad des ZPL-Endpunkts (ohne /api), mit `?copies=n` ab zwei Kopien. */
export function zplPath(kind: LabelKind, id: string, copies = 1): string {
  return `/labels/${kind}/${encodeURIComponent(id)}.zpl${copies > 1 ? `?copies=${copies}` : ''}`
}

/** Das ZPL eines Etiketts (Text; jedes Etikett ist ein eigenes ^XA…^XZ). */
export async function fetchZpl(kind: LabelKind, id: string, copies = 1): Promise<string> {
  return (await apiClient.get<string>(zplPath(kind, id, copies), { responseType: 'text' })).data
}

/** Ein Etikett der Auswahl: Id für den Endpunkt und Code für den Dateinamen. */
export interface ZplItem {
  id: string
  code: string
}

/**
 * Das ZPL aller Etiketten in der Reihenfolge der Auswahl, hintereinander in EINER Datei (ein Drucker verarbeitet
 * nacheinander jedes ^XA…^XZ). Die Abrufe laufen mit wenigen gleichzeitigen Anfragen; schlägt einer fehl, schlägt
 * das Ganze fehl (lieber keine Datei als eine, der Etiketten fehlen).
 */
export async function buildZpl(kind: LabelKind, items: readonly ZplItem[], copies: number): Promise<string> {
  const parts: string[] = new Array(items.length)
  let next = 0
  const worker = async () => {
    while (next < items.length) {
      const index = next++
      parts[index] = await fetchZpl(kind, items[index].id, copies)
    }
  }
  await Promise.all(Array.from({ length: Math.min(ZPL_CONCURRENCY, items.length) }, worker))
  return parts.map((part) => (part.endsWith('\n') ? part : `${part}\n`)).join('')
}

const FILE_PREFIX: Record<LabelKind, string> = { bin: 'bin', article: 'article', order: 'order' }
const FILE_PLURAL: Record<LabelKind, string> = { bin: 'lagerplaetze', article: 'artikel', order: 'bestellungen' }

/** Dateiname des Downloads: ein Etikett wie beim Server ("bin-A-01-01.zpl"), mehrere als "etiketten-lagerplaetze-12.zpl". */
export function zplFileName(kind: LabelKind, items: readonly ZplItem[]): string {
  if (items.length === 1) {
    const safe = items[0].code.trim().replace(/[^\p{L}\p{N}._-]/gu, '_') || 'etikett'
    return `${FILE_PREFIX[kind]}-${safe}.zpl`
  }
  return `etiketten-${FILE_PLURAL[kind]}-${items.length}.zpl`
}

export interface DownloadZplRequest {
  kind: LabelKind
  items: ZplItem[]
  copies: number
}

/** Lädt das ZPL der Auswahl (mit Token) und bietet es als Datei zum Speichern an. */
export const useDownloadZpl = () =>
  useMutation({
    mutationFn: async ({ kind, items, copies }: DownloadZplRequest) => {
      const zpl = await buildZpl(kind, items, copies)
      const fileName = zplFileName(kind, items)
      saveBlob(new Blob([zpl], { type: 'text/plain;charset=utf-8' }), fileName)
      return { fileName, count: items.length }
    },
  })
