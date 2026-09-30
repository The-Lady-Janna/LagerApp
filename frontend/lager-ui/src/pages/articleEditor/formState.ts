import type { ArticleDto, BundleComponentRequest, CreateArticleRequest, StackingAxis, UpdateArticleRequest } from '../../api/types'
import { formatNumber } from '../../lib/format'
import { t } from '../../i18n'
import { gtinError, normalizeGtin } from './gtin'
import { seasonWindowError } from './season'

/// <summary>
/// Formularzustand des Artikel-Editors und die Umwandlung in den Request. Ohne React, damit die Regeln testbar sind:
/// was gesendet wird (Datenverlust-Fix: Alternativ-SKUs, Saison-Fenster, Bundle-Komponenten, GTIN gehen unverändert oder
/// geändert im PUT mit) und welche Eingaben das Speichern sperren.
/// </summary>

/** Höchstmenge je Bundle-Komponente (wie der Server-Validator). */
export const MAX_COMPONENT_QUANTITY = 1_000_000

export interface FormState {
  sku: string
  name: string
  description: string
  lengthMm: number
  widthMm: number
  heightMm: number
  weightGrams: number
  isStackable: boolean
  stackingAxis: StackingAxis
  stackingIncrementMm: number
  maxStackCount: number
  minStock: number
  reorderPoint: number
  maxStock: number
  primarySupplierId: string
  purchasePriceCents: number
  /** GTIN/EAN wie getippt (Leerraum erlaubt); '' = keine. */
  gtin: string
  alternativeSkus: string[]
  /** Server-Wert (Mitternacht) oder null; das Datumsfeld zeigt den Tag. */
  validFrom: string | null
  validUntil: string | null
  bundleComponents: BundleComponentRequest[]
}

export const EMPTY_FORM: FormState = {
  sku: '',
  name: '',
  description: '',
  lengthMm: 0,
  widthMm: 0,
  heightMm: 0,
  weightGrams: 0,
  isStackable: false,
  stackingAxis: 'Z',
  stackingIncrementMm: 0,
  maxStackCount: 1,
  minStock: 0,
  reorderPoint: 0,
  maxStock: 0,
  primarySupplierId: '',
  purchasePriceCents: 0,
  gtin: '',
  alternativeSkus: [],
  validFrom: null,
  validUntil: null,
  bundleComponents: [],
}

export function toFormState(a: ArticleDto): FormState {
  return {
    sku: a.sku,
    name: a.name,
    description: a.description ?? '',
    lengthMm: a.dimensions.lengthMm,
    widthMm: a.dimensions.widthMm,
    heightMm: a.dimensions.heightMm,
    weightGrams: a.weightGrams,
    isStackable: a.stacking.isStackable,
    stackingAxis: a.stacking.stackingAxis,
    stackingIncrementMm: a.stacking.stackingIncrementMm,
    maxStackCount: a.stacking.maxStackCount ?? 1,
    minStock: a.minStock,
    reorderPoint: a.reorderPoint,
    maxStock: a.maxStock,
    primarySupplierId: a.primarySupplierId ?? '',
    purchasePriceCents: a.purchasePriceCents,
    gtin: a.gtin ?? '',
    alternativeSkus: a.alternativeSkus ?? [],
    validFrom: a.validFrom,
    validUntil: a.validUntil,
    bundleComponents: (a.bundleComponents ?? []).map((c) => ({ componentArticleId: c.componentArticleId, quantity: c.quantity })),
  }
}

/** Unterscheidet sich der Formularstand vom Ausgangsstand (dem geladenen Artikel bzw. dem leeren Formular)? */
export function isFormDirty(form: FormState, base: FormState): boolean {
  return JSON.stringify(form) !== JSON.stringify(base)
}

export interface FormErrors {
  gtin?: string
  season?: string
  bundle?: string
}

/** Eingaben, die das Speichern sperren (der Server prüft dasselbe noch einmal). */
export function validateForm(form: FormState): FormErrors {
  const errors: FormErrors = {}
  const gtin = gtinError(form.gtin)
  if (gtin) errors.gtin = gtin
  const season = seasonWindowError(form.validFrom, form.validUntil)
  if (season) errors.season = season
  if (form.bundleComponents.some((c) => !Number.isInteger(c.quantity) || c.quantity < 1 || c.quantity > MAX_COMPONENT_QUANTITY)) {
    errors.bundle = t('articles:bundle.quantityRange', { max: formatNumber(MAX_COMPONENT_QUANTITY) })
  }
  return errors
}

export function hasErrors(errors: FormErrors): boolean {
  return Object.keys(errors).length > 0
}

interface PayloadOptions {
  /** Neuanlage (POST): leere GTIN = keine; beim Ändern (PUT) bedeutet '' "GTIN entfernen". */
  isNew: boolean
  /** Der Nutzer hat die Bundle-Komponenten bearbeitet (auch wenn die Liste dabei leer wurde = Bundle auflösen). */
  bundleTouched: boolean
}

/**
 * Der Request zum Speichern. Alles wird mitgesendet, was der Server bei einem fehlenden Wert zurücksetzen würde (Alternativ-SKUs,
 * Saison-Fenster): unverändert, wenn der Nutzer sie nicht angefasst hat (Datenverlust-Fix).
 *  - GTIN: beim Ändern immer als Text ('' = entfernen), beim Anlegen null statt ''.
 *  - Bundle-Komponenten: nur, wenn der Artikel welche hat oder der Nutzer sie bearbeitet hat; sonst bleibt das Feld weg, der
 *    Server lässt die Komponenten dann unverändert.
 */
export function buildPayload(form: FormState, { isNew, bundleTouched }: PayloadOptions): UpdateArticleRequest {
  const gtin = normalizeGtin(form.gtin)
  const payload: UpdateArticleRequest = {
    name: form.name,
    description: form.description || null,
    dimensions: { lengthMm: form.lengthMm, widthMm: form.widthMm, heightMm: form.heightMm },
    weightGrams: form.weightGrams,
    stacking: {
      isStackable: form.isStackable,
      stackingAxis: form.stackingAxis,
      stackingIncrementMm: form.stackingIncrementMm,
      maxStackCount: form.maxStackCount,
    },
    minStock: form.minStock,
    reorderPoint: form.reorderPoint,
    maxStock: form.maxStock,
    primarySupplierId: form.primarySupplierId || null,
    purchasePriceCents: form.purchasePriceCents,
    alternativeSkus: form.alternativeSkus,
    validFrom: form.validFrom,
    validUntil: form.validUntil,
    gtin: isNew ? gtin || null : gtin,
  }
  if (form.bundleComponents.length > 0 || bundleTouched) {
    payload.bundleComponents = form.bundleComponents.map((c) => ({ componentArticleId: c.componentArticleId, quantity: c.quantity }))
  }
  return payload
}

export function buildCreatePayload(form: FormState, bundleTouched: boolean): CreateArticleRequest {
  return { ...buildPayload(form, { isNew: true, bundleTouched }), sku: form.sku }
}
