# Feature-Pakete (`src/features/`)

Neue Seiten kommen hierher — `App.tsx` wird dafür **nicht** mehr angefasst. Die bestehenden Seiten bleiben in
`src/pages`.

## Konvention

Ein Feature ist ein Ordner mit einer `route.tsx`, daneben liegen Seite, Hooks und Hilfen des Features:

```
src/features/labels/
  route.tsx          meldet die Seite(n) an (Pflicht)
  LabelsPage.tsx     die Seite
  useLabels.ts       Hooks / API-Aufrufe des Features
```

`route.tsx` exportiert (default) einen `FeatureRoute` oder ein Array davon:

```tsx
import { lazy } from 'react'
import type { FeatureRoute } from '../../routes/registry'

// Seiten per lazy() laden: das Feature landet in einem eigenen Chunk und belastet den Start nicht.
const LabelsPage = lazy(() => import('./LabelsPage').then((m) => ({ default: m.LabelsPage })))

const routes: FeatureRoute[] = [
  { path: '/labels', label: 'Etiketten', group: 'system', roles: ['Manager'], order: 20, element: LabelsPage },
]
export default routes
```

| Feld         | Bedeutung                                                                                             |
| ------------ | ----------------------------------------------------------------------------------------------------- |
| `path`       | absoluter Pfad, beginnt mit `/` (auch Detailseiten wie `/labels/:id`)                                 |
| `element`    | die Seite (`lazy(...)` oder eine normale Komponente)                                                  |
| `label`      | Text in der Sidebar; **ohne `label` gibt es nur die Route, keinen Navigationseintrag**                |
| `group`      | Navigationsgruppe: `stammdaten`, `wareneingang`, `bestand`, `auslieferung`, `auswertung`, `system` — oder eine neue Id (dann `groupLabel` angeben) |
| `groupLabel` | Beschriftung einer neu angelegten Gruppe                                                              |
| `roles`      | Rollen, von denen mindestens eine nötig ist (Admin > Manager > Rolle); ohne Angabe jeder Angemeldete. Filtert Navigation **und** Route (`RequireRole`). Der Server bleibt maßgeblich. |
| `order`      | Reihenfolge innerhalb der Gruppe (kleiner = weiter oben, Standard 100); die festen Einträge stehen davor |

Ein Pfad, den schon eine feste Seite belegt (siehe `RESERVED_PATHS` in `routes/registry.ts`) oder ein früheres
Feature, wird mit einer Konsolenwarnung übersprungen.

## Bausteine, die jedes Feature nutzen soll

- Farben nur als `var(--c-…)`/CSS-Klassen aus `src/index.css`; keine Hex-Werte in `.tsx` (ESLint-Regel).
- Texte nie im Code: `useTranslation()` und `t('namespace:schluessel')` mit Einträgen in `src/locales/de` **und** `en` (Konvention und
  Prüfskript: `src/locales/README.md`). Das `label` der Route bleibt der deutsche Text; die Sidebar übersetzt es über
  `nav:routes.<pfad ohne "/">` (neue Gruppe: `nav:groups.<id>`).
- `components/Modal` (oder `ConfirmDialog`) für Dialoge, `ErrorBanner`/`LoadState` für Fehler- und Ladezustände,
  `StatusPill` für Status, `StatTile` für Kennzahlen, `CollapsibleCard` für aufklappbare Karten.
- Formularfelder mit `<label>` (umschließend oder `htmlFor`), Icon-Buttons mit `aria-label`, Tabellen mit
  `<th scope="col">`.
- Tests unter `src/tests/<WP-ID>/`.
