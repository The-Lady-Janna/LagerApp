import { lazy } from 'react'
import type { FeatureRoute } from '../../routes/registry'

// Seite per lazy() laden: die Lagerstruktur landet in einem eigenen Chunk und belastet den Start nicht.
const WarehousesPage = lazy(() => import('./WarehousesPage').then((m) => ({ default: m.WarehousesPage })))

// Lager, Zonen, Gänge, Regale und Lagerplätze pflegen (Stammdaten, nur Manager - der Server erzwingt die Rolle ebenfalls).
const routes: FeatureRoute[] = [
  { path: '/warehouses', label: 'Lagerstruktur', group: 'stammdaten', roles: ['Manager'], order: 10, element: WarehousesPage },
]

export default routes
