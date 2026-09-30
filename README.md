# Lager

[![CI](https://github.com/The-Lady-Janna/LagerApp/actions/workflows/ci.yml/badge.svg)](https://github.com/The-Lady-Janna/LagerApp/actions/workflows/ci.yml)

Self-hosted Warehouse-Management-System für kleine bis mittlere Lager: Wareneingang, Bestand mit Chargen und MHD, Bestellungen, Kommissionierung mit wandbewusster Pickroute, Packen, Versand, Retouren, Inventur, CSV-Import/-Export, Etiketten und Auswertungen. ASP.NET-Core-Backend, React-Weboberfläche (Deutsch und Englisch) mit Layout-Editor und Mobile-Picker mit Barcode-Scan. Docker-Quickstart vorhanden (Dockerfile, Compose, MySQL-Variante, optional Caddy für HTTPS); der Container-Build wurde noch nicht praktisch getestet (siehe Projektstatus).

**English:** [README.en.md](README.en.md) (Zusammenfassung; die Oberfläche gibt es auf Deutsch und Englisch, Code, Kommentare, Meldungen des Servers und die Dokumentation unter `docs/` bleiben deutsch.)

**Stack:** ASP.NET Core 8 (Clean Architecture) · EF Core 8 · SQLite / MySQL · React 19 · Vite · Konva · TypeScript · TanStack Query · Zustand · react-i18next.

> **Projektstatus:** Vor Version 1.0 (erste Version: siehe [CHANGELOG.md](CHANGELOG.md)). Schnittstellen und Datenbankschema können sich noch ändern (Datenbanken werden beim Start automatisch aktualisiert). Es gibt keine Mandantenfähigkeit und keine Seiten-Aufteilung (Paging) der Listen; die Oberfläche ist zweisprachig (Deutsch, Englisch), Meldungen des Servers und die Doku sind deutsch. Carrier-Anbindungen (DHL, UPS) und Anbindungen an ERP/Shop-Systeme sind **nicht** umgesetzt. Was fertig, teilweise oder nur per API verfügbar ist, steht unten in der Funktionsliste, die offenen Punkte in [TODO.md](TODO.md).
>
> **Bekannte Grenze (Docker):** das Docker-Image wurde bisher **nie praktisch gebaut und gestartet** (geprüft sind nur `docker compose config` und `dotnet publish`). Der Container-Build sollte vor dem ersten Release einmal auf einem Rechner mit laufendem Docker durchgespielt werden (Login, Deep-Link, PDF-Lieferschein, Daten nach `down` und `up`, MySQL-Variante, Caddy-Profil), siehe [TODO.md](TODO.md).

---

## Doku

| Datei | Wofür |
|---|---|
| **[docs/GETTING_STARTED.md](docs/GETTING_STARTED.md)** | Installation, Docker, erster Start, Demo-Modus, MySQL, Produktivbetrieb, Backup |
| **[docs/USAGE.md](docs/USAGE.md)** | Benutzer-Handbuch: alle Workflows (Lagerstruktur, Artikel, Wareneingang, Chargen/MHD, Picken, Packen, Versand, Inventur, CSV, Etiketten, Backup ...) und bekannte Einschränkungen |
| **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)** | Alle Konfigurationsschlüssel und Umgebungsvariablen (auch Docker, Backup, Demo) |
| **[docs/API.md](docs/API.md)** | API-Überblick: Anmeldung, Rollenmatrix, Fehlerformat, Endpunkte |
| **[docs/DATA_MODEL.md](docs/DATA_MODEL.md)** | Datenmodell mit ER-Diagramm, Ledger, Status-Automaten |
| **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** | Entwickler-Doku: Schichten, Auth, Fehlerbehandlung, Schema-Evolution, Route-Registry, neue Features bauen |
| **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** | Fehlersuche: Passwort verloren, Konto gesperrt, Docker, HTTPS, CORS, Restore, CSV-Import ... |
| **[TODO.md](TODO.md)** | Roadmap: was steht, was bewusst verschoben ist |
| **[CHANGELOG.md](CHANGELOG.md)** | Änderungsverlauf |

Je Bereich gibt es eine ausführliche Referenz unter `docs/features/`: [Lagerstruktur](docs/features/lager-stammdaten.md), [Artikel und GTIN](docs/features/artikel-gtin.md), [Chargen und MHD](docs/features/chargen-mhd.md), [CSV-Import und -Export](docs/features/csv-import-export.md), [Backup und Restore](docs/features/backup-restore.md), [Etiketten](docs/features/etiketten.md), [Demo-Modus](docs/features/demo-modus.md), [Mehrsprachigkeit](docs/features/i18n.md) und [Swagger und OpenAPI](docs/features/openapi.md). Beispieldateien zum Importieren liegen unter `docs/samples/`.

---

## Voraussetzungen

- **Docker** mit Docker Compose v2, wenn die Anwendung nur laufen soll (nichts weiter nötig).
- **.NET SDK 8.0.419** oder neuer aus der 8.0-Reihe (`global.json`) und **Node.js** `^20.19.0` oder `>=22.13.0` mit npm 10 oder neuer, wenn daran entwickelt wird.
- SQLite ist eingebaut; für MySQL ein MySQL-8-/MariaDB-Server (optional).

---

## Quick-Start

### Mit Docker (Quickstart)

```bash
git clone https://github.com/The-Lady-Janna/LagerApp.git lager
cd lager
docker compose up --build
# → http://localhost:8080 (Oberfläche und API aus demselben Container)

# Einmalpasswort des Admins (steht nur beim allerersten Start im Log):
docker compose logs lager
```

Benutzer `admin`, dazu das Einmalpasswort aus dem Log; die App verlangt sofort ein neues Passwort. **Ein Standardpasswort gibt es nicht.**

- **Daten** (SQLite-Datenbank, JWT-Schlüssel, Logs, Backups) liegen im Volume `lager-data` (`/data` im Container). `docker compose down` behält sie, `docker compose down -v` **löscht sie**.
- **Backup:** in der Oberfläche unter **System > Backup & Restore** (nur Admin): Backup jetzt erstellen, herunterladen, Liste, Restore. Zeitplan und Aufbewahrung stellen `Backup__Schedule` und `Backup__RetentionCount` ein (in der `.env` setzen, die `docker-compose.yml` reicht beide durch). Backups liegen im Volume unter `/data/backups`, sind unverschlüsselt und gehören zusätzlich auf ein anderes Medium.
- **Einstellungen** stehen in einer `.env` neben der `docker-compose.yml` (Vorlage: `.env.example`, alles optional). Wichtig für den Zugriff im LAN oder unter einer Domain: `AllowedHosts=lager.example.com`, sonst antwortet die API mit 400.
- **MySQL statt SQLite:** `docker compose -f docker-compose.yml -f docker-compose.mysql.yml up --build` (Pflicht in `.env`: `LAGER_DB_PASSWORD` und `LAGER_DB_ROOT_PASSWORD`).
- **HTTPS über Caddy:** `docker compose --profile https up --build` mit `LAGER_DOMAIN` in `.env` (Beispiel: `deploy/Caddyfile`). Kamera-Scan am Handy und der Service-Worker brauchen HTTPS.
- Der Container meldet sich über `GET /health/ready` (Datenbank erreichbar) als gesund.

Einzelheiten, Varianten und Fehlersuche: **[docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#schnellstart-mit-docker)**.

### Demo-Modus: mit realistischen Beispieldaten ausprobieren

Der Demo-Modus legt in eine **leere** Datenbank einen erfundenen, aber realistischen Betrieb an: Lager mit Lagerplätzen und Wänden, über 40 Artikel mit Preisen, Chargen mit MHD, Lieferanten, Kunden, rund 60 Tage Historie (Bestellungen in allen Status, Picklisten, Sendungen, Einkauf, Retouren, Inventuren) und Demo-Benutzer je Rolle. Bestandsalarme, Lagerwert, Heatmap, ABC, Dead-Stock, Picker-Performance und Chargen-Rückverfolgung zeigen sofort Daten. Er ist **standardmäßig aus**.

```powershell
# lokal: Demo-Modus per Umgebungsvariable (gleich beim ersten Start, die Datenbank muss leer sein)
$env:LAGER_DEMO = "1"
dotnet run --project src/Lager.Api
```

Die **Zugangsdaten** (Admin: Einmalpasswort des ersten Starts; `manager`, `picker`, `packer`, `receiver`, `viewer` ...: zufällige Passwörter) stehen **einmalig in der Konsole** bzw. im Container-Log (`docker compose logs lager`), nie in der Logdatei und nie im Repository. Docker läuft als `Production` und braucht deshalb `Demo__Enabled=true` **und** `Demo__AllowInProduction=true` (beide Zeilen in die `.env`, die `docker-compose.yml` reicht sie durch, siehe [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden)). Der Datensatz ist in [docs/features/demo-modus.md](docs/features/demo-modus.md) beschrieben. Demo-Daten gehören nicht in ein Produktivsystem.

### Lokal entwickeln

```powershell
# Backend (Terminal 1)
dotnet run --project src/Lager.Api
# → http://localhost:5099 (Umgebung Development), Swagger unter /swagger

# Frontend (Terminal 2)
cd frontend/lager-ui
npm ci
npm run dev
# → http://localhost:5173
```

**Erster Login:** Benutzer `admin`. **Ein Standardpasswort gibt es nicht:** beim allerersten Start erzeugt das Backend ein Einmalpasswort und gibt es **einmalig auf der Konsole** (Terminal 1) aus. Damit anmelden, die App verlangt sofort ein neues Passwort (mindestens 10 Zeichen). Alternativ vor dem Start `Auth__BootstrapAdminPassword` setzen. Verloren? [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md#passwort-verloren-oder-kein-admin-mehr).

In der Umgebung `Development` (das Standardprofil von `dotnet run`) legt der veraltete Schalter `Database:Seed` (`appsettings.Development.json`) nur einen kleinen Datensatz an (20 Artikel, ein Lager, 8 Bestellungen, keine Historie). Den vollständigen Demo-Datensatz gibt es mit `LAGER_DEMO=1` (siehe oben). Details und der Weg in die Produktion: **[docs/GETTING_STARTED.md](docs/GETTING_STARTED.md)**.

---

## Screenshots

_Screenshots folgen (Ablage: `docs/screenshots/`, nur mit Demo-Daten)._ Wie der Eigentümer sie im Demo-Modus erstellt, steht in [docs/screenshots/README.md](docs/screenshots/README.md).

---

## Was die Software kann

Stand je Funktion: **UI** = in der Weboberfläche bedienbar, **API** = nur über die REST-API, **teilweise** = mit Einschränkung (siehe [docs/USAGE.md](docs/USAGE.md#bekannte-einschränkungen)).

**Stammdaten**

| Funktion | Stand |
|---|---|
| Artikel mit Maßen, Gewicht, Stapelbarkeit, Bestandsschwellen, Einkaufspreis, Standard-Lieferant, GTIN/EAN (mit Prüfziffer) | UI |
| Bundles/Kits (Auflösung beim Kommissionieren), Saison-Fenster (Prüfung beim Anlegen einer Bestellung), Alternativ-SKUs (von Artikelsuche und Scan-Auflösung als Rückfall berücksichtigt; einen Ersatzartikel-Vorschlag bei fehlendem Bestand gibt es nicht) | UI (Artikel-Editor) |
| Auflösung eines gescannten Codes über GTIN, SKU oder Alternativ-SKU (`GET /api/articles/by-code/...`) | API (Mobile-Picker und Globalsuche nutzen sie noch nicht) |
| Lagerstruktur: Lager, Zonen, Gänge, Regale und Lagerplätze anlegen, ändern, löschen; Bin-Typ (Standard, HotPick, Reserve) und Nachschub-Schwelle | UI (Manager, Seite **Lagerstruktur**) |
| Lager-Layout: Regale, Lagerplätze, Wände, Pickpunkte, Heatmap | UI |
| Lieferanten; Kunden mit mehreren Adressen | UI |
| Benutzer mit Rollen (Admin, Manager, Receiver, Picker, Packer, Viewer) | UI |
| CSV-Import (Artikel, Bestand, Bestellungen; mit Trockenlauf und Zeilenfehlern) und CSV-Export (zusätzlich Bewegungen und Audit) | UI (Manager, Seite **Import & Export**) |

**Operatives**

| Funktion | Stand |
|---|---|
| Wareneingang mit Positionen, Putaway-Vorschlägen, Charge und MHD (Kopf und Zeilen werden atomar angelegt); Wareneingang aus einer Einkaufsbestellung | UI (Bestellung: **Wareneingang anlegen** in der Beschaffung, bucht noch keinen Bestand) |
| Bestand mit Chargen/MHD (Buchung mit Charge und MHD, Unterzeilen je Charge, nächstes MHD), manuelle Korrektur, Bestandsalarme | UI |
| Bestellungen: manuell, externe API mit Idempotenz, Priorität, Fälligkeit, Kunde/Lieferadresse, Storno | UI (externe API: API) |
| Kommissionierung: FEFO, Abgelaufenes wird nicht gepickt, Route um Wände (Regale sind keine Hindernisse) | UI |
| Wagen-Pickliste (Bündelung nach Volumen und Gewicht des Wagens, ohne 3D-Packen) und Wellen | UI |
| Mobile-Picker mit Barcode-Scan (Kamera nur über HTTPS oder localhost, `BarcodeDetector`) | UI |
| Packen mit Ist-Mengen und Bestandsabbuchung; Packvorschlag (Karton-Heuristik); PDF-Lieferschein mit Code-128-Barcode der Bestellnummer | UI |
| Versand mit manueller Tracking-Nr; DHL/UPS nicht angebunden | UI |
| Retouren mit Qualitätsprüfung (A-/B-Ware, Defekt, Vernichtung) | UI (Bezug zur Bestellung: API) |
| Inventur (Snapshot, Zählung, Abgleich); Nachschub (HotPick aus Reserve, Bin-Typ in der Lagerstruktur einstellbar) | UI |
| Einkauf: Bestellvorschläge, Einkaufsbestellungen | UI |
| Etiketten für Lagerplatz, Artikel und Bestellung: Druck im Browser (Code 128, Einzeletikett 50 × 30 mm oder A4-Bogen 3 × 8) und ZPL-Download für Zebra-Drucker | UI (Seite **Etiketten**) |

**Auswertungen und System**

| Funktion | Stand |
|---|---|
| KPI-Dashboard (Bestellungen, Picklisten, Picks pro Stunde, Ø Pickdistanz), ABC-Analyse, Heatmap, Slotting-Vorschläge, Dead-Stock, Picker-Performance | UI |
| Lagerwert nach FIFO aus dem Ledger (Manager) | UI |
| Ablaufende Chargen (Warnliste mit Frist 7/30/90 Tage) und Chargen-Rückverfolgung (Wareneingänge, Bestand, Bewegungen, möglicherweise betroffene Bestellungen) | UI (Reports, Seite **Chargen-Trace**) |
| Bestandsverlauf eines Artikels | API |
| StockMovement-Ledger (nur anhängend, mit Kosten-Snapshot); keine Bestandsreservierung | intern, Auswertung per UI/API |
| Audit-Trail für Artikel, Bestand, Bestellungen, Picklisten, Layout-Objekte und Benutzer (nicht für alle Objekte, kein "voller" Audit-Trail); ein CSV-Import erscheint als ein Sammel-Eintrag | UI (Manager) |
| Backup und Restore der SQLite-Datenbank: Liste, Download, Zeitplan, Aufbewahrung, Restore mit Bestätigung (in Produktion gesperrt, bis `Backup__AllowRestore` gesetzt ist) | UI (Admin, **System > Backup & Restore**) |
| Demo-Modus mit Beispieldaten, Historie und Demo-Benutzern | Konfiguration (`Demo__Enabled` bzw. `LAGER_DEMO=1`) |
| Docker-Betrieb aus einem Container (Oberfläche und API), Health-Endpunkte `/health/live` und `/health/ready` | Docker Compose (Container-Build noch nicht praktisch getestet) |
| Oberfläche auf Deutsch und Englisch (Umschalter DE/EN in der Seitenleiste und auf der Anmeldeseite, Wahl pro Browser); Meldungen des Servers und die Doku bleiben deutsch | UI ([docs/features/i18n.md](docs/features/i18n.md)) |
| Swagger UI (`/swagger`) und OpenAPI-Beschreibung mit Zusammenfassung, Mindestrolle und Fehlerantworten je Endpunkt; in `Development` oder mit `Swagger:Enabled=true` | Konfiguration ([docs/features/openapi.md](docs/features/openapi.md)) |
| Dark Mode, globale Suche (Strg/Cmd + K), PWA-Manifest und Service-Worker (nur über HTTPS im Produktions-Build) | UI |

---

## Sicherheit

Lager ist **standardmäßig geschlossen**: jeder Endpunkt außer dem Login (und den Health-Endpunkten) verlangt ein Token und die passende Rolle. Trotzdem gehört ein Produktivbetrieb nicht ohne Vorbereitung ins Internet:

- **Umgebung:** `dotnet run` startet als `Development` (Swagger, Reseed, freier Restore). Produktion: `ASPNETCORE_ENVIRONMENT=Production`; das Docker-Image läuft bereits so. Swagger bleibt dort aus; wer es mit `Swagger__Enabled=true` einschaltet, macht die API-Beschreibung ohne Anmeldung lesbar (die Endpunkte bleiben geschützt).
- **JWT-Key:** `Jwt__SigningKey` (mindestens 32 Bytes) oder `Jwt__KeyFile` setzen; ohne Key startet die API in Produktion nicht. Das Docker-Image legt den Schlüssel beim ersten Start im Volume ab (`/data/jwt.key`). Nie einchecken.
- **Admin-Zugang:** kein Standardpasswort; Einmalpasswort von der Konsole bzw. aus dem Container-Log oder `Auth__BootstrapAdminPassword`, sofort ändern. Wer eine ältere Datenbank mit dem früheren öffentlichen Standardpasswort nutzt: Passwort **jetzt** ändern.
- **Demo-Modus** nur zum Ausprobieren: Demo-Daten und Demo-Benutzer gehören nicht in ein Produktivsystem (in `Production` bricht der Start ohne ausdrückliche Freigabe ab).
- **HTTPS:** TLS am Reverse-Proxy beenden (Caddy-Profil im Compose-Setup), dann `Security__ForwardedHeaders__Enabled=true`; ohne Proxy `Security__RequireHttps=true`. Nie Zugangsdaten über HTTP übertragen.
- **Hosts und CORS:** `AllowedHosts` und `Cors__AllowedOrigins__0` auf die eigene Domain setzen.
- **Rollen:** Benutzer mit den kleinsten nötigen Rollen anlegen; Konten werden nach 5 Fehlversuchen 15 Minuten gesperrt, der Login ist rate-limitiert.
- **Backups** sind unverschlüsselt und enthalten Passwort-Hashes; gut aufbewahren, nicht einchecken. Datenbank, Logs und Schlüssel gehören nicht ins Repository (`.gitignore`).

Die Checkliste im Detail: [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#7-production-setup-kurz). Schwachstellen bitte vertraulich melden: [SECURITY.md](SECURITY.md).

---

## Projekt-Struktur

```
src/
├── Lager.Domain/          Entitäten + Value Objects (framework-frei)
├── Lager.Application/     Services, Abstractions, Pickroute-/Pack-Optimierer, CSV-Import/-Export
├── Lager.Infrastructure/  EF Core, Repositories, Auth, Schema-Schritte, Backup, Carrier-Adapter
├── Lager.Contracts/       DTOs für API und Anwendungsschicht
└── Lager.Api/             ASP.NET Core: Controller, Sicherheit, Fehlerbehandlung, Seeder, Health, Etiketten

frontend/lager-ui/         React + Vite + TypeScript + Konva (Feature-Pakete unter src/features/)
tests/Lager.Tests/         xUnit-Tests (API im Speicher, Domain, Algorithmen, Doku)
docs/                      Anleitungen und Referenz (features/ je Bereich, samples/ CSV-Beispiele, screenshots/)
deploy/                    Beispiel für Caddy als Reverse-Proxy
Dockerfile                 ein Container mit Oberfläche und API
docker-compose.yml         Compose-Setup (Zusatzdatei docker-compose.mysql.yml für MySQL)
.env.example               Vorlage der Einstellungen für Docker Compose
```

Details zur Architektur: **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.

---

## Mitwirken

Beiträge sind willkommen. Bitte zuerst [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) und [SECURITY.md](SECURITY.md) lesen; Änderungen kommen in den Abschnitt `[Unreleased]` des [CHANGELOG.md](CHANGELOG.md). Vor einem Pull Request müssen diese Prüfungen durchlaufen (die CI führt sie ebenfalls aus):

```powershell
dotnet build Lager.sln -p:LagerStrict=true
dotnet test tests/Lager.Tests

cd frontend/lager-ui
npm ci
npm run lint
npm run typecheck
npm test
npm run build
```

`-p:LagerStrict=true` macht Warnungen zu Fehlern (wie in der CI). `npx tsc --noEmit` prüft im Frontend **nichts** (die Wurzel-`tsconfig.json` enthält nur Project References); die Typprüfung ist `npm run typecheck` (`tsc -b`). Bei neuen Entitäten oder Spalten gehört ein Schritt in `src/Lager.Infrastructure/Persistence/SchemaSteps/` dazu (EF-Migrationen gibt es nicht), siehe [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#schema-evolution).

---

## Drittlizenzen

Die PDF-Lieferscheine nutzen **QuestPDF** unter der **Community-Lizenz**. Sie ist kostenlos, unter anderem für Open-Source-Projekte, Bildung und Unternehmen mit weniger als 1 Mio. USD Jahresumsatz. **Wer diese Bedingungen nicht erfüllt (etwa größere Firmen, die Lager selbst betreiben), braucht eine kommerzielle QuestPDF-Lizenz** und muss die Lizenzzuweisung in `src/Lager.Api/Documents/ShippingLabelRenderer.cs` entsprechend ändern. Maßgeblich ist der Wortlaut auf [questpdf.com/license](https://www.questpdf.com/license/); das ist keine Rechtsberatung. Weitere Abhängigkeiten (NuGet, npm) stehen unter ihren jeweiligen eigenen Lizenzen.

---

## Lizenz

Dieses Projekt steht unter der **MIT-Lizenz**, siehe [LICENSE](LICENSE). Die Abhängigkeit QuestPDF hat eine eigene Lizenz (siehe [Drittlizenzen](#drittlizenzen)).
