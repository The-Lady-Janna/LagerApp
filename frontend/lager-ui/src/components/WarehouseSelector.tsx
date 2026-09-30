import { useEffect, useId } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { useWarehouseLayout } from '../api/hooks'
import { useActiveWarehouse } from '../state/activeWarehouse'
import { useAuth } from '../state/auth'

/// <summary>
/// Sidebar dropdown that picks the "active" warehouse. The list comes from the
/// layout endpoint (one warehouse per row at the top of the tree). If only one
/// warehouse exists the selector hides itself — pointless UI noise.
/// Auto-selects the first warehouse on first load so warehouse-scoped pages
/// always have a non-null id to filter by.
/// Gibt es noch gar kein Lager (leere Datenbank ohne Demodaten), zeigt er stattdessen einen Hinweis
/// mit Link zur Lagerstruktur (Manager) bzw. den Hinweis, dass ein Manager zuerst ein Lager anlegen muss.
/// </summary>
export function WarehouseSelector() {
  const { data: warehouses } = useWarehouseLayout()
  const { activeId, setActive } = useActiveWarehouse()
  const selectId = useId()
  const canManage = useAuth((s) => s.hasRole('Manager'))
  const { t } = useTranslation()

  // Auto-select first warehouse once we know what's available.
  useEffect(() => {
    if (!warehouses || warehouses.length === 0) return
    if (activeId && warehouses.some((w) => w.id === activeId)) return
    setActive(warehouses[0].id)
  }, [warehouses, activeId, setActive])

  if (warehouses && warehouses.length === 0) {
    return (
      <div className="warehouse-select">
        {canManage
          ? <Link to="/warehouses" style={{ color: 'var(--c-sidebar-text)' }}>{t('warehouse:selector.none')}</Link>
          : <span style={{ color: 'var(--c-sidebar-muted)' }}>{t('warehouse:selector.noneManager')}</span>}
      </div>
    )
  }

  if (!warehouses || warehouses.length <= 1) return null

  return (
    <div className="warehouse-select">
      <label htmlFor={selectId}>{t('warehouse:selector.active')}</label>
      <select
        id={selectId}
        value={activeId ?? ''}
        onChange={(e) => setActive(e.target.value || null)}
      >
        {warehouses.map((w) => (
          <option key={w.id} value={w.id}>{w.code} — {w.name}</option>
        ))}
      </select>
    </div>
  )
}
