import type { ImportKind, ImportResultDto, ImportRowErrorDto } from '../../api/importExportHooks'

/// <summary>Testdaten des CSV-Imports in der Form der API-DTOs (typisiert gegen api/importExportHooks.ts).</summary>

export function makeResult(overrides: Partial<ImportResultDto> = {}): ImportResultDto {
  const base: ImportResultDto = {
    kind: 'articles',
    dryRun: true,
    applied: false,
    delimiter: 'semicolon',
    rows: 3,
    created: 2,
    updated: 1,
    unchanged: 0,
    errorCount: 0,
    errors: [],
    errorsTruncated: false,
    warnings: [],
    summary: '2 neu, 1 aktualisiert, 0 Fehler',
    message: 'Trockenlauf: 2 neu, 1 aktualisiert, 0 Fehler. Es wurde nichts geschrieben.',
    importId: null,
  }
  return { ...base, ...overrides }
}

export function makeRowError(line: number, overrides: Partial<ImportRowErrorDto> = {}): ImportRowErrorDto {
  return { line, key: `SKU-${line}`, code: 'name_missing', message: `Der Name fehlt (Zeile ${line}).`, ...overrides }
}

/** Trockenlauf mit zwei Zeilenfehlern: 2 neu, 1 aktualisiert, 2 Fehler. */
export function makeResultWithErrors(kind: ImportKind = 'articles'): ImportResultDto {
  return makeResult({
    kind,
    rows: 5,
    errorCount: 2,
    errors: [
      makeRowError(3, { key: 'SKU-X', code: 'invalid_gtin', message: 'Prüfziffer der GTIN stimmt nicht (erwartet 1).' }),
      makeRowError(5, { key: null, code: 'sku_missing', message: 'Die SKU fehlt.' }),
    ],
    summary: '2 neu, 1 aktualisiert, 2 Fehler',
    message: 'Trockenlauf: 2 neu, 1 aktualisiert, 2 Fehler. Es wurde nichts geschrieben.',
  })
}

/** Die Antwort der Übernahme zu einem Trockenlauf. */
export function makeApplied(overrides: Partial<ImportResultDto> = {}): ImportResultDto {
  return makeResult({
    dryRun: false,
    applied: true,
    importId: '7d1f1f4e-0000-4000-8000-000000000001',
    message: 'Übernommen: 2 neu, 1 aktualisiert, 0 Fehler.',
    ...overrides,
  })
}

export function csvFile(name = 'artikel.csv', content = 'Sku;Name\nA-1;Schraube\n'): File {
  return new File([content], name, { type: 'text/csv' })
}
