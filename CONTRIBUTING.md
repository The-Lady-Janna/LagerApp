# Mitwirken an Lager

Danke, dass du zu Lager beitragen möchtest. Diese Seite fasst zusammen, wie du das Projekt lokal baust, testest und einen Pull Request vorbereitest.

Bitte beachte außerdem:

- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md): Verhaltenskodex für alle Beteiligten
- [SECURITY.md](SECURITY.md): Sicherheitslücken **nicht** als öffentliches Issue melden
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): Aufbau, Dependency-Regeln und Erweiterungspunkte
- [CHANGELOG.md](CHANGELOG.md): Änderungsverlauf

## Voraussetzungen

| Tool | Version | Prüfen mit |
|---|---|---|
| .NET SDK | 8.0 (`global.json` pinnt 8.0.4xx, neuere Feature-Bands sind erlaubt) | `dotnet --version` |
| Node.js | 20.19 oder neuer | `node --version` |
| npm | 10 oder neuer | `npm --version` |

Für den ersten Start reicht SQLite, es ist kein Datenbankserver nötig. Wer nur etwas ausprobieren will, startet mit Docker (`docker compose up --build`, siehe [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#schnellstart-mit-docker)); Installation, Start und erster Login (es gibt kein Standardpasswort, das Einmalpasswort steht in der Konsole) sind in [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md) beschrieben. Einen gefüllten Testbetrieb gibt es mit dem Demo-Modus (`LAGER_DEMO=1`).

## Bauen und Testen

Vom Repository-Root aus:

```powershell
# Backend bauen (strikt: Warnungen sind Fehler, wie in der CI) und alle Tests ausführen
dotnet build Lager.sln -p:LagerStrict=true
dotnet test tests/Lager.Tests

# Frontend: Abhängigkeiten, Lint, Typprüfung, Tests, Build
cd frontend/lager-ui
npm ci
npm run lint
npm run typecheck
npm test
npm run build
```

Hinweise:

- `dotnet build -p:LagerStrict=true` behandelt Warnungen (auch die NuGet-Audit-Meldungen zu verwundbaren Paketen) als Fehler, genau wie der Backend-Job der CI. Ohne den Schalter bleiben Warnungen lokal Warnungen.
- `npm run typecheck` ist `tsc -b` und prüft die Projekt-Referenzen (`tsconfig.app.json`, `tsconfig.node.json`). Ein einfaches `npx tsc --noEmit` prüft im Root-`tsconfig.json` nichts. Der Schritt ist auch Teil von `npm run build`, mit `typecheck` bekommst du die Fehler aber schneller.
- Die Backend-Tests (xUnit) starten die API im Speicher gegen eine frische SQLite-Datei im Temp-Verzeichnis (`LagerApiFactory` in `tests/Lager.Tests/Infrastructure`). Sie brauchen keine laufende Instanz und keine vorhandene Datenbank.
- Das Frontend hat automatisierte Tests mit Vitest und Testing Library (`npm test`, Tests unter `frontend/lager-ui/src/tests/`); `npm test` gehört in die Prüfliste. Neue Tests legst du in einem Ordner je Thema unter `frontend/lager-ui/src/tests/` ab.
- Einige Backend-Tests führen Frontend-Logik mit Node aus (ab Node 22.18); ältere Node-Versionen lassen sie aus.
- Neue oder geänderte Oberflächentexte brauchen Einträge in beiden Sprachdateien (`de` und `en`); `npm run i18n:check` (im Ordner `frontend/lager-ui`, läuft auch als Teil von `npm test`) findet fehlende, verwaiste und ungleiche Schlüssel.
- Die **Dokumentation wird mitgetestet** (`tests/Lager.Tests/WP18`): relative Links und Überschrift-Anker, in Backticks genannte Dateipfade, `npm`-Skripte, Konfigurationsschlüssel samt Standardwerten, Fehlercodes und jede Route samt Mindestrolle in `docs/API.md` müssen zum Code passen. Wer eine Überschrift umbenennt, einen Endpunkt oder einen Schlüssel ergänzt, passt die Doku im selben Pull Request an.
- Wer die Docker-Dateien ändert (`Dockerfile`, `docker-compose*.yml`, `.env.example`), prüft sie mit `docker compose config`; den Image-Build übernimmt der CI-Job `Docker Build-Check`.

Alle genannten Schritte sollten ohne Fehler durchlaufen, bevor du einen Pull Request öffnest.

## Branches und Commits

- Arbeite auf einem eigenen Branch, der von `main` abzweigt (bei externen Beiträgen in deinem Fork). Direkte Pushes auf `main` sind nicht vorgesehen.
- Branch-Namen mit Präfix und kurzer Beschreibung, zum Beispiel `feature/wave-picking-filter`, `fix/login-lockout`, `docs/getting-started`, `chore/deps-update`.
- Commit-Nachrichten im Stil von [Conventional Commits](https://www.conventionalcommits.org/de/v1.0.0/): `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`. Kurze Betreffzeile (möglichst höchstens 72 Zeichen) im Imperativ, bei Bedarf ein Textblock mit dem Warum.
- Ein Pull Request behandelt ein Thema. Kleine, gut reviewbare Änderungen werden schneller übernommen als große Sammel-PRs.
- Trage nennenswerte Änderungen unter `## [Unreleased]` im [CHANGELOG.md](CHANGELOG.md) ein.

## Sprache

- **Deutsch:** Kommentare, XML-Dokumentation, Log- und Fehlermeldungen des Servers und die Dokumentation.
- **Benutzeroberfläche:** zweisprachig (Deutsch und Englisch). Die Texte stehen nicht im Code, sondern in den Sprachdateien unter `frontend/lager-ui/src/locales`: ein neuer Text bekommt einen Schlüssel in `de` (Quelle) **und** in `en`; `npm run i18n:check` prüft das und läuft auch als Test. Siehe [docs/features/i18n.md](docs/features/i18n.md).
- **Bezeichner** (Klassen, Methoden, Variablen, Dateinamen) folgen der bestehenden englischen Namensgebung im Code, zum Beispiel `PickList`, `StockMovement`.
- **Issues und Pull-Request-Beschreibungen** kannst du auf Deutsch oder Englisch schreiben.
- Bestehende englische Kommentare müssen nicht in einem Rutsch übersetzt werden. Neue und geänderte Kommentare schreibst du auf Deutsch.

## Code-Stil und Architektur

- Die Editor-Grundregeln (Zeichensatz, Einrückung, Zeilenenden) stehen in der [.editorconfig](.editorconfig). Bitte einen Editor verwenden, der sie unterstützt, und keine unbeteiligten Zeilen umformatieren.
- Es gilt die Clean-Architecture-Regel **Abhängigkeiten nur nach innen** (siehe [Dependency-Regeln](docs/ARCHITECTURE.md#dependency-regeln)): `Lager.Domain` bleibt frei von Frameworks, `Lager.Application` kennt kein EF Core, technische Details gehören nach `Lager.Infrastructure`.
- Halte dich an vorhandene Muster (Repositories, Unit of Work, DTOs in `Lager.Contracts`). Eine Schritt-für-Schritt-Anleitung für neue Entitäten findest du unter [Eine neue Domain-Entität end-to-end bauen](docs/ARCHITECTURE.md#eine-neue-domain-entität-end-to-end-bauen).
- Für Fehlerbehebungen und neues Verhalten gehört ein Test dazu. Reine Logik (Algorithmen, Domain) testest du als Unit-Test ohne Host, API-Verhalten als Integrationstest mit `LagerApiFactory`.

## Datenbank-Änderungen (SchemaUpgrader)

Wenn du eine neue Entität, Tabelle oder Spalte einführst, müssen auch bereits bestehende Datenbanken aktualisiert werden. Neue, leere Datenbanken legt EF Core beim Start aus dem Modell an, bestehende Datenbanken bekommen die Änderung nur über den **SchemaUpgrader**:

- Die Schema-Evolution läuft über den [SchemaUpgrader](src/Lager.Infrastructure/Persistence/SchemaUpgrader.cs). EF-Core-Migrationen (`dotnet ef migrations add`) werden nicht verwendet, im Repository gibt es keine. Bitte keine Migration anlegen.
- Ergänze den Schritt für jede neue Tabelle bzw. Spalte dort (bzw. in den Schritt-Dateien, sofern der SchemaUpgrader sie einbindet). Er muss **idempotent** sein (mehrfaches Ausführen ändert nichts) und für SQLite **und** MySQL funktionieren.
- Neue Entity-Tabellen brauchen zusätzlich die Concurrency-Token-Spalte, die der SchemaUpgrader für alle Entity-Tabellen sicherstellt.
- Die genaue Vorgehensweise steht in der Architektur-Doku: [Schema-Evolution](docs/ARCHITECTURE.md#schema-evolution).

## Pull Requests

1. Fork bzw. Branch anlegen, Änderung umsetzen, lokal bauen und testen (siehe oben).
2. Pull Request gegen `main` öffnen und die Checkliste aus der PR-Vorlage abarbeiten.
3. Rückmeldungen aus dem Review einarbeiten. Neue Commits sind dabei in Ordnung, vor dem Merge kann zusammengefasst werden (Squash).

Bitte checke keine Zugangsdaten, Schlüssel, Produktiv-Konfigurationen (`appsettings.Production.json`, `.env`), Datenbankdateien (`*.db`, `*.sqlite`) oder Logdateien ein. Sie sind in der `.gitignore` ausgeschlossen, prüfe vor dem Commit trotzdem mit `git status`.

Mit einem Pull Request erklärst du dich einverstanden, dass dein Beitrag unter der Lizenz dieses Repositories (siehe [LICENSE](LICENSE)) veröffentlicht wird.

## Fehler und Ideen melden

Nutze die Vorlagen unter **Issues** (Fehlerbericht, Feature-Wunsch). Bei Fehlern helfen Version bzw. Commit, Schritte zur Reproduktion und die Angabe von Datenbank-Provider und Browser. Bitte keine Passwörter, Tokens oder echten Kundendaten in Issues einfügen.
