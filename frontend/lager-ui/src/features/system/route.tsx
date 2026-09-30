import { lazy } from 'react'
import type { FeatureRoute } from '../../routes/registry'

// Die Seite per lazy() laden: das Feature landet in einem eigenen Chunk und belastet den Start nicht.
const SystemPage = lazy(() => import('./SystemPage').then((m) => ({ default: m.SystemPage })))

// System-Seite (Backup und Restore): Sidebar-Gruppe "System", nur für Admins (Navigation und Route; der Server prüft ebenfalls).
const routes: FeatureRoute[] = [
  { path: '/system', label: 'Backup & Restore', group: 'system', roles: ['Admin'], order: 20, element: SystemPage },
]
export default routes
