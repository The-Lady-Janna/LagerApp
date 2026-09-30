# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden in dieser Datei festgehalten.

Das Format basiert auf [Keep a Changelog 1.1.0](https://keepachangelog.com/de/1.1.0/), die Versionierung folgt [Semantic Versioning](https://semver.org/lang/de/). Solange das Projekt vor 1.0 ist, können sich Schnittstellen und Datenbankschema noch ändern; Hinweise dazu stehen unter **Geändert** bzw. **Entfernt**.

## [Unreleased]

Noch keine Änderungen seit 0.1.0. Neue Änderungen kommen hier hinein (siehe [CONTRIBUTING.md](CONTRIBUTING.md)).

## [0.1.0] - JJJJ-MM-TT

<!-- Datum beim Veröffentlichen eintragen (JJJJ-MM-TT) und die Version im Repository taggen; dieser Abschnitt ist bis dahin der Stand der ersten öffentlichen Version. -->

Erste öffentliche Version. Vorher war das Projekt ein interner Entwicklungsstand; alles Folgende beschreibt, was 0.1.0 enthält, mit Schwerpunkt auf dem, was sich gegenüber dem früheren Entwicklungsstand geändert hat.

### Sicherheit

- **Die API ist standardmäßig geschlossen:** jeder Endpunkt außer `POST /api/auth/login` und den Health-Endpunkten verlangt ein gültiges Token (`FallbackPolicy` plus `RequireAuthorization()` an jedem Controller). Auch unbekannte Pfade antworten ohne Token mit 401.
- **Rollenmatrix an allen Endpunkten:** Admin, Manager, Receiver, Picker, Packer, Viewer (nur lesen). Eine Endpunkt-Matrix (`tests/Lager.Tests/WP02/EndpointMatrix.cs`) prüft jede echte Route gegen die erwartete Mindestrolle.
- **Kein Standardpasswort mehr:** der Bootstrap-Admin entsteht mit einem Zufalls-**Einmalpasswort**, das einmalig auf der Konsole (Docker: im Container-Log) steht, oder mit `Auth:BootstrapAdminPassword`. Der Passwortwechsel beim ersten Login wird **serverseitig** erzwungen (403 `password_change_required`). Passwortregeln: mindestens 10 Zeichen, höchstens 72 Bytes, nicht gleich dem Benutzernamen.
- **JWT-Schlüssel ist Pflicht** (`Jwt:SigningKey` oder `Jwt:KeyFile`, mindestens 32 Bytes); der frühere Beispiel-Schlüssel wird abgelehnt, nur `Development` darf mit einem flüchtigen Schlüssel starten.
- **Token-Widerruf:** ein Token wird sofort ungültig, wenn das Konto deaktiviert oder gelöscht, das Passwort geändert oder zurückgesetzt wird oder sich die Rollen ändern.
- **Login-Härtung:** gleiche Antwort und Rechenzeit für jeden Fehlschlag, Kontosperre nach Fehlversuchen (Standard 5, 15 Minuten), Rate-Limit je Client-IP, Sicherheits-Log (`SecurityAudit`).
- **HTTPS opt-in** (`Security:RequireHttps`, HSTS und Weiterleitung), Weitergabe-Header für Reverse-Proxys (`Security:ForwardedHeaders:*`), Host-Filter (`AllowedHosts`), Sicherheits-Header auf jeder Antwort, CORS mit konkreter Origin-Liste.
- **Restore ist in Produktion gesperrt**, solange `Backup:AllowRestore` nicht gesetzt ist; Reseed und Bulk-Seed gibt es nur in `Development`; Swagger läuft nur in `Development` oder mit ausdrücklichem `Swagger:Enabled=true` (die Beschreibung ist dann ohne Anmeldung lesbar, die Endpunkte bleiben geschützt).

### Hinzugefügt

- Lizenz: MIT, siehe [LICENSE](LICENSE); QuestPDF bleibt unter seiner eigenen Community-Lizenz (siehe README, Abschnitt Drittlizenzen).
**Betrieb und Docker**

- **Docker-Quickstart:** `Dockerfile` (Oberfläche und API in einem Container), `docker-compose.yml` mit Volume `lager-data` für Datenbank, JWT-Schlüssel, Logs und Backups, `docker-compose.mysql.yml` (MySQL statt SQLite), Profil `https` mit Caddy (`deploy/Caddyfile`), `.env.example`, `.dockerignore` und ein CI-Build-Check (`.github/workflows/docker.yml`). `docker compose up --build` liefert die Anwendung unter `http://localhost:8080`. Die `docker-compose.yml` reicht unter anderem `Demo__Enabled`, `Backup__Schedule` und `Backup__RetentionCount` aus der `.env` durch. Anleitung: [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#schnellstart-mit-docker).
- **Health-Endpunkte** `GET /health/live` und `GET /health/ready` (anonym, nur der Status; 503 bei nicht erreichbarer Datenbank) und **Auslieferung des Frontends durch das Backend**, sobald `wwwroot/index.html` vorhanden ist (SPA-Fallback, Cache-Regeln).
- **Logging** mit Serilog: Konsole und Tagesdatei, Verzeichnis, Aufbewahrung und Level per Konfiguration (`Logging:Directory`, `Logging:RetainedFileCount`, Abschnitt `Serilog`), Korrelations-ID je Anfrage auch in Fehlerantworten; die Health-Probes loggen nur auf `Debug`.
- **Demo-Modus** (`Demo:Enabled` bzw. `LAGER_DEMO=1`, standardmäßig aus): ein erfundener, realistischer Betrieb mit über 40 Artikeln, Chargen mit MHD, Lieferanten, Kunden, rund 60 Tagen Historie und Demo-Benutzern je Rolle, nur in eine leere Datenbank und mit zufälligen Passwörtern, die einmalig in der Konsole stehen. In `Production` nur mit `Demo:AllowInProduction`.
- **Backup-Oberfläche** unter **System > Backup & Restore** (Admin, SQLite): Liste, Download, Löschen, zeitgesteuerte Backups (`Backup:Schedule`, UTC), Aufbewahrung (`Backup:RetentionCount`), Restore mit Bestätigung (`RESTORE`) und Sicherheitskopie vor jedem Restore; Einstellungen unter `Backup:*`.
- **CI:** GitHub Actions (Backend strikt mit Warnungen als Fehler und NuGet-Audit, Frontend auf Node 20.19 und 22 mit Lint, Typprüfung, Tests, Build und `npm audit`), CodeQL (wöchentlich), Dependabot.
- **Mehrsprachige Oberfläche (Deutsch und Englisch):** react-i18next, Deutsch als Standardsprache und Quelle aller Texte, Englisch vollständig übersetzt (22 Namespaces unter `frontend/lager-ui/src/locales`). Umschalter DE/EN in der Seitenleiste und auf der Anmeldeseite, die Wahl liegt pro Browser in `localStorage` (`lager.lang`), ohne Wahl gilt die Browsersprache; Datum, Zahl und Betrag folgen der Sprache. Häufige Fehlercodes des Servers erscheinen übersetzt, **Meldungen des Servers und die Doku bleiben deutsch**. Das Prüfskript `npm run i18n:check` (auch als Test) sichert Parität, Platzhalter sowie fehlende und ungenutzte Schlüssel. Anleitung und Grenzen: [docs/features/i18n.md](docs/features/i18n.md).
- **Swagger/OpenAPI:** Swagger UI (`/swagger`) und OpenAPI-Beschreibung laufen in `Development` oder mit dem neuen Schlüssel `Swagger:Enabled=true`; `Swagger:Enabled=false` schaltet sie auch in `Development` ab. Alle Operationen haben eine Zusammenfassung, die Mindestrolle sowie Erfolgs- und Fehlerantworten (`application/problem+json`, `Location` an jedem 201), die XML-Kommentare der Controller und der DTOs (`Lager.Contracts` erzeugt jetzt ebenfalls eine XML-Dokumentation) kommen ins Dokument; Beispielanfragen stehen in `src/Lager.Api/Lager.Api.http`. Dazu `GET /api/version` (App-Version und Schemastand). Referenz: [docs/features/openapi.md](docs/features/openapi.md).
- **Repository-Basisdateien:** `.gitattributes`, `.editorconfig`, `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `CHANGELOG.md`, Issue- und Pull-Request-Vorlagen.

**Stammdaten**

- **Lagerstruktur:** Lager, Zonen, Gänge, Regale und Lagerplätze lassen sich per API und in der Oberfläche (Seite **Lagerstruktur**, Manager) anlegen, ändern und löschen, samt **Bin-Typ** (Standard, HotPick, Reserve) und Nachschub-Schwelle. Codes sind eindeutig (409 `duplicate_code`), Löschen prüft Bestand, offene Aufgaben und Belege (409 `warehouse_not_empty`, `in_use`).
- **Artikel:** GTIN/EAN mit Prüfziffer und Eindeutigkeit (auch UPC-A/EAN-13-Äquivalenz), Alternativ-SKUs, Saison-Fenster und Bundle-Komponenten (mit Zyklus- und Tiefenprüfung, 409 `bundle_cycle`, `bundle_too_deep`) im Artikel-Editor; Suche und Statusfilter in der Artikelliste (`GET /api/articles?search=`); Auflösung eines gescannten Codes über `GET /api/articles/by-code/{code}` (GTIN, SKU, Alternativ-SKU als Rückfall; 404 und 409 `ambiguous_code`).
- **CSV-Import und -Export** (Seite **Import & Export**, Manager): Export von Artikeln, Bestand, Bestellungen, Bewegungen und Audit; Import von Artikeln, Bestand und Bestellungen mit **Trockenlauf**, Zeilenfehlern und Übernahme in einer Transaktion, geschrieben über die vorhandenen Dienste (Bestand nur über das Ledger); ein Sammel-Eintrag im Audit-Trail je Import; Beispieldateien unter `docs/samples/`.

**Bestand, Chargen und Bestellungen**

- **Chargen und MHD in der Oberfläche:** Charge und MHD im Wareneingang und bei der Bestandsbuchung, Spalte "Nächstes MHD" mit Unterzeilen je Charge, Karte **Ablaufende Chargen** (`GET /api/reports/expiring`, Frist 7/30/90 Tage) und die Seite **Chargen-Trace** (`GET /api/reports/charge/{lotNumber}`, erweitert um Wareneingangs-Status und möglicherweise betroffene Bestellungen).
- **Wareneingang aus einer Einkaufsbestellung** in der Oberfläche (**Wareneingang anlegen** in der Beschaffung; legt einen Entwurf an, bucht noch keinen Bestand).
- **Etiketten** (Seite **Etiketten**, Picker): Lagerplatz-, Artikel- und Bestelletiketten mit Code 128 im Browser drucken (Einzeletikett 50 × 30 mm oder A4-Bogen 3 × 8), ZPL-Download für Zebra-Drucker (`?copies=n`, UTF-8), echter Code-128-Barcode der Bestellnummer im Lieferschein-PDF.
- **StockMovement-Ledger** (nur anhängend, vorzeichenbehaftetes Delta, Kosten-Snapshot, Grund, Vorgangsverweis, Charge/MHD) mit einem einheitlichen Buchungsweg (`StockBooking`) für Wareneingang, Pick, Retoure, Inventur, Nachschub, Korrektur und CSV-Import; lot-genauer Bestand je Artikel, Lagerplatz und Charge, FEFO beim Kommissionieren (abgelaufene Chargen werden nicht gepickt), FIFO-Bestandsbewertung, Bestandsverlauf, Dead-Stock aus dem Ledger.
- **Bestell-Lebenszyklus:** New → Picking → Picked → Packed → Shipped, Storno aus New, Picking und Picked, Priorität und Fälligkeit, Kunde und Lieferadresse, Saison-Prüfung, externe Bestell-API (`POST /api/orders`) mit `Idempotency-Key`/`externalReference`.
- **Kommissionierung und Packen:** Bestandsallokation über alle Positionen (alles oder nichts), Packen bucht genau einmal, Ist-Mengen, Kurz-Pick, Wagen-Pickliste und Wellen, Pickroute um Wände, Packvorschlag (Karton-Heuristik), Mobile-Picker mit Barcode-Scan.
- **Weitere Abläufe:** Inventur (Snapshot, Zählung, Abgleich gegen den aktuellen Bestand), Retouren mit Qualitätsprüfung, Nachschub (HotPick aus Reserve), Einkauf mit Bestellvorschlägen, Versand mit manuellen Tracking-Nummern (DHL und UPS als "nicht verfügbar", keine Fake-Nummern).
- **Optimistische Sperre** für Bestand und alle Belege mit Statuswechsel (409 `concurrency_conflict`).
- **Datenbank:** Schema-Evolution über den `SchemaUpgrader` mit Schritt-Dateien und Versionstabelle `__LagerSchemaVersion` (keine EF-Migrationen) für SQLite und MySQL; atomare Nummernkreise; Fremdschlüssel mit `Restrict` (Löschen verwendeter Datensätze scheitert mit 409); SKU, Bestellnummer und Benutzername ohne Beachtung der Schreibweise eindeutig; SQLite im WAL-Modus.

**Oberfläche und Dokumentation**

- **Feature-Pakete im Frontend:** neue Seiten melden sich über `src/features/<name>/route.tsx` an (Route-Registry), ohne `App.tsx` zu ändern.
- **Frontend-Grundlagen:** Vitest-Tests, zentrale Fehleranzeige (Toasts, Banner), Bestätigungsdialoge und Doppelklick-Schutz, Session-Ablauf und Passwortwechsel-Dialog, Service-Worker mit Update-Hinweis, Rollen-Guards, Dark Mode, globale Suche.
- **Dokumentation:** README (deutsch und englisch), Getting Started (mit Docker und Demo-Modus), Benutzer-Handbuch, Konfiguration, API, Datenmodell, Architektur, Fehlersuche, Referenzen je Bereich unter `docs/features/`, CSV-Beispieldateien und eine Anleitung für Screenshots (`docs/screenshots/README.md`). Die Doku wird durch Tests gegen den Code geprüft.

### Geändert

- **Fehlerformat:** alle Fehler kommen als `application/problem+json` mit `type`, `title`, `status`, `detail`, `code`, `correlationId` und (bei Validierung) `errors`; das Kompatibilitätsfeld `error` bleibt erhalten. Neue Codes der Verwaltung: `development_only`, `restore_disabled`, `sqlite_only`, `database_not_file_based`, `invalid_backup_name`, `confirmation_required`, `invalid_backup_file`, `database_in_use`, `backup_in_use` u. a. (siehe [docs/API.md](docs/API.md#fehlercodes)). Ausnahmen bleiben die kurzen `{ code, error }`-Bodies von 429 und `password_change_required`.
- **Statuscodes:** Regelverstöße bei Einkaufsbestellungen, Retouren und Sendungen antworten jetzt mit **409** statt 400 (400 bleibt für ungültige Eingaben).
- **Zeitstempel sind UTC** und tragen in der API ein `Z`; Kalendertage laufen als UTC-Mitternacht.
- **Neue Konfigurationsschlüssel** (alle in [docs/CONFIGURATION.md](docs/CONFIGURATION.md)): `Jwt:SigningKey`, `Jwt:KeyFile`, `Auth:*`, `Security:*`, `Logging:Directory`, `Logging:RetainedFileCount`, `Serilog:*`, `Backup:Directory`, `Backup:Schedule`, `Backup:RetentionCount`, `Backup:AllowRestore`, `Demo:Enabled`, `Demo:AllowInProduction`, `Swagger:Enabled`. `Database:Seed` ist **veraltet** (legt nur einen kleinen Datensatz an und warnt); der Standard ist jetzt `false`, die Anwendung legt ohne ausdrückliche Einstellung keine Demo-Daten an.
- `POST /api/inbound` nimmt optional `lines` entgegen und legt Kopf und Positionen atomar an; `POST /api/admin/restore` nimmt auch den Namen eines vorhandenen Backups (`backupName`) und eine Bestätigung (`confirm`) entgegen; `POST /api/admin/backup` bleibt neben dem neuen `POST /api/admin/backups`.
- Der Knopf "Empfangen" der Einkaufsbestellung (er schrieb nur die empfangene Menge fort, ohne Bestand zu buchen, hieß im Dialog aber "Wareneingang buchen") ist durch **Wareneingang anlegen** ersetzt: die Oberfläche legt einen Wareneingang im Entwurf an, den Bestand bucht erst dieser.
- Reseed- und Bulk-Seed-Knöpfe erscheinen nur im Entwicklungsbuild.
- `.gitignore` um Secrets und lokale Konfiguration (`appsettings.Production.json`, `appsettings.*.local.json`, `.env*`, Schlüssel und Zertifikate), weitere SQLite-Endungen, Test-/Coverage- und Publish-Artefakte sowie lokale Tool-Ordner erweitert.

### Behoben

- `POST /api/warehouse/shelves/{id}/bins` (weiteren Lagerplatz in einem Regal anlegen) antwortete bei jedem Regal mit 409 `concurrency_conflict`, weil das neue Fach am bereits geladenen Regal als bestehend erkannt wurde. Er legt den Lagerplatz jetzt an (Regressionstest: `tests/Lager.Tests/WP34/AddBinToShelfTests.cs`).
- Wareneingang: die Erfassungsmaske legte eine leere Lieferung an, weil die Positionen nicht gesendet wurden.
- Die Oberfläche nannte "mindestens 8 Zeichen" für Passwörter, der Server verlangt 10.

### Entfernt

- Das frühere öffentliche Standardpasswort des Bootstrap-Admins und der öffentliche Beispiel-Signing-Key. Wer eine ältere Datenbank weiter nutzt, muss das Passwort des Admin-Kontos **sofort** ändern.

### Bekannte Einschränkungen

Die vollständige Liste steht unter [Bekannte Einschränkungen](docs/USAGE.md#bekannte-einschränkungen), die Roadmap in [TODO.md](TODO.md). Die wichtigsten Punkte:

- Keine Carrier-Anbindung (DHL, UPS, GLS, DPD), keine Anbindung an ERP/Shop-Systeme, keine Bestandsreservierung, keine Verpackungseinheiten und Artikelvarianten, kein Paging der Listen. Die Oberfläche ist zweisprachig, aber Meldungen des Servers und die Doku bleiben deutsch, die farbigen Statusfelder zeigen teils den englischen Serverwert (siehe [docs/features/i18n.md](docs/features/i18n.md#grenzen)).
- Backup und Restore der Anwendung gibt es nur für SQLite (bei MySQL: `mysqldump`); MySQL ist weniger erprobt als SQLite (keine Tests gegen einen echten Server).
- Das Docker-Image wurde bisher **nie praktisch gebaut und gestartet** (geprüft sind nur `docker compose config` und `dotnet publish`); der Container-Build sollte vor dem ersten Release einmal praktisch getestet werden.
- PWA-Manifest und Icons nur als SVG (PNG 192/512 und `apple-touch-icon` fehlen); einige Bestätigungen nutzen noch `window.confirm` statt des Bestätigungsdialogs; die gezählten Mengen des Mobile-Pickers werden nicht auf dem Server gespeichert; Warnungen der Pickroute zeigt die Oberfläche nicht.
