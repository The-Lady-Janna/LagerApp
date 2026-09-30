import { useEffect, useId, useMemo, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { useArticles, useCustomers, useOrders, useStorageLocations } from '../api/hooks'
import { Modal } from './Modal'

interface SearchHit {
  kind: 'article' | 'bin' | 'order' | 'customer'
  label: string
  sublabel: string
  path: string
}

/// <summary>
/// Cmd+K / Ctrl+K öffnet einen Such-Dialog (Modal: role="dialog", Fokusfalle, Fokus-Rückgabe). Sucht clientside in den bereits geladenen
/// Stammdaten (Articles, Bins, Orders, Customers). Pfeiltasten + Enter
/// navigieren (Combobox-Semantik: Eingabefeld + Listbox mit aria-activedescendant). ESC schließt. Score: einfache "starts-with then contains"-
/// Heuristik nach Lowercase-Kollation.
///
/// Aufgeteilt in eine Hülle (Hotkey + open-Zustand) und den SearchDialog, der
/// nur bei geöffnetem Modal existiert: sein State (Suchtext, Cursor) startet so
/// bei jedem Öffnen frisch, und die vier Stammdaten-Queries laufen erst dann.
/// </summary>
export function GlobalSearch() {
  const [open, setOpen] = useState(false)

  // Hotkey Cmd+K / Ctrl+K
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setOpen(true)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  if (!open) return null
  return <SearchDialog onClose={() => setOpen(false)} />
}

function SearchDialog({ onClose }: { onClose: () => void }) {
  const [q, setQ] = useState('')
  const [cursor, setCursor] = useState(0)
  const navigate = useNavigate()
  const { t } = useTranslation()
  const listId = useId()
  const optionId = (index: number) => `${listId}-option-${index}`

  const articles = useArticles()
  const bins = useStorageLocations()
  const orders = useOrders()
  const customers = useCustomers()
  const loading = articles.isLoading || bins.isLoading || orders.isLoading || customers.isLoading

  const hits = useMemo<SearchHit[]>(() => {
    const needle = q.trim().toLowerCase()
    if (needle.length < 2) return []
    const results: SearchHit[] = []
    const score = (text: string) => {
      const lower = text.toLowerCase()
      if (lower.startsWith(needle)) return 100
      if (lower.includes(needle)) return 50
      return 0
    }
    for (const a of articles.data ?? []) {
      const s = Math.max(score(a.sku), score(a.name))
      if (s > 0) results.push({ kind: 'article', label: `${a.sku} — ${a.name}`, sublabel: a.description ?? '', path: `/articles/${a.id}` })
    }
    for (const b of bins.data ?? []) {
      if (score(b.code) > 0) results.push({ kind: 'bin', label: b.code, sublabel: `${b.widthMm}×${b.depthMm}×${b.heightMm} mm`, path: `/layout` })
    }
    for (const o of orders.data ?? []) {
      const s = Math.max(score(o.orderNumber), score(o.customerReference ?? ''))
      if (s > 0) results.push({ kind: 'order', label: o.orderNumber, sublabel: `${t(`status:order.${o.status}`, { defaultValue: o.status })} · ${o.customerReference ?? '—'}`, path: `/orders/${o.id}` })
    }
    for (const c of customers.data ?? []) {
      const s = Math.max(score(c.code), score(c.name))
      if (s > 0) results.push({ kind: 'customer', label: `${c.code} — ${c.name}`, sublabel: c.email ?? '', path: `/customers` })
    }
    return results.slice(0, 25)
  }, [q, articles.data, bins.data, orders.data, customers.data, t])

  const onChange = (value: string) => {
    setQ(value)
    setCursor(0)
  }

  const goto = (hit: SearchHit) => {
    navigate(hit.path)
    onClose()
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setCursor((c) => Math.min(c + 1, hits.length - 1)) }
    if (e.key === 'ArrowUp') { e.preventDefault(); setCursor((c) => Math.max(c - 1, 0)) }
    if (e.key === 'Enter' && hits[cursor]) { e.preventDefault(); goto(hits[cursor]) }
  }

  const showEmpty = q.length >= 2 && hits.length === 0
  const status = q.trim().length < 2
    ? ''
    : hits.length > 0 ? t('nav:search.hits', { count: hits.length }) : loading ? t('nav:search.loading') : t('nav:search.noHits')

  return (
    <Modal title={t('nav:search.title')} hideTitle placement="top" appearance="flush" width={600} onClose={onClose} closeOnBackdrop>
      <input
        data-autofocus
        type="search"
        role="combobox"
        aria-label={t('nav:search.aria')}
        aria-expanded={hits.length > 0}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={hits[cursor] ? optionId(cursor) : undefined}
        className="search-input"
        value={q}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        placeholder={t('nav:search.placeholder')}
      />
      <div role="status" className="visually-hidden">{status}</div>
      <ul id={listId} role="listbox" aria-label={t('nav:search.results')} className="search-results" hidden={hits.length === 0}>
        {hits.map((h, i) => (
          <li
            key={`${h.kind}-${h.label}-${i}`}
            id={optionId(i)}
            role="option"
            aria-selected={cursor === i}
            className="search-result"
            onMouseEnter={() => setCursor(i)}
            onClick={() => goto(h)}
          >
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
              <strong>{h.label}</strong>
              <span className="search-kind">{t(`nav:search.kind.${h.kind}`)}</span>
            </div>
            {h.sublabel && <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{h.sublabel}</div>}
          </li>
        ))}
      </ul>
      {showEmpty && (
        <div className="muted" style={{ padding: 24, textAlign: 'center' }}>
          {loading ? t('nav:search.loading') : t('nav:search.noHits')}
        </div>
      )}
      <div className="muted" style={{ padding: '8px 16px', fontSize: 12, borderTop: '1px solid var(--c-border-light)' }}>
        <Trans i18nKey="nav:search.footer" components={{ kbd: <kbd /> }} />
      </div>
    </Modal>
  )
}
