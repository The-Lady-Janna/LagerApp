import { useId, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { checkAlternativeSku, splitSkuInput } from './altSkus'

interface Props {
  /** Die eingetragenen Alternativ-SKUs. */
  value: readonly string[]
  onChange: (next: string[]) => void
  /** SKU des bearbeiteten Artikels (nicht sein eigener Ersatz). */
  ownSku: string
  /** Bekannte SKUs (kleingeschrieben → SKU des Artikels); null = Artikelliste noch nicht geladen. */
  knownSkus: ReadonlyMap<string, string> | null
}

const CHIP_STYLE = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 4,
  padding: '2px 4px 2px 10px',
  borderRadius: 999,
  border: '1px solid var(--c-border)',
  background: 'var(--c-surface-alt)',
  fontSize: 13,
} as const

/// <summary>
/// Alternativ-SKUs (Ersatzartikel bei Out-of-Stock) als Chips. Beim Hinzufügen prüft der Editor, dass ein Artikel mit dieser
/// SKU existiert, dass es nicht der Artikel selbst ist und dass die SKU nicht schon eingetragen ist (Groß-/Kleinschreibung
/// egal). Mehrere SKUs lassen sich mit Komma getrennt einfügen. Ein bereits gespeicherter Chip, dessen SKU es nicht mehr gibt,
/// bleibt sichtbar (mit Warnzeichen) und lässt sich entfernen.
/// </summary>
export function AlternativeSkusField({ value, onChange, ownSku, knownSkus }: Props) {
  const { t } = useTranslation()
  const inputId = useId()
  const errorId = useId()
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)

  const add = () => {
    const entries = splitSkuInput(text)
    if (entries.length === 0) {
      setError(t('articles:altSku.enter'))
      return
    }

    const next = [...value]
    const rejected: { entry: string; message: string }[] = []
    for (const entry of entries) {
      const check = checkAlternativeSku({ value: entry, ownSku, existing: next, knownSkus })
      if (check.ok) next.push(check.sku)
      else rejected.push({ entry, message: check.message })
    }

    if (next.length > value.length) onChange(next)
    // Ungültige Einträge bleiben im Feld, damit man sie korrigieren kann; gültige sind schon übernommen.
    setText(rejected.map((r) => r.entry).join(', '))
    setError(rejected.length > 0 ? rejected.map((r) => r.message).join(' ') : null)
  }

  return (
    <div>
      <label htmlFor={inputId}>{t('articles:altSku.add')}</label>
      <div className="row" style={{ gap: 8, marginTop: 4 }}>
        <input
          id={inputId}
          value={text}
          onChange={(e) => { setText(e.target.value); setError(null) }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault() // Enter darf das Formular nicht absenden
              add()
            }
          }}
          aria-invalid={error !== null}
          aria-describedby={error ? errorId : undefined}
          placeholder={t('articles:altSku.placeholder')}
          autoComplete="off"
          style={{ flex: 1, minWidth: 0 }}
        />
        <button type="button" onClick={add}>{t('common:add')}</button>
      </div>
      {error && <p id={errorId} role="alert" style={{ color: 'var(--c-danger)', fontSize: 12, margin: '4px 0 0' }}>{error}</p>}

      {value.length === 0 ? (
        <p className="muted" style={{ fontSize: 12, margin: '8px 0 0' }}>{t('articles:altSku.none')}</p>
      ) : (
        <ul aria-label={t('articles:altSku.list')} style={{ listStyle: 'none', display: 'flex', flexWrap: 'wrap', gap: 6, padding: 0, margin: '8px 0 0' }}>
          {value.map((sku) => {
            const unknown = knownSkus !== null && !knownSkus.has(sku.toLowerCase())
            return (
              <li key={sku} style={{ ...CHIP_STYLE, borderColor: unknown ? 'var(--c-danger)' : 'var(--c-border)' }}>
                <code>{sku}</code>
                {unknown && <span title={t('articles:altSku.unknownTitle')} aria-label={t('articles:altSku.unknownAria')}>⚠</span>}
                <button
                  type="button"
                  onClick={() => onChange(value.filter((s) => s !== sku))}
                  aria-label={t('articles:removeAria', { sku })}
                  style={{ padding: '0 6px', border: 'none', background: 'transparent', fontSize: 16, lineHeight: 1, cursor: 'pointer' }}
                >
                  ×
                </button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
