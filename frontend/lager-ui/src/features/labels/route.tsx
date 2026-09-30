import { lazy } from 'react'
import type { FeatureRoute } from '../../routes/registry'

// Die Seite wird erst beim Aufruf geladen (eigener Chunk mit Code-128-Encoder, Vorschau und CSS).
const LabelsPage = lazy(() => import('./LabelsPage').then((m) => ({ default: m.LabelsPage })))

// Rolle wie der Server (Policy Picker: Picker, Manager, Admin). Die Gruppe "stammdaten" statt "system": die Gruppe "system"
// ist in der Navigation Admins vorbehalten, die Etiketten sollen aber auch Manager und Picker finden.
const routes: FeatureRoute[] = [
  { path: '/labels', label: 'Etiketten', group: 'stammdaten', roles: ['Picker'], order: 50, element: LabelsPage },
]

export default routes
