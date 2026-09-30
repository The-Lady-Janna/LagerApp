import { useMutation, useQueryClient } from '@tanstack/react-query'
import { apiClient } from './client'
import { saveBlob } from '../lib/download'
import { queryKeys, type QueryKeyPrefix } from '../lib/queryKeys'

/// <summary>
/// API-Zugriffe der Seite "Import & Export" (CSV, Rolle Manager): eigene Hooks und Typen des Features (api/types.ts und api/hooks.ts
/// bleiben unberührt). Endpunkte: GET api/export/{articles|stock|orders|movements|audit}.csv und
/// POST api/import/{articles|stock|orders}?dryRun=&skipErrors=&delimiter= (Multipart, Feld "file"), siehe docs/features/csv-import-export.md.
/// </summary>

/** Was importiert werden kann (Teil der Route). */
export type ImportKind = 'articles' | 'stock' | 'orders'

/** Was exportiert werden kann (Dateiname ohne ".csv"). */
export type ExportKind = 'articles' | 'stock' | 'orders' | 'movements' | 'audit'

/** Trennzeichen: Semikolon (Standard, Excel-DE) mit Dezimalkomma oder Komma mit Dezimalpunkt. */
export type CsvDelimiter = 'semicolon' | 'comma'

/** Grenzen des Servers (ImportLimits): größere Dateien lehnt er mit einer klaren Meldung ab, das Frontend meldet es schon vor dem Upload. */
export const IMPORT_MAX_BYTES = 5 * 1024 * 1024
export const IMPORT_MAX_ROWS = 20_000

/** Ein Fehler einer Datenzeile: Zeile der Datei (1 = Kopfzeile), Schlüssel (SKU, Bestellnummer …), Code und Text. */
export interface ImportRowErrorDto {
  line: number
  key: string | null
  code: string
  message: string
}

/** Ergebnis eines Imports (auch des Trockenlaufs). `applied` = es wurde geschrieben. */
export interface ImportResultDto {
  kind: ImportKind
  dryRun: boolean
  applied: boolean
  delimiter: CsvDelimiter
  rows: number
  created: number
  updated: number
  unchanged: number
  errorCount: number
  /** Höchstens 500 Einzelfehler; `errorCount` zählt alle. */
  errors: ImportRowErrorDto[]
  errorsTruncated: boolean
  warnings: string[]
  /** "x neu, y aktualisiert, z Fehler". */
  summary: string
  message: string
  importId: string | null
}

export interface ImportRequest {
  kind: ImportKind
  file: File
  delimiter: CsvDelimiter
  /** true = nur prüfen (Trockenlauf), false = übernehmen. */
  dryRun: boolean
  /** Fehlerhafte Zeilen auslassen und die übrigen übernehmen. */
  skipErrors?: boolean
}

export interface ExportOptions {
  delimiter: CsvDelimiter
  /** Nur Bewegungen und Audit: Zeitraum als Datum (2026-09-01) - "bis" schließt den ganzen Tag ein. */
  from?: string
  to?: string
  /** Nur Audit: Benutzername. */
  user?: string
}

/** Die Adresse des Imports (relativ zu /api) mit allen Parametern im Query-String. */
export function importUrl(request: Pick<ImportRequest, 'kind' | 'delimiter' | 'dryRun' | 'skipErrors'>): string {
  const params = new URLSearchParams({ dryRun: String(request.dryRun), delimiter: request.delimiter })
  if (request.skipErrors) params.set('skipErrors', 'true')
  return `/import/${request.kind}?${params.toString()}`
}

/** Die Adresse des Exports (relativ zu /api); leere Filter entfallen. */
export function exportUrl(kind: ExportKind, options: ExportOptions): string {
  const params = new URLSearchParams({ delimiter: options.delimiter })
  if (kind === 'movements' || kind === 'audit') {
    if (options.from) params.set('from', options.from)
    if (options.to) params.set('to', options.to)
  }
  if (kind === 'audit' && options.user?.trim()) params.set('user', options.user.trim())
  return `/export/${kind}.csv?${params.toString()}`
}

/** Dateiname laut Antwort-Header Content-Disposition; ohne (oder bei unlesbarem Header) ein eigener mit UTC-Zeit wie beim Server. */
export function exportFileName(kind: ExportKind, contentDisposition?: string | null, now: Date = new Date()): string {
  const match = contentDisposition ? /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(contentDisposition) : null
  if (match?.[1]) {
    try {
      const name = decodeURIComponent(match[1].trim())
      // nur ein reiner Dateiname: nie ein Pfad aus dem Header übernehmen
      if (name !== '' && !/[\\/]/.test(name)) return name
    } catch {
      /* unlesbar: Ersatzname */
    }
  }
  const pad = (n: number) => String(n).padStart(2, '0')
  const stamp = `${now.getUTCFullYear()}${pad(now.getUTCMonth() + 1)}${pad(now.getUTCDate())}T${pad(now.getUTCHours())}${pad(now.getUTCMinutes())}${pad(now.getUTCSeconds())}Z`
  return `${kind}-${stamp}.csv`
}

/**
 * Lädt eine Exportdatei mit dem Login-Token (ein normaler Link schickt keinen Authorization-Header) und bietet sie als Download an.
 * Liefert den Dateinamen.
 */
export async function downloadExport(kind: ExportKind, options: ExportOptions): Promise<string> {
  const response = await apiClient.get<Blob>(exportUrl(kind, options), { responseType: 'blob' })
  const fileName = exportFileName(kind, response.headers?.['content-disposition'] as string | undefined)
  saveBlob(response.data, fileName)
  return fileName
}

/** Welche Query-Gruppen ein übernommener Import verändert hat (sie laden danach neu). */
const invalidateAfterImport: Record<ImportKind, readonly QueryKeyPrefix[]> = {
  articles: [queryKeys.articles],
  stock: [queryKeys.stock, queryKeys.reports],
  orders: [queryKeys.orders],
}

/**
 * Import als Multipart-Formular (Feld `file`). Der Content-Type wird ausdrücklich gesetzt, weil der Client sonst wegen seines
 * JSON-Standards das Formular in JSON umwandeln würde. Die Parameter stehen im Query-String (`importUrl`). Ein übernommener Import
 * (`applied`) lässt die betroffenen Listen neu laden, ein Trockenlauf ändert nichts.
 */
export const useImportCsv = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (request: ImportRequest) => {
      const form = new FormData()
      form.append('file', request.file, request.file.name)
      return (await apiClient.post<ImportResultDto>(importUrl(request), form, { headers: { 'Content-Type': 'multipart/form-data' } })).data
    },
    onSuccess: async (result, request) => {
      if (!result.applied) return
      await Promise.all([
        ...invalidateAfterImport[request.kind].map((queryKey) => qc.invalidateQueries({ queryKey })),
        qc.invalidateQueries({ queryKey: queryKeys.audit }),
      ])
    },
  })
}
