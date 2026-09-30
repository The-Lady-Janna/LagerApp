# Lager-UI

Weboberfläche des selbst gehosteten Warehouse-Management-Systems **Lager** (React 19, Vite, TypeScript,
TanStack Query, Zustand, React Router, Konva für die Lagerplan-Ansichten). Das Backend (ASP.NET Core) und die
Gesamtdokumentation stehen im [Wurzel-README](../../README.md) und unter [`docs/`](../../docs).

## Skripte

| Befehl              | Zweck                                                              |
| ------------------- | ------------------------------------------------------------------ |
| `npm run dev`       | Dev-Server auf <http://localhost:5173> (proxied `/api` ans Backend) |
| `npm run build`     | Typprüfung (`tsc -b`) und Produktions-Build nach `dist/`           |
| `npm run typecheck` | nur die Typprüfung                                                 |
| `npm run lint`      | ESLint                                                             |
| `npm run i18n:check`| Sprachdateien de/en prüfen (Parität, fehlende/ungenutzte Schlüssel) |
| `npm test`          | Vitest (jsdom + Testing Library), einmalig                         |
| `npm run test:watch`| Vitest im Watch-Modus                                              |
| `npm run preview`   | den Build lokal ausliefern                                         |

Vor einem Commit müssen `lint`, `typecheck`, `build` und `test` grün sein.

## Backend-Adresse (`VITE_API_TARGET`)

Der Dev-Server leitet `/api` an `http://localhost:5099` weiter. Läuft das Backend woanders, die Adresse per
Umgebungsvariable oder in einer `.env.local` (nicht eingecheckt) setzen:

```
VITE_API_TARGET=http://192.168.1.20:5099
```

Für Handy-Tests im WLAN startet man den Dev-Server mit `npm run dev -- --host`. Kamera und Barcode-Erkennung
brauchen einen sicheren Kontext (HTTPS oder `localhost`); über `http://<PC-IP>` erscheint im Scanner die Handeingabe.

## Struktur

```
src/
  api/          axios-Client, Fehlerauswertung (errors.ts), Typen und React-Query-Hooks
  components/   wiederverwendbare Bausteine (Modal, ConfirmDialog, StatusPill, StatTile, CollapsibleCard,
                ErrorBanner, LoadState, Sidebar, Canvas-Komponenten, Layout-Editor-Teile …)
  features/     Feature-Pakete: je Ordner eine route.tsx (siehe unten) + Seite/Hooks
  i18n/         Einrichtung von i18next/react-i18next (Sprache, Erkennung, <html lang>)
  locales/      Sprachdateien de/ en/ je Namespace (siehe locales/README.md)
  lib/          reine Hilfen (Formate, Rollen, Status-Töne, Canvas-Farben, Geometrie …)
  pages/        die bestehenden Seiten
  routes/       Route-Registry und Navigation
  state/        Zustand-Stores (Auth, Theme, Lager-Auswahl, Toasts)
  tests/        Vitest-Tests, je Arbeitspaket ein Ordner (tests/WPxx), gemeinsame Helfer in tests/helpers
public/         Manifest, Service-Worker (sw.js), Icons
```

## Sprachen (Deutsch/Englisch)

Die Oberfläche ist zweisprachig (react-i18next). **Deutsch** ist Standardsprache und Quelle aller Texte, **Englisch** die vollständige
Übersetzung; der Umschalter DE/EN sitzt in der Seitenleiste und auf der Anmeldeseite, die Wahl liegt in `localStorage` (`lager.lang`),
ohne Wahl folgt die Sprache dem Browser. Texte stehen nicht im Code, sondern in `src/locales/<sprache>/<namespace>.json` und werden mit
`t('namespace:schluessel')` benutzt; Datum, Zahl und Betrag folgen der Sprache (`src/lib/format.ts`). Backend-Meldungen bleiben deutsch,
häufige Fehlercodes übersetzt die Tabelle `errors:codes`. Konventionen, neue Sprache und Prüfskript: [`src/locales/README.md`](src/locales/README.md) (Nutzung und Grenzen für Anwender: [docs/features/i18n.md](../../docs/features/i18n.md));
`npm run i18n:check` muss sauber sein (läuft auch als Vitest-Test).

## Neue Seiten anmelden (Feature-Registry)

Eine neue Seite trägt sich **nicht** in `App.tsx` ein. Sie legt `src/features/<name>/route.tsx` an; die Registry
(`src/routes/registry.ts`) sammelt die Datei per `import.meta.glob` ein und ergänzt Routing und Sidebar. Die
Konvention (Felder `path`, `label`, `group`, `roles`, `order`) steht in [`src/features/README.md`](src/features/README.md).

## Oberfläche: Tokens, Dark-Mode, Barrierefreiheit

- **Farben** kommen ausschließlich aus den Design-Tokens in `src/index.css` (`var(--c-…)`, Status-Pills über
  `.pill--<ton>`); Hell und Dunkel erreichen für Text/Hintergrund-Paare mindestens 4,5:1. Hex-Werte in `.tsx` sind
  per ESLint-Regel verboten. Ausnahme sind die Zeichenflächen (Konva): ihre Farben stehen zentral in
  `src/lib/canvasColors.ts`.
- **Theme**: `state/theme.ts` setzt `data-theme` und `color-scheme` auf `<html>` (Hell, Dunkel oder automatisch
  nach Betriebssystem).
- **Dialoge** bauen auf `components/Modal.tsx` (Dialog-Semantik, Fokusfalle, Escape, Fokus-Rückgabe);
  Bestätigungen laufen über `ConfirmDialog`, Fehler über `ErrorBanner`/`LoadState`, Status über `StatusPill`.
- **Responsive**: unter 900 px ist die Sidebar ein ausklappbarer Drawer; Tabellen scrollen in ihrem Container.
