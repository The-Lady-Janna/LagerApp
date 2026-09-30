import { lazy } from 'react'
import type { FeatureRoute } from '../../routes/registry'

// Die Seite wird erst beim Aufruf geladen (eigener Chunk mit Dialog und Hilfen).
const ImportExportPage = lazy(() => import('./ImportExportPage').then((m) => ({ default: m.ImportExportPage })))

// Rolle wie der Server (Policy Manager: Manager, Admin; der Server erzwingt sie ebenfalls). Die Gruppe "stammdaten" statt "system":
// die Gruppe "system" ist in der Navigation Admins vorbehalten (Gruppen-Rolle in routes/navigation.ts), die Seite ist aber für Manager da.
const routes: FeatureRoute[] = [
  { path: '/import-export', label: 'Import & Export', group: 'stammdaten', roles: ['Manager'], order: 60, element: ImportExportPage },
]

export default routes
