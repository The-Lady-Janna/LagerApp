import type { ReactNode } from 'react'

interface Props {
  label: string
  value: ReactNode
  /** Farbe des Werts: Standardtext, oder Erfolg/Warnung/Fehler/gedämpft. */
  tone?: 'success' | 'warning' | 'danger' | 'muted'
  /** Erklärung als Tooltip. */
  tooltip?: string
}

/// <summary>
/// Kennzahlen-Kachel (Übersichtskarten der Bestell- und Pack-Seite): eine Fläche in Theme-Farben mit kleinem Label
/// und großem Wert. Vorher hatten OrdersPage (DashTile) und PackingOverviewPage (Tile) je eine eigene Variante mit
/// festem weißem Hintergrund, die im Dark-Mode grell aus der Seite fiel.
/// </summary>
export function StatTile({ label, value, tone, tooltip }: Props) {
  return (
    <div className={tone ? `stat-tile stat-tile--${tone}` : 'stat-tile'} title={tooltip}>
      <div className="stat-tile-label">{label}</div>
      <div className="stat-tile-value">{value}</div>
    </div>
  )
}
