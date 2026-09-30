import type { ComponentType } from 'react'
import type { RoleName } from '../lib/roles'

/// <summary>
/// Route-Registry: Feature-Pakete melden ihre Seite selbst an, statt App.tsx anzufassen.
/// Jede Datei src/features/<name>/route.tsx exportiert (default) einen FeatureRoute oder ein Array davon;
/// registry.ts sammelt sie per import.meta.glob ein (Konvention: src/features/README.md).
/// App.tsx baut Routen und Sidebar-Navigation aus der statischen Liste PLUS diesen Einträgen.
/// </summary>
export interface FeatureRoute {
  /** Absoluter Pfad, z. B. "/labels" oder "/labels/:id". */
  path: string
  /** Die Seite, meist lazy: `lazy(() => import('./LabelsPage'))`. */
  element: ComponentType
  /** Beschriftung in der Sidebar. Ohne Label gibt es nur die Route, keinen Navigationseintrag (z. B. Detailseiten). */
  label?: string
  /** Id der Navigationsgruppe: stammdaten, wareneingang, bestand, auslieferung, auswertung, system — oder eine neue. */
  group?: string
  /** Beschriftung, falls die Gruppe neu angelegt wird (sonst wird die Gruppen-Id angezeigt). */
  groupLabel?: string
  /** Rollen, von denen mindestens eine nötig ist (Admin > Manager > Rolle, siehe lib/roles.ts). Ohne Angabe: jeder Angemeldete. */
  roles?: RoleName[]
  /** Sortierung innerhalb der Gruppe (kleiner = weiter oben). Standard 100; die bestehenden Einträge stehen davor. */
  order?: number
}

/** Was eine route.tsx exportiert. */
export interface FeatureRouteModule {
  default?: FeatureRoute | FeatureRoute[]
}

const DEFAULT_ORDER = 100

function isRoute(value: unknown): value is FeatureRoute {
  if (typeof value !== 'object' || value === null) return false
  const route = value as Partial<FeatureRoute>
  return typeof route.path === 'string' && route.path.startsWith('/') && route.element !== undefined && route.element !== null
}

/**
 * Sammelt die Routen aus den (per Glob geladenen) Feature-Modulen ein. Ungültige Einträge und Pfade, die
 * schon vergeben sind (`reserved` = Pfade der festen Seiten, oder ein früherer Feature-Eintrag), werden
 * mit einer Konsolenwarnung übersprungen, statt die App zu brechen. Reihenfolge: order, dann Label.
 */
export function collectFeatureRoutes(modules: Record<string, FeatureRouteModule>, reserved: readonly string[] = []): FeatureRoute[] {
  const taken = new Set(reserved)
  const routes: FeatureRoute[] = []
  for (const [file, mod] of Object.entries(modules).sort(([a], [b]) => a.localeCompare(b))) {
    const exported = mod.default
    const entries = Array.isArray(exported) ? exported : exported === undefined ? [] : [exported]
    for (const entry of entries) {
      if (!isRoute(entry)) {
        console.warn(`[routes] ${file}: ungültiger Eintrag (path mit "/" und element sind Pflicht) — übersprungen.`)
        continue
      }
      if (taken.has(entry.path)) {
        console.warn(`[routes] ${file}: Pfad ${entry.path} ist schon vergeben — übersprungen.`)
        continue
      }
      taken.add(entry.path)
      routes.push(entry)
    }
  }
  return routes.sort((a, b) => (a.order ?? DEFAULT_ORDER) - (b.order ?? DEFAULT_ORDER) || (a.label ?? a.path).localeCompare(b.label ?? b.path))
}

/** Pfade der festen Seiten in App.tsx — ein Feature darf sie nicht überschreiben. */
export const RESERVED_PATHS: readonly string[] = [
  '/', '/articles', '/articles/new', '/articles/:id', '/stock', '/inbound', '/inventory', '/inventory/:id', '/orders', '/orders/:id',
  '/picklists', '/picklists/:id', '/picklists/:id/pack', '/packing', '/cart-configs', '/layout', '/reports', '/audit', '/users',
  '/suppliers', '/purchase-orders', '/returns', '/customers', '/shipments', '/waves', '/replenishment',
]

// Vite ersetzt das beim Build durch die Liste der vorhandenen route.tsx-Dateien (eager: kein Lade-Umweg für die Navigation;
// die Seiten selbst laden die Features per lazy()). Ohne Feature-Ordner ist das Ergebnis leer.
const modules = import.meta.glob<FeatureRouteModule>('../features/*/route.tsx', { eager: true })

/** Alle angemeldeten Feature-Routen. */
export const featureRoutes: FeatureRoute[] = collectFeatureRoutes(modules, RESERVED_PATHS)
