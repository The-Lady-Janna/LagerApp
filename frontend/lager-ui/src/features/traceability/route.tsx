import { lazy } from 'react'
import type { FeatureRoute } from '../../routes/registry'

// Seite per lazy() laden: die Rückverfolgung landet in einem eigenen Chunk und belastet den Start nicht.
const TraceabilityPage = lazy(() => import('./TraceabilityPage').then((m) => ({ default: m.TraceabilityPage })))

const routes: FeatureRoute[] = [
  // Lesen darf jeder Angemeldete (GET /api/reports/charge/{lot} braucht keine Rolle): keine roles-Angabe.
  { path: '/traceability', label: 'Chargen-Trace', group: 'auswertung', order: 20, element: TraceabilityPage },
]
export default routes
