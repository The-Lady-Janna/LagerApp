# Sprachdateien (`src/locales`)

Die Oberfläche ist zweisprachig: **Deutsch (`de`) ist die Standardsprache und die Quelle aller Texte**, Englisch (`en`) die vollständige
Übersetzung. Die Einrichtung steht in [`src/i18n/index.ts`](../i18n/index.ts) (i18next + react-i18next).

```
src/locales/
  de/<namespace>.json     Quelle: die deutschen Texte (wortgleich zum Stand vor der Internationalisierung)
  en/<namespace>.json     Übersetzung, dieselben Schlüssel
```

Ein Namespace je Bereich: `common` (Schaltflächen, Laden/Leer, Scanner), `nav` (Seitenleiste, Suche, Sprache, Farbschema), `auth` (Login,
Passwort), `errors` (Fehlertexte, Fehlercodes des Servers, Fehlerseiten), `status` (Anzeigenamen der Statuswerte), `articles`, `orders`,
`stock`, `inbound`, `inventory`, `returns`, `purchasing` (Bestellungen an Lieferanten, Lieferanten, Nachschub), `customers`, `picking`
(Picklisten, Packen, Mobile-Picker, Wagen, Wellen), `shipping`, `reports` (samt Audit), `warehouse` (Lagerstruktur, Layout-Editor),
`system`, `labels`, `importexport`, `traceability`, `users`. Alle `*.json` werden per `import.meta.glob` eingesammelt: **ein neuer
Namespace braucht nur zwei Dateien (de und en), keinen Code.**

## Sprache wählen und merken

Reihenfolge: gespeicherte Wahl (`localStorage` `lager.lang`) → Browsersprache (`en-US` zählt als `en`) → Deutsch. Gespeichert wird nur eine
bewusste Wahl über den Umschalter DE/EN (Seitenleiste, Anmeldeseite, `setLanguage()`), nicht die erkannte Browsersprache. `<html lang>` folgt
der Sprache. Datum, Zahl und Betrag laufen über `src/lib/format.ts` (Intl mit der aktiven Sprache: `de-DE`, englisch `en-GB`).

## Schlüsselkonventionen

* **Immer mit Namespace schreiben:** `t('orders:cancel.button')`, in JSX `<Trans i18nKey="orders:cart.none" … />`. `useTranslation()` wird ohne
  Argument benutzt. Außerhalb von React (Stores, reine Logik, Fehlertexte): `import { t } from '../i18n'` — die Sprache wird bei jedem Aufruf
  neu gelesen.
* Schlüssel in `camelCase`, Gruppen mit Punkt: `title`, `col.<spalte>`, `empty.<fall>`, `confirm.title|label|body`, `failed`, `…Aria` für
  `aria-label`. Schlüssel beschreiben die Stelle, nicht den Text.
* **Platzhalter** `{{name}}`, **Plural** über `_one`/`_other` und `t(key, { count })` (keine Stringkonkatenation, kein `n === 1 ? … : …`).
  Ein `count`-Wert muss eine Zahl sein (formatierte Zahlen als eigener Platzhalter übergeben).
* **Text mit Auszeichnung** (`<strong>`, `<code>`, Link): ein Schlüssel mit Tags im Text und `<Trans components={{ strong: <strong /> }} />`.
  Keine HTML-Void-Namen als Tag (`link`, `br`, `img` …); Links heißen `<a>`.
* **Dynamische Schlüssel** nur als Template-Literal mit festem Präfix: `` t(`status:order.${status}`, { defaultValue: status }) `` — so sieht
  das Prüfskript alle Schlüssel mit diesem Präfix als benutzt, und ein neuer, unbekannter Status erscheint unverändert statt als Rohschlüssel.
* Deutsche Texte **nicht umformulieren**: die `de`-Dateien sind die Quelle und bleiben wortgleich; Übersetzungen entstehen daneben.

## Was übersetzt wird und was nicht

* **Statuswerte:** `status:pill.<bereich>.<status>` (StatusPill) und `status:<art>.<status>` (Text in Tabellen). Deutsch zeigt die Pills wie bisher
  mit dem Serverwert („Received“, „Shipped“ …); wer sie eindeutschen will, ändert nur die Werte in `de/status.json` (und die Tests, die
  den Serverwert erwarten).
* **Fehlercodes des Servers:** Tabelle `errors:codes.<code>` (snake_case). Deutsch hat der (deutsche) Text des Servers Vorrang, die Tabelle ist
  Fallback; in anderen Sprachen ersetzt ein bekannter Code die deutsche Serverzeile. Unbekannte Codes zeigen weiter das deutsche `detail`.
  **Backend-Meldungen selbst bleiben deutsch** (Entscheidung des Eigentümers); die Reports-Kennzahlen (`lib/kpiText.ts`) und Fehlercodes sind die
  Ausnahmen, weil die UI sie abfängt.
* **Navigation:** `nav:routes.<pfad ohne „/“>` (Schrägstriche als „-“: `/cart-configs` → `cart-configs`) und `nav:groups.<id>`. Ein neues
  Feature (`src/features/<name>/route.tsx`) trägt sein deutsches `label` wie bisher ein und ergänzt die Übersetzung hier; ohne Eintrag
  erscheint das `label`.
* Nicht übersetzt: Markenname „Lager“, Rollennamen (Admin, Manager, …), Einheiten (mm, kg), Entwicklerhinweise in der Konsole.

## Prüfen: `npm run i18n:check`

[`scripts/i18n-check.mjs`](../../scripts/i18n-check.mjs) prüft (Exit-Code 1 bei Abweichungen; dieselbe Logik läuft im Vitest-Test
`src/tests/WP29/i18nCheck.test.ts`):

1. **Parität:** jede Sprache hat dieselben Namespaces und Schlüssel wie `de`;
2. **Platzhalter** (`{{…}}`) je Schlüssel in `de` und `en` gleich; keine leeren Texte;
3. **fehlende Schlüssel:** im Code als `'namespace:schluessel'` benutzt, in `de` nicht definiert (die UI würde den Rohschlüssel zeigen);
4. **ungenutzte Schlüssel:** in `de` definiert, im Code nirgends benutzt.

Der Code wird als Text durchsucht (kein Parser): jedes String-Literal `'namespace:pfad.zum.key'` mit bekanntem Namespace zählt als Benutzung
(`t(…)`, `i18nKey="…"`, Tabellen wie `labelKey: '…'`), ein Template-Literal `` `namespace:prefix.${x}` `` als Benutzung aller Schlüssel mit
diesem Präfix. Ein Test (`src/tests/WP29/englishUi.test.tsx`) rendert die Hauptseiten in `en` und stellt sicher, dass kein deutsches Stichwort
übrig bleibt.

## Neue Sprache hinzufügen

1. `src/locales/<code>/` anlegen und alle Namespace-Dateien von `de/` dorthin kopieren und übersetzen (gleiche Schlüssel und Platzhalter).
2. In `src/i18n/index.ts` den Code in `SUPPORTED_LANGUAGES` eintragen und in `intlLocale()` die Intl-Locale ergänzen.
3. Den Namen der Sprache in `nav.json` unter `language.names.<code>` (in allen Sprachen) ergänzen.
4. `npm run i18n:check` und `npm test` ausführen.

## Tests

Die Testumgebung meldet sich als deutscher Browser (`src/tests/helpers/germanBrowser.ts`), damit die Oberfläche in den bestehenden Tests
deutsch bleibt; `src/tests/setup.ts` stellt die Sprache nach jedem Test auf Deutsch zurück. Englisch in einem Test: `await act(() => setLanguage('en'))`.
