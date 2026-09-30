import { useEffectiveTheme, type EffectiveTheme } from '../state/theme'

/// <summary>
/// Farben der Zeichenflächen (Konva-Canvas: Lager-Layout, Pickroute). Konva kann keine CSS-Variablen
/// auflösen, deshalb stehen die Werte hier — und NUR hier — als Literale; alle übrigen Seiten und
/// Komponenten nutzen die Tokens aus index.css. Hell/Dunkel getrennt, damit die Zeichenfläche dem
/// Theme folgt (useCanvasColors).
/// </summary>
export interface CanvasColors {
  /** Hintergrund und Rahmen der Zeichenfläche, Raster, Achsen, Beschriftung des Ursprungs. */
  surface: string
  border: string
  grid: string
  axis: string
  originLabel: string
  /** Zoom-Hinweis unten rechts. */
  hudBackground: string
  hudText: string
  /** Regale. */
  shelfFill: string
  shelfFillSelected: string
  shelfStroke: string
  shelfText: string
  /** Beschriftung auf den hellen Heatmap-Farben (in beiden Themes dunkel). */
  shelfTextOnHeat: string
  /** Wände. */
  wall: string
  /** Auswahl (Wand/Regal/Pickpunkt), Wand-Stützpunkte. */
  selection: string
  selectionHandleStroke: string
  /** Wand-Entwurf und Pickroute. */
  draft: string
  draftStart: string
  cursorDot: string
  cursorDotSnapped: string
  snapRing: string
  /** Pickpunkte je Typ (Start / Ende / Beides). */
  pickStart: string
  pickEnd: string
  pickBoth: string
  pickOutline: string
  /** Lagerplätze der Pickroute. */
  binFill: string
  binStroke: string
  /** Pickroute: Start-Beschriftung, Nummernmarker. */
  routeStartText: string
  routeMarkerText: string
  waypointFill: string
}

export const CANVAS_LIGHT: CanvasColors = {
  surface: '#fafafa',
  border: '#cbd5e1',
  grid: '#e2e8f0',
  axis: '#94a3b8',
  originLabel: '#64748b',
  hudBackground: 'rgba(255,255,255,0.9)',
  hudText: '#475569',
  shelfFill: '#cbd5e1',
  shelfFillSelected: '#94a3b8',
  shelfStroke: '#475569',
  shelfText: '#1e293b',
  shelfTextOnHeat: '#1e293b',
  wall: '#334155',
  selection: '#1d4ed8',
  selectionHandleStroke: '#ffffff',
  draft: '#2563eb',
  draftStart: '#16a34a',
  cursorDot: '#94a3b8',
  cursorDotSnapped: '#16a34a',
  snapRing: '#eab308',
  pickStart: '#16a34a',
  pickEnd: '#dc2626',
  pickBoth: '#7c3aed',
  pickOutline: '#ffffff',
  binFill: '#fef3c7',
  binStroke: '#d97706',
  routeStartText: '#166534',
  routeMarkerText: '#ffffff',
  waypointFill: '#ffffff',
}

export const CANVAS_DARK: CanvasColors = {
  surface: '#0b1220',
  border: '#475569',
  grid: '#1e293b',
  axis: '#475569',
  originLabel: '#94a3b8',
  hudBackground: 'rgba(15,23,42,0.9)',
  hudText: '#cbd5e1',
  shelfFill: '#334155',
  shelfFillSelected: '#475569',
  shelfStroke: '#94a3b8',
  shelfText: '#f1f5f9',
  shelfTextOnHeat: '#1e293b',
  wall: '#94a3b8',
  selection: '#60a5fa',
  selectionHandleStroke: '#0f172a',
  draft: '#60a5fa',
  draftStart: '#4ade80',
  cursorDot: '#94a3b8',
  cursorDotSnapped: '#4ade80',
  snapRing: '#facc15',
  pickStart: '#4ade80',
  pickEnd: '#f87171',
  pickBoth: '#a78bfa',
  pickOutline: '#0f172a',
  binFill: '#422006',
  binStroke: '#f59e0b',
  routeStartText: '#86efac',
  routeMarkerText: '#0f172a',
  waypointFill: '#0f172a',
}

export function canvasColors(theme: EffectiveTheme): CanvasColors {
  return theme === 'dark' ? CANVAS_DARK : CANVAS_LIGHT
}

/** Die Zeichenflächen-Farben passend zum aktuell angewendeten Theme (wechselt live mit). */
export function useCanvasColors(): CanvasColors {
  return canvasColors(useEffectiveTheme())
}

export type PickPointKind = 'Start' | 'End' | 'Both'

/** Farbe eines Pickpunkts nach Typ (Start / Ende / Beides). */
export function pickPointColor(colors: CanvasColors, type: PickPointKind): string {
  return type === 'Start' ? colors.pickStart : type === 'End' ? colors.pickEnd : colors.pickBoth
}
