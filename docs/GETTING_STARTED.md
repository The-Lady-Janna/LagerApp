# Getting Started

Diese Anleitung führt durch den ersten Start des Lager-Systems: am schnellsten mit Docker, sonst von der Installation über den Entwicklungsbetrieb bis zum Produktivbetrieb. Konfigurationsschlüssel stehen in [CONFIGURATION.md](CONFIGURATION.md), Probleme löst [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

---

## Schnellstart mit Docker

Der kürzeste Weg: ein Container liefert Oberfläche **und** API aus, die Daten liegen auf einem Volume. Nötig ist nur **Docker mit Docker Compose v2** (`docker compose version`), kein .NET und kein Node.

```bash
git clone https://github.com/The-Lady-Janna/LagerApp.git lager
cd lager
docker compose up --build
```

Der erste Build braucht einige Minuten (Frontend bauen, API veröffentlichen, Laufzeit-Image). Danach ist alles unter **http://localhost:8080** erreichbar: die Oberfläche, `/api/...` und `/health/ready`. `docker compose ps` zeigt `healthy`, sobald die Datenbank bereit ist (der Healthcheck ruft `GET /health/ready`, siehe [CONFIGURATION.md](CONFIGURATION.md#health-endpunkte)).

**Erster Login:** Benutzer `admin`. Ein Standardpasswort gibt es nicht: beim allerersten Start erzeugt die Anwendung ein Einmalpasswort und schreibt es **einmalig ins Container-Log** (nicht in die Logdatei):

```bash
docker compose logs lager
```

Im Log steht ein eingerahmter Block "Lager: Bootstrap-Admin angelegt" mit dem Passwort. **Sofort kopieren**, danach verlangt die App ein neues Passwort (mindestens 10 Zeichen, siehe [Erstes Login](#4-erstes-login)). Alternativ ein eigenes Startpasswort in `.env` setzen (`Auth__BootstrapAdminPassword`, siehe unten).

### Was wo liegt

| Was | Wo |
|---|---|
| SQLite-Datenbank (`lager.db`), JWT-Schlüssel (`jwt.key`), Logdateien (`logs/`), Backups (`backups/`) | Volume `lager-data`, im Container `/data` |
| Daten überleben | `docker compose down` und `docker compose up` (das Volume bleibt); Neustart des Rechners |
| Daten sind **weg** | `docker compose down -v` (löscht das Volume) |
| Einstellungen | `.env` neben der `docker-compose.yml` (Vorlage `.env.example`, alles optional; ohne `.env` gelten sichere Standardwerte) |

Der JWT-Schlüssel entsteht beim ersten Start automatisch im Volume (`Jwt__KeyFile=/data/jwt.key`): nach einem Neustart bleiben die Anmeldungen gültig. Das Image läuft als `Production` (kein Swagger, sofern nicht `Swagger__Enabled=true` gesetzt wird, kein Reseed, Restore gesperrt) und unter einem unprivilegierten Benutzer. Ein benanntes Volume bekommt die richtigen Rechte selbst; bei einem eigenen Verzeichnis als Volume (Bind-Mount) muss es für die Benutzer-ID 1654 beschreibbar sein.

### Einstellungen in `.env`

```bash
cp .env.example .env     # dann die gewünschten Zeilen einkommentieren
```

Die wichtigsten (alle Schlüssel und Erklärungen stehen in der `.env.example`, die Bedeutung jedes Schlüssels in [CONFIGURATION.md](CONFIGURATION.md)):

| Eintrag in `.env` | Wirkung |
|---|---|
| `LAGER_PORT=8080` | Port auf dem Docker-Host; `127.0.0.1:8080` macht die Anwendung nur vom eigenen Rechner erreichbar (z. B. hinter Caddy) |
| `AllowedHosts=lager.example.com;192.168.1.20` | erlaubte Host-Namen (Host-Header). **Für jeden Zugriff aus dem LAN oder unter einer Domain nötig**, sonst antwortet die API mit 400 "Bad Request - Invalid Hostname". `localhost` bleibt immer erlaubt |
| `Auth__BootstrapAdminPassword=...` | eigenes Startpasswort des Admins statt des Einmalpassworts (mindestens 10 Zeichen) |
| `Jwt__SigningKey=...` | eigener JWT-Schlüssel statt der Schlüsseldatei (hat Vorrang; mindestens 32 Bytes) |
| `Cors__AllowedOrigins__0=...` | nur nötig, wenn ein **anderer** Origin die API aufruft (die mitgelieferte Oberfläche braucht kein CORS) |
| `Backup__AllowRestore=true` | gibt den Restore aus der Oberfläche frei (nur für die Dauer des Restores) |
| `Backup__Schedule=02:00`, `Backup__RetentionCount=14` | automatisches Backup: tägliche Startzeit in **UTC** (leer = kein Zeitplan) und Zahl der aufbewahrten Backups (siehe [Backup und Restore](#backup-und-restore)) |
| `Demo__Enabled=true`, `Demo__AllowInProduction=true` | Demo-Modus mit Beispieldaten (nur für eine **leere** Datenbank; das Image läuft als `Production`, deshalb beide; siehe [Demo-Modus](#5-demo-modus-und-demo-daten-erkunden)) |
| `Serilog__MinimumLevel__Default=Debug`, `Logging__RetainedFileCount=14` | Log-Level und Aufbewahrung |
| `LAGER_DB_PASSWORD`, `LAGER_DB_ROOT_PASSWORD` | Passwörter der MySQL-Variante (siehe unten) |
| `LAGER_DOMAIN=lager.example.com` | Name für das Caddy-Profil (siehe unten) |

Die `docker-compose.yml` reicht **nur** die dort im Block `environment` aufgeführten Einstellungen an den Container durch (bewusst keine ganze Datei, damit z. B. das Root-Passwort der Datenbank nie im Lager-Container landet). Jede weitere Einstellung (z. B. `Swagger__Enabled` oder `Security__RateLimiting__Enabled`) steht nicht auf dieser Liste: sie wird im Block `environment` der `docker-compose.yml` ergänzt oder in einer eigenen Zusatzdatei, die Compose mit `-f` zusammenführt. Schlüssel schreibt man dabei mit `__` statt `:` (`Backup__Schedule` = `Backup:Schedule`).

### Anhalten, Neustarten, Aktualisieren

```bash
docker compose stop              # anhalten, Daten bleiben
docker compose up -d             # im Hintergrund (wieder) starten
docker compose restart           # z. B. nach einem Restore
docker compose logs -f lager     # Log mitlesen
```

**Update:** erst ein Backup anlegen (**System > Backup & Restore** in der Oberfläche), dann `git pull` und `docker compose up --build -d`. Die Datenbank wird beim Start automatisch auf den neuen Stand gebracht (siehe [Updates](#updates)).

### MySQL statt SQLite

```bash
# in .env setzen: LAGER_DB_PASSWORD und LAGER_DB_ROOT_PASSWORD (ohne sie bricht Compose ab)
docker compose -f docker-compose.yml -f docker-compose.mysql.yml up --build
```

Die Zusatzdatei startet einen MySQL-8.4-Container (Volume `mysql-data`) und stellt `Database__Provider=MySql` ein; Lager wartet mit dem Start, bis MySQL bereit ist. Die Passwörter landen in einem Verbindungsstring: Zeichen wie `;` oder `=` darin vermeiden. **Backup und Restore der Anwendung gibt es bei MySQL nicht:** gesichert wird mit `mysqldump` (z. B. im MySQL-Container mit `docker compose exec mysql mysqldump ...`, der Aufruf steht auch auf der Seite **System > Backup & Restore**). MySQL ist weniger erprobt als SQLite, siehe [MySQL](TROUBLESHOOTING.md#mysql).

### HTTPS und Kamera-Scan am Handy (Caddy)

Der Kamera-Scan im Mobile-Picker und der Service-Worker (PWA) brauchen einen **sicheren Kontext (HTTPS)**. Dafür gibt es ein optionales Compose-Profil mit Caddy als Reverse-Proxy:

```bash
# in .env: LAGER_DOMAIN, AllowedHosts (derselbe Name) und Security__ForwardedHeaders__Enabled=true setzen
docker compose --profile https up --build
```

Caddy (`deploy/Caddyfile`) beantragt für einen öffentlichen Namen automatisch ein Zertifikat (Ports 80 und 443 müssen aus dem Internet erreichbar sein). Für einen **lokalen Namen ohne öffentliches DNS** (z. B. `lager.lan`) in `deploy/Caddyfile` die Zeile `tls internal` einkommentieren: Caddy stellt dann Zertifikate aus einer eigenen CA aus, deren Root-Zertifikat (Volume `caddy-data`, Pfad `pki/authorities/local/root.crt`) auf den Geräten als vertrauenswürdig installiert werden muss. Soll Lager nur über Caddy erreichbar sein, `LAGER_PORT=127.0.0.1:8080` setzen. `Security__ForwardedHeaders__Enabled=true` sorgt dafür, dass die Anwendung die echte Client-IP aus `X-Forwarded-For` sieht (Rate-Limit je Client); `Security__RequireHttps` bleibt aus, die Umleitung übernimmt der Proxy.

### Demo-Modus in Docker

Siehe [Demo-Modus](#5-demo-modus-und-demo-daten-erkunden): Der Container läuft als `Production`, der Demo-Modus braucht deshalb zwei Schalter und eine Zusatzdatei.

> **Stand der Prüfung:** Die Compose-Dateien sind mit `docker compose config` geprüft (auch mit MySQL-Zusatzdatei und Profil `https`). Den Image-Build baut der CI-Job `Docker Build-Check` (`.github/workflows/docker.yml`); beim Schreiben dieser Anleitung stand kein laufender Docker-Daemon zur Verfügung, ein praktischer Durchlauf von `docker compose up --build` (Login, Deep-Link, PDF-Lieferschein im Container) steht deshalb noch aus, siehe [TODO.md](../TODO.md).

---

## 1. Voraussetzungen

Für die Entwicklung und den Betrieb ohne Docker:

| Tool | Version | Prüfen mit |
|---|---|---|
| **.NET SDK** | 8.0.419 oder neuer aus der 8.0-Reihe (`global.json` pinnt 8.0.419 und erlaubt neuere Feature-Bands; ein reines .NET-9-SDK genügt nicht) | `dotnet --version` |
| **Node.js** | `^20.19.0` oder `>=22.13.0` (`engines` in `package.json`; Vite 8 und ESLint 10 verlangen das) | `node --version` |
| **npm** | 10 oder neuer | `npm --version` |
| Git | beliebig | `git --version` |

Optional für den MySQL-Betrieb: ein MySQL-8-Server (oder MariaDB). Für den ersten Start reicht SQLite, dafür ist nichts einzurichten.

---

## 2. Installation

```powershell
git clone https://github.com/The-Lady-Janna/LagerApp.git lager
cd lager
```


### Backend-Abhängigkeiten

```powershell
dotnet restore
```

Lädt alle NuGet-Pakete. Beim ersten Mal dauert das eine Minute.

### Frontend-Abhängigkeiten

```powershell
cd frontend/lager-ui
npm ci
cd ../..
```

`npm ci` installiert genau die Versionen aus `package-lock.json`.

---

## 3. Erster Start

Backend und Frontend laufen in **zwei separaten Terminals**, weil beide blockieren.

### Terminal 1 — Backend

```powershell
dotnet run --project src/Lager.Api
```

`dotnet run` nutzt das Profil `http` aus `src/Lager.Api/Properties/launchSettings.json`: Umgebung **`Development`**, Adresse `http://localhost:5099`. Beim allerersten Start passiert automatisch:

1. Die SQLite-Datenbank `src/Lager.Api/lager.db` wird angelegt (dazu `lager.db-wal` und `lager.db-shm`; ein relativer Pfad gilt relativ zum Projektverzeichnis).
2. Das Schema entsteht aus dem Modell (`EnsureCreated`), danach bringt der `SchemaUpgrader` jede Datenbank auf den aktuellen Stand (bei einer neuen Datenbank ohne Änderungen).
3. Der **Bootstrap-Admin** `admin` wird angelegt. Es gibt **kein Standardpasswort**: die Anwendung erzeugt ein Zufalls-Einmalpasswort und gibt es **einmalig auf der Konsole** aus (nicht in die Logdatei):

   ```
   ==================================================================
    Lager: Bootstrap-Admin angelegt (Einmalpasswort, wird nur jetzt angezeigt)
      Benutzer : admin
      Passwort : <zufälliges 20-stelliges Passwort>
    Das Passwort muss beim ersten Login geändert werden.
   ==================================================================
   ```

   **Dieses Passwort sofort kopieren.** Es wird nirgends gespeichert. (Alternativ vor dem Start `Auth__BootstrapAdminPassword` setzen, dann gilt dieses Passwort.)
4. Die **Beispieldaten** werden geladen, wenn sie eingeschaltet sind und die Datenbank **leer** ist. Unter `Development` steht `Database:Seed=true` in `appsettings.Development.json`: das ist der **veraltete** Schalter und legt nur einen kleinen Datensatz an (20 Artikel, ein Lager `WH01` mit 3 Gängen, 12 Regalen und 36 Lagerplätzen, 25 Bestandszeilen ohne Ledger, 8 Bestellungen, 5 Wände, 3 Pickpunkte) und warnt bei jedem Start im Log. Den vollständigen, realistischen Datensatz mit Historie und Demo-Benutzern gibt es mit dem **Demo-Modus** (`LAGER_DEMO=1`, siehe [Abschnitt 5](#5-demo-modus-und-demo-daten-erkunden)); er muss schon beim **allerersten** Start gesetzt sein oder die Datenbank vorher gelöscht werden, weil nur in eine leere Datenbank angelegt wird.

**Swagger** (API-Oberfläche, in `Development` an, sonst mit `Swagger:Enabled=true`; `Swagger:Enabled=false` schaltet es auch hier ab): `http://localhost:5099/swagger`. Zum Ausprobieren erst `POST /api/auth/login` ausführen und das `token` oben über **Authorize** eintragen; das Token der Weboberfläche gilt in Swagger nicht. Was die Beschreibung enthält und wie man sie in Produktion einschaltet: [features/openapi.md](features/openapi.md).

### Terminal 2 — Frontend

```powershell
cd frontend/lager-ui
npm run dev
```

Der Entwicklungsserver läuft auf `http://localhost:5173` und leitet `/api` automatisch an `http://localhost:5099` weiter (anderes Ziel: Umgebungsvariable `VITE_API_TARGET`).

> **Windows-PowerShell-Hinweis:** PowerShell 5.1 kennt `&&` nicht. Statt `cd x && npm run dev` zwei Zeilen oder `cd x; npm run dev` schreiben.

---

## 4. Erstes Login

Browser öffnen: **http://localhost:5173** (Docker: **http://localhost:8080**). Die Anmeldeseite (Vollbild) verlangt:

- **Benutzername:** `admin`
- **Passwort:** das Einmalpasswort aus der Konsole des Backends (Docker: aus `docker compose logs lager`)

Danach sperrt die App sofort alles außer dem Dialog **"Passwort ändern"**: neues Passwort mit **mindestens 10 Zeichen** eingeben (höchstens 72 Bytes, nicht gleich dem Benutzernamen oder dem bisherigen Passwort). Danach ist die App nutzbar. Im Seitenleisten-Fuß lässt sich das Passwort jederzeit erneut ändern (**🔒 Passwort ändern**).

Weitere Benutzer legt der Admin unter **System > Benutzer** an (siehe [USAGE.md](USAGE.md#login--benutzer)). Passwort oder Einmalpasswort verloren? [TROUBLESHOOTING.md](TROUBLESHOOTING.md#passwort-verloren-oder-kein-admin-mehr).

> **Achtung bei älteren Datenbanken:** Frühere Versionen legten den Bootstrap-Admin mit einem öffentlich bekannten Standardpasswort an. Wer eine solche Datenbank weiter nutzt, muss das Passwort dieses Kontos sofort ändern. Das Konto bleibt gültig, auch wenn neue Installationen das Passwort nicht mehr kennen.

---

## 5. Demo-Modus und Demo-Daten erkunden

### Demo-Modus einschalten

Der **Demo-Modus** füllt eine **leere** Datenbank beim Start mit einem erfundenen, aber realistischen Betrieb (Stammdaten, rund 60 Tage Historie, Ist-Stand in allen Bereichen) und legt Demo-Benutzer je Rolle an. Er ist **standardmäßig aus**.

| Schalter | Umgebungsvariable | Wirkung |
|---|---|---|
| `Demo:Enabled` | `Demo__Enabled` | Demo-Modus an (Standard `false`) |
| (Kurzform) | `LAGER_DEMO` | `1`, `true`, `yes` oder `on` schaltet an, `0`, `false`, `no` oder `off` aus (schlägt die Konfigurationsdateien) |
| `Demo:AllowInProduction` | `Demo__AllowInProduction` | erlaubt den Demo-Modus in der Umgebung `Production` |

```powershell
# lokal (Umgebung Development): gleich beim ersten Start, mit einer frischen Datenbank
$env:LAGER_DEMO = "1"
dotnet run --project src/Lager.Api
```

In der Umgebung `Production` bricht der Start mit `Demo:Enabled=true` mit einer klaren Meldung ab, solange `Demo:AllowInProduction` nicht ausdrücklich `true` ist: Demo-Daten und Demo-Benutzer gehören nicht in ein Produktivsystem. Das **Docker-Image läuft als `Production`**, ein Demo-Container braucht deshalb beide Schalter. Die mitgelieferte `docker-compose.yml` reicht `Demo__Enabled` und `Demo__AllowInProduction` aus der `.env` durch, eine Zusatzdatei ist nicht nötig. Beide Zeilen in die `.env` neben der `docker-compose.yml` eintragen (Vorlage: `.env.example`):

```bash
Demo__Enabled=true
Demo__AllowInProduction=true
```

```bash
# mit einem neuen, leeren Volume (docker compose down -v löscht die vorhandenen Daten!)
docker compose up --build
docker compose logs lager     # die einmaligen Zugangsdaten der Demo-Benutzer
```

Nach dem Ausprobieren beide Zeilen wieder aus der `.env` entfernen: Demo-Daten und Demo-Benutzer gehören nicht in ein Produktivsystem.

Der Seeder legt nur an, wenn **keine** Fachdaten da sind (keine Artikel, Lager, Lagerplätze, Bestände, Bestellungen, Kunden, Lieferanten; Benutzer zählen nicht). Sonst schreibt er eine Info-Zeile und ändert nichts; er ergänzt oder repariert nie bestehende Daten. Alles entsteht in **einer Transaktion**. Ein Lauf dauert je nach Rechner rund 5 bis 30 Sekunden; die Anwendung nimmt in dieser Zeit noch keine Anfragen an (das Log meldet Beginn und Ende).

### Zugangsdaten der Demo-Benutzer

Der Admin bleibt der Bootstrap-Admin (Einmalpasswort wie in Abschnitt 3). Zusätzlich gibt es je Rolle Benutzer: `manager`, `picker`, `picker2`, `picker3`, `packer`, `receiver`, `viewer`. **Feste Passwörter gibt es nicht:** der Seeder erzeugt für jeden ein zufälliges Passwort und gibt es **genau einmal** als Warnung "Demo-Zugangsdaten" aus, in der **Konsole** (Docker: im Container-Log), bewusst nicht in der Logdatei:

```bash
docker compose logs lager | grep -A 7 "Demo-Zugangsdaten"
```

Die Demo-Benutzer müssen ihr Passwort nicht ändern. Wurde die Ausgabe verpasst, hilft nur ein Zurücksetzen: als Admin unter **System > Benutzer** ein neues Passwort vergeben (**PW reset**).

### Was der Demo-Datensatz enthält

Alles ist erfunden (keine echten Firmen, Personen oder Adressen, Mail-Domains nur `example.com`). Die Übersicht und die Entstehung beschreibt [features/demo-modus.md](features/demo-modus.md); in Kürze: Lager `WH01` mit Zonen, Gängen, Regalen, Lagerplätzen (Hot-Pick mit Nachschub-Schwelle, Reserve) und Wänden; über 40 Artikel mit Preisen, Schwellen und Maßen (darunter Bundles, Artikel mit GTIN, Alternativ-SKUs und Saison-Fenster); drei Lieferanten, fünf Kunden mit Adressen; Chargen mit MHD (mindestens drei laufen in 30 Tagen ab, eine ist abgelaufen); rund 60 Tage Historie mit Bestellungen in **allen** Status, Picklisten, Sendungen, Einkauf, Retouren, Inventuren und Nachschub.

| Seite | Was du siehst |
|---|---|
| **Artikel** | die Artikel mit Preisen und Schwellen, GTIN-Spalte, Bundles und Saison-Status |
| **Bestand** | Bestand je Artikel, Lagerplatz und Charge mit MHD-Status; Alarm-Karte für Artikel unter Mindestbestand |
| **Bestellungen** | Bestellungen in allen Status zum Kommissionieren bzw. zum Ansehen des Verlaufs |
| **Lager-Layout** und **Lagerstruktur** | Draufsicht mit Regalen, Lagerplätzen, Wänden und Pickpunkten; der Strukturbaum mit Bin-Typen |
| **Reports** | Lagerwert, Live-Status, Kennzahlen, ABC, Heatmap, Dead-Stock, Picker-Performance, ablaufende Chargen |
| **Chargen-Trace** | eine Charge der Demo-Daten eingeben und Wareneingänge, Bestand, Bewegungen, betroffene Bestellungen ansehen |

Ein guter Einstieg: Bestellungen > eine neue Bestellung öffnen > **Pickliste generieren**, dann **Picken** und **Packen** (siehe [USAGE.md](USAGE.md#picken)). Eine Anleitung, wie der Eigentümer mit dem Demo-Modus Screenshots erstellt: [screenshots/README.md](screenshots/README.md).

### Zurücksetzen

Es gibt **keinen** Demo-Reset-Endpunkt. Für einen sauberen Stand die Anwendung stoppen, die Datenbankdatei samt `-wal`/`-shm` löschen (Docker: `docker compose down -v`) und neu starten. Der Endpunkt `POST /api/admin/reseed` (nur `Development`, Rolle Admin; in der Oberfläche der Knopf "Komplette DB löschen + Demo-Daten neu erzeugen" auf der Seite **Picklisten**, nur im Entwicklungsbuild) löscht alle Fachdaten (Benutzer und Audit-Trail bleiben), legt aber nur den **kleinen Altbestand-Datensatz** an, nicht den Demo-Datensatz. Außerhalb von `Development` antwortet er mit 403. Zusätzlich gibt es `POST /api/admin/seed-bulk?count=1000` für Zufallsdaten (Leistungstests, nur `Development`).

> Admin-Endpunkte verlangen die Rolle Admin. In Swagger heißt das: mit einem Admin-Token über **Authorize** anmelden.

---

## 6. Auf MySQL umschalten (optional)

Standard ist SQLite (keine Einrichtung). Für MySQL/MariaDB (Provider `MySql`, Pomelo) so vorgehen (in Docker übernimmt das die Zusatzdatei, siehe oben). **MySQL ist weniger erprobt als SQLite**: die Test-Suite läuft nicht gegen einen echten MySQL-Server.

1. **Datenbank und eigenen Benutzer anlegen** (nicht `root` verwenden):

   ```sql
   CREATE DATABASE lager CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
   CREATE USER 'lager'@'%' IDENTIFIED BY '<starkes-passwort>';
   GRANT ALL PRIVILEGES ON lager.* TO 'lager'@'%';
   ```

2. **Verbindung per Umgebungsvariablen setzen** (Zugangsdaten nicht in `appsettings.json` schreiben):

   ```powershell
   $env:Database__Provider = "MySql"
   $env:Database__ConnectionString = "Server=localhost;Port=3306;Database=lager;User=lager;Password=<starkes-passwort>"
   $env:Database__MySqlServerVersion = "8.0.36"   # optional; ohne Angabe wird die Version beim Start beim Server erfragt
   ```

3. **Backend starten.** Der Server muss dabei erreichbar sein. Beim ersten Start entsteht das Schema in der leeren Datenbank (`EnsureCreated`), der Bootstrap-Admin wird angelegt, und mit `LAGER_DEMO=1` kommen die Demo-Daten dazu.

Backup und Restore über die Anwendung gibt es für MySQL nicht: dafür `mysqldump`. Stolpersteine (Zeichensatz, große Textspalten, Server-Version): [TROUBLESHOOTING.md](TROUBLESHOOTING.md#mysql).

---

## 7. Production-Setup (Kurz)

`dotnet run` startet wegen `launchSettings.json` immer in der Umgebung `Development`: mit Swagger, Reseed und freiem Restore. **Für den Produktivbetrieb muss die Umgebung ausdrücklich `Production` sein** (das Docker-Image tut das schon). Alle Schlüssel: [CONFIGURATION.md](CONFIGURATION.md).

### Checkliste

1. **Umgebung:** `ASPNETCORE_ENVIRONMENT=Production`. Damit sind Swagger, Reseed und Bulk-Seed aus und der Restore gesperrt. Swagger bleibt aus; wer es mit `Swagger__Enabled=true` einschaltet, macht die API-Beschreibung ohne Anmeldung lesbar (die Endpunkte bleiben geschützt).
2. **JWT-Schlüssel:** `Jwt__SigningKey` (mindestens 32 Bytes, z. B. `openssl rand -base64 48`) **oder** `Jwt__KeyFile` (Pfad einer Datei, die beim ersten Start mit einem Zufallskey angelegt wird). Ohne Key startet die API in Produktion nicht. Den Key nie einchecken.
3. **Admin-Zugang:** Entweder ein eigenes Startpasswort in `Auth__BootstrapAdminPassword` (mindestens 10 Zeichen) oder das Einmalpasswort von der Konsole nutzen. Beides ist nur für den allerersten Start relevant und muss beim ersten Login geändert werden.
4. **Demo-Modus aus:** `Demo:Enabled` bleibt `false` (Standard), ebenso der veraltete `Database:Seed`. In `Production` bricht der Start mit `Demo:Enabled=true` ab, außer `Demo__AllowInProduction=true` steht ausdrücklich dabei (nur für öffentliche Vorführungen).
5. **Datenbank:** absoluten Pfad setzen, z. B. `Database__ConnectionString="Data Source=/var/lib/lager/lager.db"` (ein relativer Pfad hängt vom Arbeitsverzeichnis ab; ein anderes Startverzeichnis erzeugt eine neue, leere Datenbank). Das Verzeichnis muss beschreibbar und Teil der Datensicherung sein. Bei MySQL siehe Abschnitt 6.
6. **Erlaubte Hosts:** `AllowedHosts=lager.example.com` (der Standard erlaubt nur `localhost`; jeder andere Host-Header bekommt 400).
7. **HTTPS:** TLS am Reverse-Proxy beenden (siehe unten). Hinter einem Proxy `Security__RequireHttps` **aus** lassen und `Security__ForwardedHeaders__Enabled=true` setzen (bei Bedarf `KnownProxies`/`KnownNetworks`), damit die API Client-IP und Schema erkennt. Ohne Proxy kann die API selbst HTTPS sprechen (Kestrel-Zertifikat, `ASPNETCORE_URLS=https://...`) und mit `Security__RequireHttps=true` HSTS und Weiterleitung erzwingen.
8. **CORS:** Werden Frontend und `/api` unter **einer** Adresse ausgeliefert (empfohlen), ist nichts nötig. Sonst `Cors__AllowedOrigins__0=https://lager.example.com` setzen.
9. **Datensicherung** einrichten (siehe unten: Zeitplan und Aufbewahrung) und einmal testweise zurückspielen.

### Backend veröffentlichen und starten

```powershell
dotnet publish src/Lager.Api -c Release -o publish
```

Starten (Beispiel Linux/Bash; Umgebungsvariablen vorher setzen, siehe [CONFIGURATION.md](CONFIGURATION.md#beispiele)):

```bash
cd publish
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS=http://127.0.0.1:5099
dotnet Lager.Api.dll
```

**Immer aus dem Verzeichnis der veröffentlichten Dateien starten** (bzw. dort das `WorkingDirectory` des Dienstes setzen): Die Anwendung nimmt das Arbeitsverzeichnis als Content-Root und sucht dort `appsettings.json` und `wwwroot`. Wer `dotnet publish/Lager.Api.dll` aus einem anderen Verzeichnis startet, bekommt weder die mitgelieferten Standardwerte (u. a. den Host-Filter `AllowedHosts`) noch das Frontend, und `logs/` sowie relative Datenbankpfade zeigen auf das falsche Verzeichnis.

Ohne `ASPNETCORE_URLS` lauscht Kestrel in Produktion auf `http://localhost:5000` (nicht `5099`). Als Dienst betreiben, z. B. mit systemd (Beispiel in [CONFIGURATION.md](CONFIGURATION.md#beispiele)) oder unter Windows über IIS (ASP.NET Core Hosting Bundle) bzw. ein Dienst-Werkzeug. Das Backend hat keinen eigenen Windows-Dienst-Modus.

Ob die Anwendung läuft und die Datenbank erreichbar ist, zeigen die anonymen Endpunkte `GET /health/live` und `GET /health/ready` (200 bzw. 503, siehe [CONFIGURATION.md](CONFIGURATION.md#health-endpunkte)); sie eignen sich für Monitoring und Proxy-Prüfungen.

### Frontend ausliefern

```powershell
cd frontend/lager-ui
npm ci
npm run build
# Ausgabe: frontend/lager-ui/dist/
```

**`dotnet publish` baut und kopiert das Frontend nicht.** Zwei Wege:

**A) Das Backend liefert es mit aus (ein Prozess, ein Port, eine Origin).** Den Inhalt von `dist/` nach `wwwroot` im Verzeichnis der veröffentlichten Dateien kopieren:

```powershell
Copy-Item frontend/lager-ui/dist publish/wwwroot -Recurse
```

Sobald `wwwroot/index.html` im Verzeichnis der veröffentlichten Dateien (neben `Lager.Api.dll`) liegt, liefert das Backend die Oberfläche unter `/` aus (Pfade wie `/orders` fallen auf `index.html` zurück, `/api`, `/health` und `/swagger` nicht; das Log meldet beim Start, ob ein Frontend gefunden wurde). Ein Reverse-Proxy leitet dann alles an das Backend weiter, CORS ist nicht nötig. Die Cache-Regeln stehen in [CONFIGURATION.md](CONFIGURATION.md#frontend-auslieferung-und-entwicklungsserver). Das Docker-Image macht genau das.

**B) Ein eigener Webserver.** Er muss (1) den Inhalt von `dist/` ausliefern, (2) unbekannte Pfade auf `index.html` zurückfallen lassen (die App nutzt Router-Pfade wie `/orders`) und (3) `/api` an das Backend weiterreichen. Das gebaute Frontend ruft immer relative `/api`-Pfade auf.

nginx (Auszug für Weg B):

```nginx
server {
    listen 443 ssl;
    server_name lager.example.com;
    # ssl_certificate ...; ssl_certificate_key ...;

    root /var/www/lager;                      # Inhalt von frontend/lager-ui/dist/
    index index.html;

    location /api/ {
        proxy_pass http://127.0.0.1:5099;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        client_max_body_size 1g;              # nur nötig für den Restore-Upload
    }

    location = /index.html { add_header Cache-Control "no-cache"; }
    location = /sw.js      { add_header Cache-Control "no-cache"; }
    location /             { try_files $uri /index.html; }
}
```

Caddy (Weg B; setzt die `X-Forwarded-*`-Header selbst):

```
lager.example.com {
    root * /var/www/lager
    handle /api/* {
        reverse_proxy 127.0.0.1:5099
    }
    handle {
        try_files {path} /index.html
        file_server
    }
}
```

Bei Weg A genügt in Caddy `lager.example.com { reverse_proxy 127.0.0.1:5099 }`. Mit dem Docker-Setup übernimmt das das Profil `https` (siehe [Schnellstart mit Docker](#schnellstart-mit-docker)).

Über HTTPS ausgeliefert funktionieren auch die Kamera im Mobile-Picker und die Installation als App (PWA); über reines HTTP nicht (siehe [TROUBLESHOOTING.md](TROUBLESHOOTING.md#oberfläche-und-scanner)).

### Docker im Produktivbetrieb

Das Docker-Setup aus dem [Schnellstart](#schnellstart-mit-docker) ist der empfohlene Weg für den Betrieb mit einem Container. Für den Produktivbetrieb zusätzlich:

1. **`.env` anlegen:** `AllowedHosts` auf den eigenen Namen, ggf. `Auth__BootstrapAdminPassword` und `Jwt__SigningKey` (sonst Einmalpasswort im Log und Schlüsseldatei im Volume); die `.env` gehört nicht ins Repository (`.gitignore`).
2. **HTTPS:** Profil `https` mit Caddy (siehe oben) oder ein eigener Reverse-Proxy vor `LAGER_PORT=127.0.0.1:8080`; dann `Security__ForwardedHeaders__Enabled=true`.
3. **Backups:** Zeitplan und Aufbewahrung in der `.env` setzen, z. B. `Backup__Schedule=02:00` (täglich 02:00 UTC) und `Backup__RetentionCount=14` (die `docker-compose.yml` reicht beide durch). Sie landen im Volume unter `/data/backups`: **dieses Volume schützt nicht vor einem Plattenausfall**, das Verzeichnis zusätzlich auf ein anderes Medium kopieren (z. B. `docker compose cp lager:/data/backups ./backups-kopie` oder Sicherung des Volume-Verzeichnisses des Docker-Hosts). Den Restore brauchen Admins nur im Notfall: `Backup__AllowRestore=true` in `.env` setzen, `docker compose up -d`, Restore in der Oberfläche, danach `docker compose restart` und das Flag wieder entfernen.
4. **Updates:** siehe [Schnellstart](#anhalten-neustarten-aktualisieren). Der Container startet mit `restart: unless-stopped` nach einem Rechnerneustart wieder.
5. **Log:** `docker compose logs lager` (Konsole, enthält die einmaligen Zugangsdaten) und die Dateien im Volume unter `/data/logs`. Die Health-Probes des Compose-Healthchecks erscheinen nur bei einem Fehler im Log (auf Level Debug sind sie zu sehen).

### Backup und Restore

**Backup und Restore (SQLite, Rolle Admin)** gibt es in der Oberfläche unter **System > Backup & Restore**; die ausführliche Beschreibung steht in [features/backup-restore.md](features/backup-restore.md), die Bedienung in [USAGE.md](USAGE.md#backup-und-restore).

- **Backup jetzt erstellen** legt die Datei `lager-backup-<UTC-Zeit>.db` an (konsistenter Snapshot per `VACUUM INTO`, auch unter Last), neben der Datenbank oder in `Backup__Directory` (Docker: `/data/backups`). **Herunterladen** holt die Datei über den Browser.
- **Zeitplan:** `Backup__Schedule` (`HH:mm` in **UTC**, z. B. `02:00`; leer = kein Zeitplan) und **Aufbewahrung** `Backup__RetentionCount` (Standard 14; ältere Backups werden nach jedem neuen gelöscht). Ein verpasster Lauf wird nicht nachgeholt. Die Werte sind in [CONFIGURATION.md](CONFIGURATION.md#backup-und-restore) beschrieben.
- **Ohne Oberfläche** (Skript): `POST /api/admin/backups` (oder das ältere `POST /api/admin/backup`) mit Admin-Token:

  ```bash
  curl -X POST http://127.0.0.1:5099/api/admin/backups -H "Authorization: Bearer $TOKEN"
  ```

- **Restore** ersetzt die laufende Datenbank: in der Oberfläche **Wiederherstellen** (aus der Liste) oder **Backup-Datei einspielen…**, zur Bestätigung `RESTORE` eintippen. Ablauf: Datei prüfen (SQLite-Header, Integritätsprüfung, Pflichttabellen, Schemastand nicht neuer als die App), Sicherheitskopie `lager-before-restore-<Zeit>.db` anlegen, Datei austauschen. **In Produktion ist er gesperrt**, solange `Backup__AllowRestore` nicht `true` ist (nur für die Dauer des Restores setzen). **Danach die Anwendung neu starten** (Docker: `docker compose restart`). Alternativ offline: Dienst stoppen, `lager.db` samt `-wal`/`-shm` ersetzen, Dienst starten.

**Backups sind unverschlüsselt und enthalten alle Daten samt Passwort-Hashes:** an einem geschützten Ort ablegen, nie ins Repository. MySQL: `mysqldump` bzw. Sicherung des Servers.

### Updates

1. **Backup** der Datenbank anlegen.
2. Anwendung stoppen, neue Dateien einspielen (`dotnet publish`), Frontend neu bauen und ausliefern (bei Weg A `wwwroot` ersetzen; Docker: `git pull` und `docker compose up --build -d`). Ein Browser mit installiertem Service-Worker zeigt danach ein Banner "Neue Version verfügbar"; bleibt der alte Stand, hilft ein hartes Neuladen (siehe [TROUBLESHOOTING.md](TROUBLESHOOTING.md#oberfläche-und-scanner)).
3. Anwendung starten. Der `SchemaUpgrader` aktualisiert die Datenbank automatisch; im Log steht `Datenbankschema ist aktuell` bzw. je angewendetem Schritt eine Meldung. Scheitert ein kritischer Schritt, startet die Anwendung nicht (siehe [TROUBLESHOOTING.md](TROUBLESHOOTING.md#start-schlägt-fehl)).
4. Vor größeren Updates das Update **mit einer Kopie der echten Datenbank** proben. MySQL-Schritte sind nicht gegen einen echten Server getestet.

---

## 8. Was als Nächstes?

- **[USAGE.md](USAGE.md)** — wie die Workflows (Lagerstruktur, Wareneingang, Picken, Packen, Versand, CSV, Etiketten ...) funktionieren
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — wie das System aufgebaut ist, wo neue Features hingehören
- **[API.md](API.md)** — Endpunkte, Rollen, Fehlerformat
- **[TODO.md](../TODO.md)** — Roadmap mit offenen Punkten

---

## Troubleshooting

Die häufigsten Probleme (Passwort verloren, Konto gesperrt, Port belegt, Docker, CORS, HTTPS-Schleifen, Kamera-Scan, Backup/Restore, CSV-Import) mit Lösungen stehen in [TROUBLESHOOTING.md](TROUBLESHOOTING.md). Logs liegen unter `logs/lager-<Datum>.log` (Schlüssel `Logging:Directory`; relativ zum Arbeitsverzeichnis, bei `dotnet run --project src/Lager.Api` also `src/Lager.Api/logs/`; Docker: `/data/logs`) mit Korrelations-ID je Anfrage.
