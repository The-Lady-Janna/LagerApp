import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { useArticle, useArticles, useCreateArticle, useSuppliers, useUpdateArticle } from '../../api/hooks'
import { parseApiError } from '../../api/errors'
import type { StackingAxis } from '../../api/types'
import { ErrorBanner } from '../../components/ErrorBanner'
import { formatMoney } from '../../lib/format'
import { AlternativeSkusField } from './AlternativeSkusField'
import { BundleBadge, SeasonBadge } from './ArticleBadges'
import { BundleComponentsField } from './BundleComponentsField'
import {
  buildCreatePayload,
  buildPayload,
  EMPTY_FORM,
  hasErrors,
  isFormDirty,
  toFormState,
  validateForm,
  type FormState,
} from './formState'
import { gtinHint } from './gtin'
import { dateInputValue, isInSeason, seasonValueFromInput } from './season'
import { useUnsavedChangesGuard } from './useUnsavedChangesGuard'

const HINT_STYLE = { fontSize: 12, margin: '4px 0 0' } as const

/// <summary>
/// Der Artikel-Editor selbst. Liegt in einer eigenen Datei, damit ArticleEditorPage ihn nachladen kann (React.lazy): der Editor
/// samt Bundle-, Alternativ-SKU- und Saison-Feldern gehört nicht in das Hauptpaket der App.
/// </summary>
export function ArticleEditor({ id }: { id: string | undefined }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const isNew = !id
  const { data: existing, isError: loadFailed, error: loadError, refetch } = useArticle(id)
  const createMut = useCreateArticle()
  const updateMut = useUpdateArticle(id ?? '')
  const { data: suppliers } = useSuppliers()
  // Alle Artikel: für die Existenzprüfung der Alternativ-SKUs und die Suche nach Bundle-Komponenten.
  const { data: articles } = useArticles()
  // Nur die vom User geänderten Felder. Das Formular ist der geladene Artikel
  // (bzw. EMPTY_FORM beim Neuanlegen) plus diese Änderungen — so überschreibt ein
  // Refetch (z. B. nach stale Cache-Daten) keine Eingaben, und es braucht
  // keinen Effect, der den Server-Stand in den State kopiert.
  const [draft, setDraft] = useState<Partial<FormState>>({})
  const base: FormState = existing ? toFormState(existing) : EMPTY_FORM
  const form: FormState = { ...base, ...draft }

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setDraft((d) => ({ ...d, [key]: value }))

  const mutation = isNew ? createMut : updateMut
  const errors = validateForm(form)
  // Nach erfolgreichem Speichern ist nichts mehr ungespeichert (die Navigation zur Liste läuft schon).
  const dirty = !mutation.isSuccess && isFormDirty(form, base)
  const { requestLeave, leaveDialog } = useUnsavedChangesGuard(dirty)

  // Kleingeschriebene SKU → SKU in der Schreibweise des Artikels (Existenzprüfung der Alternativ-SKUs).
  const knownSkus = useMemo(
    () => (articles ? new Map(articles.map((a) => [a.sku.toLowerCase(), a.sku] as const)) : null),
    [articles],
  )

  // mutate statt mutateAsync: ein Fehler bleibt in mutation.error (ErrorBanner unten), keine unbehandelte Rejection.
  const submit = () => {
    if (hasErrors(errors)) return
    // Bundle-Komponenten nur senden, wenn der Artikel welche hat oder sie bearbeitet wurden (sonst lässt der Server sie unverändert).
    const bundleTouched = 'bundleComponents' in draft
    const onSuccess = () => navigate('/articles')
    if (isNew) createMut.mutate(buildCreatePayload(form, bundleTouched), { onSuccess })
    else updateMut.mutate(buildPayload(form, { isNew: false, bundleTouched }), { onSuccess })
  }

  // Ein bestehender Artikel wird erst bearbeitet, wenn er geladen ist: sonst würde ein Speichern das
  // leere Formular (EMPTY_FORM) über den echten Artikel schreiben.
  if (!isNew && !existing) {
    return loadFailed
      ? <ErrorBanner error={loadError} title={t('articles:editor.loadFailed')} onRetry={() => void refetch()} />
      : <p className="muted" role="status">{t('articles:editor.loading')}</p>
  }

  // Konflikte (409) und Feldfehler (400) des Servers am betroffenen Feld kenntlich machen; der Text steht im ErrorBanner unten.
  const serverError = mutation.error ? parseApiError(mutation.error) : null
  // Beim Anlegen ist ein 409 ohne besonderen Code die doppelte SKU (die doppelte GTIN trägt den Code duplicate_gtin).
  const skuConflict = isNew && serverError?.status === 409 && serverError.code === 'conflict'
    ? t('articles:editor.skuTaken')
    : serverError?.fieldErrors?.Sku?.[0]
  const gtinServerError = serverError?.code === 'duplicate_gtin'
    ? t('articles:editor.gtinTaken')
    : serverError?.fieldErrors?.Gtin?.[0]

  const gtin = gtinHint(form.gtin)
  const gtinMessage = gtin.status === 'invalid' ? gtin.message : gtinServerError
  const inSeason = isInSeason(form.validFrom, form.validUntil)
  const skuById = new Map((articles ?? []).map((a) => [a.id, a.sku] as const))
  const isBundle = form.bundleComponents.length > 0

  return (
    <>
      <h2>{isNew ? t('articles:editor.titleNew') : t('articles:editor.titleEdit', { sku: form.sku })}</h2>
      <div className="row" style={{ marginBottom: 12, flexWrap: 'wrap' }}>
        <BundleBadge
          article={{ isBundle, bundleComponents: form.bundleComponents.map((c) => ({ ...c, componentSku: skuById.get(c.componentArticleId) ?? '' })) }}
        />
        <SeasonBadge active={inSeason} validFrom={form.validFrom} validUntil={form.validUntil} />
      </div>
      <div className="card">
        <div className="grid-3">
          <div>
            <label>
              {t('articles:col.sku')}
              <input value={form.sku} onChange={(e) => update('sku', e.target.value)} disabled={!isNew} aria-invalid={!!skuConflict} />
            </label>
            {skuConflict && <p role="status" style={{ ...HINT_STYLE, color: 'var(--c-danger)' }}>{skuConflict}</p>}
          </div>
          <label>
            {t('articles:col.name')}
            <input value={form.name} onChange={(e) => update('name', e.target.value)} />
          </label>
          <label>
            {t('articles:col.weight')}
            <input type="number" value={form.weightGrams} onChange={(e) => update('weightGrams', +e.target.value)} />
          </label>
        </div>
        <label style={{ marginTop: 12 }}>
          {t('articles:editor.description')}
          <textarea value={form.description} onChange={(e) => update('description', e.target.value)} rows={2} />
        </label>

        <h3>{t('articles:editor.identifiers')}</h3>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          {t('articles:editor.identifiersHint')}
        </p>
        <div className="grid-2">
          <div>
            <label>
              {t('articles:editor.gtin')}
              <input
                value={form.gtin}
                onChange={(e) => update('gtin', e.target.value)}
                inputMode="numeric"
                autoComplete="off"
                placeholder={t('articles:editor.gtinPlaceholder')}
                aria-invalid={!!gtinMessage}
              />
            </label>
            {gtin.status === 'valid' && !gtinServerError && (
              <p role="status" style={{ ...HINT_STYLE, color: 'var(--c-success)' }}>{gtin.message}</p>
            )}
            {gtinMessage && <p role="status" style={{ ...HINT_STYLE, color: 'var(--c-danger)' }}>{gtinMessage}</p>}
          </div>
          <AlternativeSkusField
            value={form.alternativeSkus}
            onChange={(next) => update('alternativeSkus', next)}
            ownSku={form.sku}
            knownSkus={knownSkus}
          />
        </div>

        <h3>{t('articles:editor.dimensions')}</h3>
        <div className="grid-3">
          <label>{t('articles:editor.length')}<input type="number" value={form.lengthMm} onChange={(e) => update('lengthMm', +e.target.value)} /></label>
          <label>{t('articles:editor.width')}<input type="number" value={form.widthMm} onChange={(e) => update('widthMm', +e.target.value)} /></label>
          <label>{t('articles:editor.height')}<input type="number" value={form.heightMm} onChange={(e) => update('heightMm', +e.target.value)} /></label>
        </div>
        <h3>{t('articles:editor.stacking')}</h3>
        <label className="row">
          <input type="checkbox" checked={form.isStackable} onChange={(e) => update('isStackable', e.target.checked)} />
          {t('articles:col.stackable')}
        </label>
        {form.isStackable && (
          <div className="grid-3" style={{ marginTop: 12 }}>
            <label>
              {t('articles:editor.axis')}
              <select value={form.stackingAxis} onChange={(e) => update('stackingAxis', e.target.value as StackingAxis)}>
                <option value="X">X</option>
                <option value="Y">Y</option>
                <option value="Z">{t('articles:editor.axisZ')}</option>
              </select>
            </label>
            <label>
              {t('articles:editor.increment')}
              <input type="number" value={form.stackingIncrementMm} onChange={(e) => update('stackingIncrementMm', +e.target.value)} />
            </label>
            <label>
              {t('articles:editor.maxStack')}
              <input type="number" value={form.maxStackCount} onChange={(e) => update('maxStackCount', +e.target.value)} />
            </label>
          </div>
        )}
        <h3>{t('articles:editor.thresholds')}</h3>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          {t('articles:editor.thresholdsHint')}
        </p>
        <div className="grid-3">
          <label>{t('articles:editor.minStock')} <input type="number" min={0} value={form.minStock} onChange={(e) => update('minStock', +e.target.value)} /></label>
          <label>{t('articles:editor.reorderPoint')} <input type="number" min={0} value={form.reorderPoint} onChange={(e) => update('reorderPoint', +e.target.value)} /></label>
          <label>{t('articles:editor.maxStock')} <input type="number" min={0} value={form.maxStock} onChange={(e) => update('maxStock', +e.target.value)} /></label>
        </div>

        <h3>{t('articles:editor.procurement')}</h3>
        <div className="grid-2">
          <label>{t('articles:editor.supplier')}
            <select value={form.primarySupplierId} onChange={(e) => update('primarySupplierId', e.target.value)}>
              <option value="">{t('articles:editor.noSupplier')}</option>
              {suppliers?.map((s) => <option key={s.id} value={s.id}>{s.code} — {s.name}</option>)}
            </select>
          </label>
          <label>{t('articles:editor.price')}
            <input type="number" min={0} value={form.purchasePriceCents} onChange={(e) => update('purchasePriceCents', +e.target.value)} />
            <span className="muted" style={{ fontSize: 11, marginLeft: 6 }}>
              {formatMoney(form.purchasePriceCents)}
            </span>
          </label>
        </div>

        <h3>{t('articles:editor.season')}</h3>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          {t('articles:editor.seasonHint')}
        </p>
        <div className="grid-2">
          <label>
            {t('articles:editor.validFrom')}
            <input
              type="date"
              value={dateInputValue(form.validFrom)}
              onChange={(e) => update('validFrom', seasonValueFromInput(e.target.value))}
            />
          </label>
          <label>
            {t('articles:editor.validUntil')}
            <input
              type="date"
              value={dateInputValue(form.validUntil)}
              onChange={(e) => update('validUntil', seasonValueFromInput(e.target.value))}
              aria-invalid={!!errors.season}
            />
          </label>
        </div>
        {errors.season && <p role="alert" style={{ ...HINT_STYLE, color: 'var(--c-danger)' }}>{errors.season}</p>}

        <h3>{t('articles:editor.bundle')}</h3>
        <BundleComponentsField
          articleId={id}
          rows={form.bundleComponents}
          onChange={(rows) => update('bundleComponents', rows)}
          articles={articles}
          error={errors.bundle}
        />

        <div className="toolbar" style={{ marginTop: 16 }}>
          <button className="primary" onClick={submit} disabled={mutation.isPending || hasErrors(errors)}>
            {mutation.isPending ? t('common:saving') : t('common:save')}
          </button>
          <button onClick={() => requestLeave('/articles')}>{t('common:cancel')}</button>
          {dirty && <span className="muted" style={{ fontSize: 12 }}>{t('articles:editor.unsaved')}</span>}
        </div>
        <ErrorBanner error={mutation.error} title={t('articles:editor.saveFailed')} onDismiss={mutation.reset} style={{ marginTop: 8 }} />
      </div>
      {leaveDialog}
    </>
  )
}
