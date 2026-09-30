# Konfigurationsreferenz

Alle Einstellungen, die das Backend (`src/Lager.Api`) liest: Schlüssel, Umgebungsvariable, Standardwert und Wirkung. Die Werte kommen aus `appsettings.json`, `appsettings.<Umgebung>.json` und Umgebungsvariablen (spätere Quellen überschreiben frühere). Für Produktion gehören Geheimnisse ausschließlich in Umgebungsvariablen oder einen Secret-Store, nie in Dateien im Repository. Die Schritt-für-Schritt-Anleitung steht in [GETTING_STARTED.md](GETTING_STARTED.md#7-production-setup-kurz), Fehlersuche in [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

---

## Inhalt

1. [Schreibweise der Umgebungsvariablen](#schreibweise-der-umgebungsvariablen)
2. [Umgebung und Adressen](#umgebung-und-adressen)
3. [Datenbank](#datenbank)
4. [JWT und Anmeldung](#jwt-und-anmeldung)
5. [CORS und erlaubte Hosts](#cors-und-erlaubte-hosts)
6. [HTTPS, Reverse-Proxy und Rate-Limiting](#https-reverse-proxy-und-rate-limiting)
7. [Backup und Restore](#backup-und-restore)
8. [Demo-Modus und Demo-Daten](#demo-modus-und-demo-daten)
9. [Logging](#logging)
10. [Frontend-Auslieferung und Entwicklungsserver](#frontend-auslieferung-und-entwicklungsserver)
11. [Health-Endpunkte](#health-endpunkte)
12. [Docker](#docker)
13. [Beispiele](#beispiele)

---

## Schreibweise der Umgebungsvariablen

ASP.NET Core bildet Konfigurationsschlüssel auf Umgebungsvariablen ab: der Doppelpunkt wird zu einem doppelten Unterstrich, die Groß-/Kleinschreibung ist egal.

| Konfigurationsschlüssel | Umgebungsvariable |
|---|---|
| `Jwt:SigningKey` | `Jwt__SigningKey` |
| `Security:ForwardedHeaders:Enabled` | `Security__ForwardedHeaders__Enabled` |
| `Cors:AllowedOrigins` (Array, erster Eintrag) | `Cors__AllowedOrigins__0` |
| `Cors:AllowedOrigins` (zweiter Eintrag) | `Cors__AllowedOrigins__1` |

Arrays werden per Index gesetzt (`__0`, `__1`, ...). Der Standard in `appsettings.json` hat genau einen Eintrag (Index 0): `Cors__AllowedOrigins__0` ersetzt ihn, `__1` fügt einen weiteren hinzu.

---

## Umgebung und Adressen

| Schlüssel / Variable | Standard | Pflicht | Wirkung |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (ohne Angabe; das Docker-Image setzt es ausdrücklich); `Development` über `Properties/launchSettings.json` bei `dotnet run` | nein | Entscheidet über Swagger (Standard: nur `Development`; `Swagger:Enabled` überschreibt es, siehe unten), den Demo-Modus (in `Production` nur mit Freigabe, siehe [Demo-Modus](#demo-modus-und-demo-daten)), Reseed/Bulk-Seed (nur `Development`), Restore (siehe [Backup](#backup-und-restore)), den flüchtigen JWT-Key (nur `Development`) und die Detailtiefe von Fehlerantworten (Stacktrace nur in `Development`). |
| `ASPNETCORE_URLS` | ohne Angabe `http://localhost:5000`; `dotnet run` nimmt `http://localhost:5099` aus `launchSettings.json` | nein | Adressen, auf denen Kestrel lauscht, z. B. `http://127.0.0.1:5099`. Eine `https://`-Adresse darin liefert auch den Port für die HTTPS-Weiterleitung. |
| `ASPNETCORE_HTTPS_PORT` (auch `HTTPS_PORT`) | – | nein | HTTPS-Port für die Weiterleitung (siehe `Security:HttpsPort`). |

`dotnet run --project src/Lager.Api` startet mit dem Profil `http` aus `launchSettings.json` (Umgebung `Development`, Adresse `http://localhost:5099`). Ein veröffentlichtes Programm (`dotnet publish`) kennt `launchSettings.json` nicht und läuft ohne Angabe als `Production`.

**Swagger** (Swagger UI unter `/swagger`, OpenAPI-Beschreibung unter `/swagger/v1/swagger.json`) läuft in der Umgebung `Development` oder mit `Swagger:Enabled=true`; in jeder anderen Umgebung (auch im Docker-Image) ist es ohne Schalter nicht vorhanden:

| Schlüssel | Umgebungsvariable | Standard | Pflicht | Wirkung |
|---|---|---|---|---|
| `Swagger:Enabled` | `Swagger__Enabled` | nicht gesetzt: an in `Development`, sonst aus | nein | `true` schaltet Swagger UI und die OpenAPI-Beschreibung auch außerhalb von `Development` ein, `false` schaltet sie auch in `Development` ab. Wird beim Start gelesen (Neustart nötig). Die Beschreibung ist **ohne Anmeldung lesbar** (sie enthält keine Daten, die Endpunkte bleiben geschützt): in einer erreichbaren Produktionsumgebung nur bewusst einschalten. Einzelheiten: [features/openapi.md](features/openapi.md). |

In Docker reicht die `docker-compose.yml` `Swagger__Enabled` nicht aus der `.env` durch (siehe [Docker](#docker)): zum Einschalten `Swagger__Enabled: "true"` in den Block `environment` eintragen.

---

## Datenbank

| Schlüssel | Umgebungsvariable | Standard | Pflicht | Wirkung |
|---|---|---|---|---|
| `Database:Provider` | `Database__Provider` | `Sqlite` | nein | `Sqlite` oder `MySql` (Groß-/Kleinschreibung egal). Jeder andere Wert bricht den Start ab. |
| `Database:ConnectionString` | `Database__ConnectionString` | `Data Source=lager.db` | ja | SQLite: Dateipfad. Ein **relativer** Pfad gilt relativ zum Content-Root der Anwendung (das ist das Arbeitsverzeichnis beim Start; bei `dotnet run` das Projektverzeichnis `src/Lager.Api`). Für Dienst/Container einen absoluten Pfad auf ein dauerhaftes Laufwerk setzen, sonst entsteht bei einem anderen Arbeitsverzeichnis eine neue, leere Datenbank samt neuem Bootstrap-Admin. MySQL: Pomelo-Verbindungszeichenfolge, siehe [GETTING_STARTED.md](GETTING_STARTED.md#6-auf-mysql-umschalten-optional). |
| `Database:MySqlServerVersion` | `Database__MySqlServerVersion` | leer (automatisch erkennen) | nein | Nur MySQL. Feste Serverversion, z. B. `8.0.36` oder `10.11.0-mariadb`. Ohne Angabe fragt die Anwendung die Version einmalig beim Server ab; der Server muss dann beim Start erreichbar sein. |
| `Database:Seed` | `Database__Seed` | `false` (`true` in `appsettings.Development.json`) | nein | **Veraltet.** Legt beim Start nur den kleinen Altbestand-Datensatz an (20 Artikel, keine Historie), aber nur in eine **leere** Datenbank, und warnt bei jedem Start im Log. Der vollständige Demo-Datensatz kommt aus `Demo:Enabled` (siehe [Demo-Modus](#demo-modus-und-demo-daten)). Für den Produktivbetrieb `false`. |

Die Datenbank wird beim Start angelegt bzw. auf den aktuellen Stand gebracht (`EnsureCreated` + `SchemaUpgrader`, keine EF-Migrationen, siehe [ARCHITECTURE.md](ARCHITECTURE.md#schema-evolution)). SQLite läuft im WAL-Modus; neben `lager.db` liegen daher `lager.db-wal` und `lager.db-shm`.

---

## JWT und Anmeldung

Den Signing-Key liefert **nicht** die `appsettings.json`. Er wird beim Start in dieser Reihenfolge aufgelöst:

1. `Jwt:SigningKey` (Umgebungsvariable oder Secret-Store),
2. `Jwt:KeyFile` (Datei; wird beim ersten Start mit einem Zufallskey angelegt und danach wiederverwendet),
3. nur `Development`: ein flüchtiger Zufallskey (alle Tokens verfallen beim Neustart).

Ohne Key startet die API außerhalb von `Development` nicht (Fehlermeldung nennt beide Wege). Keys unter 32 Bytes und der frühere Beispiel-Key aus dem Repository (Präfix `DEV-ONLY`) werden in **jeder** Umgebung abgelehnt.

| Schlüssel | Umgebungsvariable | Standard | Pflicht | Wirkung |
|---|---|---|---|---|
| `Jwt:SigningKey` | `Jwt__SigningKey` | – | in Produktion einer von beiden (`SigningKey` oder `KeyFile`) | Zufälliger Schlüssel, mindestens 32 Bytes (UTF-8), z. B. `openssl rand -base64 48`. |
| `Jwt:KeyFile` | `Jwt__KeyFile` | – | s. o. | Pfad einer Schlüsseldatei (absolut oder relativ zum Content-Root). Wird beim ersten Start mit 64 Zufallsbytes (Base64) angelegt, unter Unix nur für den Besitzer lesbar. Sinnvoll auf einem Volume, damit der Key Neustarts überlebt. Die Datei ist ein Geheimnis. |
| `Jwt:Issuer` | `Jwt__Issuer` | `Lager` | nein | Aussteller im Token; wird beim Prüfen verlangt. |
| `Jwt:Audience` | `Jwt__Audience` | `Lager` | nein | Zielgruppe im Token; wird beim Prüfen verlangt. |
| `Jwt:LifetimeMinutes` | `Jwt__LifetimeMinutes` | `480` (8 Stunden) | nein | Gültigkeit eines Tokens. Toleranz bei der Prüfung: 30 Sekunden. |
| `Auth:BootstrapAdminUsername` | `Auth__BootstrapAdminUsername` | `admin` | nein | Benutzername des ersten Administrators (wird kleingeschrieben gespeichert). |
| `Auth:BootstrapAdminPassword` | `Auth__BootstrapAdminPassword` | – (kein Standardpasswort) | nein | Passwort des ersten Administrators. Ohne Angabe erzeugt die Anwendung ein Zufalls-Einmalpasswort und gibt es **einmalig auf der Konsole (stdout)** aus, nicht in die Logdatei. Ein gesetztes Passwort muss die Passwortregeln erfüllen (mindestens 10 Zeichen, höchstens 72 Bytes, nicht gleich dem Benutzernamen), sonst bricht der Start ab. Nie in eine Datei im Repository schreiben. |
| `Auth:MaxFailedAttempts` | `Auth__MaxFailedAttempts` | `5` | nein | Fehlversuche bis zur Kontosperre. Ganze Zahl >= 1, sonst Startabbruch. |
| `Auth:LockoutMinutes` | `Auth__LockoutMinutes` | `15` | nein | Dauer der Kontosperre in Minuten. Ganze Zahl >= 1. |

Der Bootstrap-Admin entsteht **nur**, wenn die Tabelle `Users` leer ist (erster Start bzw. Wiederherstellung, siehe [TROUBLESHOOTING.md](TROUBLESHOOTING.md#passwort-verloren-oder-kein-admin-mehr)). Er muss sein Passwort beim ersten Login ändern. Wer früher mit dem inzwischen entfernten öffentlichen Standardpasswort gestartet ist, hat in seiner **bestehenden** Datenbank weiterhin einen Admin mit diesem Passwort: sofort ändern.

---

## CORS und erlaubte Hosts

| Schlüssel | Umgebungsvariable | Standard | Pflicht | Wirkung |
|---|---|---|---|---|
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`, ... | `["http://localhost:5173"]` | in Produktion ja, wenn Frontend und API verschiedene Origins haben | Erlaubte Browser-Origins (Schema, Host, Port, ohne Pfad und Schrägstrich am Ende). Der Sonderwert `*` als einziger Eintrag erlaubt jede Origin (ohne Cookies/Credentials); außerhalb von `Development` schreibt die Anwendung dazu eine Warnung. Liefert ein Reverse-Proxy Frontend und `/api` unter derselben Origin aus, ist CORS nicht nötig. |
| `AllowedHosts` | `AllowedHosts` | `localhost;127.0.0.1;[::1]` | in Produktion ja (eigener Hostname) | Host-Header-Filter des Servers, Werte mit `;` getrennt. Anfragen mit einem anderen Host-Header beantwortet ASP.NET Core mit **400**. Für den Betrieb hinter einem Proxy oder im LAN den eigenen Hostnamen eintragen (z. B. `lager.example.com`); `*` erlaubt jeden Host. |

---

## HTTPS, Reverse-Proxy und Rate-Limiting

| Schlüssel | Umgebungsvariable | Standard | Pflicht | Wirkung |
|---|---|---|---|---|
| `Security:RequireHttps` | `Security__RequireHttps` | `false` | nein | Schaltet HSTS und die Weiterleitung auf HTTPS ein. Die Weiterleitung braucht einen ermittelbaren HTTPS-Port (`Security:HttpsPort`, `ASPNETCORE_HTTPS_PORT` oder eine `https://`-Adresse in `ASPNETCORE_URLS`); ohne Port bleibt sie aus und die Anwendung warnt im Log. **Hinter einem TLS-terminierenden Reverse-Proxy auf `false` lassen**: der Proxy leitet um. |
| `Security:HttpsPort` | `Security__HttpsPort` | – | nein | Port für die HTTPS-Weiterleitung (1 bis 65535). |
| `Security:ForwardedHeaders:Enabled` | `Security__ForwardedHeaders__Enabled` | `false` | nein | Auswertung von `X-Forwarded-For` und `X-Forwarded-Proto`. Nur einschalten, wenn die API hinter einem Reverse-Proxy läuft; sonst könnte jeder Client seine IP fälschen. Ohne Listen (unten) vertraut die Anwendung nur Proxys auf dem Loopback-Interface. |
| `Security:ForwardedHeaders:KnownProxies` | `Security__ForwardedHeaders__KnownProxies__0`, ... | leer | nein | IP-Adressen vertrauenswürdiger Proxys. Sobald eine der beiden Listen gefüllt ist, gelten **nur** diese Einträge. |
| `Security:ForwardedHeaders:KnownNetworks` | `Security__ForwardedHeaders__KnownNetworks__0`, ... | leer | nein | Vertrauenswürdige Netze in CIDR-Schreibweise, z. B. `10.0.0.0/8`. |
| `Security:RateLimiting:Enabled` | `Security__RateLimiting__Enabled` | `true` (aus im Environment `Testing`) | nein | Schaltet das Rate-Limiting ein oder aus. Bei Überschreitung antwortet die API mit **429** und dem Header `Retry-After`. |
| `Security:LoginRateLimitPerMinute` | `Security__LoginRateLimitPerMinute` | `10` | nein | Anfragen an `POST /api/auth/login` je Client-IP und Minute. Ganze Zahl >= 1. |
| `Security:GlobalRateLimitPerMinute` | `Security__GlobalRateLimitPerMinute` | `600` | nein | Grobes Limit für alle Anfragen je Client-IP und Minute. Ganze Zahl >= 1. |

Ungültige Zahlen (0, negativ, Text) bei `Auth:*` und `Security:*` brechen den Start mit einer klaren Meldung ab, statt still auf einen Standard zu fallen. Unabhängig von der Konfiguration setzt jede Antwort Sicherheits-Header (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, eine restriktive `Content-Security-Policy`; Swagger ist von der CSP ausgenommen).

---

## Backup und Restore

| Schlüssel | Umgebungsvariable | Standard | Pflicht | Wirkung |
|---|---|---|---|---|
| `Backup:AllowRestore` | `Backup__AllowRestore` | `false` | nein | Erlaubt `POST /api/admin/restore` (und den Restore in der Oberfläche) **außerhalb** von `Development`. In `Development` ist der Restore immer erlaubt. Ohne dieses Flag antwortet der Endpunkt in Produktion mit 403 `restore_disabled`. Nur für die Dauer des Restores setzen. |
| `Backup:Directory` | `Backup__Directory` | Verzeichnis der SQLite-Datei | nein | Zielverzeichnis für Backups und die Sicherheitskopie vor einem Restore. Ein relativer Pfad gilt relativ zum Content-Root. Docker: `/data/backups` auf dem Volume. |
| `Backup:Schedule` | `Backup__Schedule` | leer (kein Zeitplan) | nein | Tägliche Startzeit des automatischen Backups als `HH:mm` in **UTC**, z. B. `02:00` (im deutschen Winter 03:00 Uhr, im Sommer 04:00 Uhr). Ein ungültiger Wert wird im Log gewarnt und wie "aus" behandelt. Ein verpasster Lauf wird nicht nachgeholt. |
| `Backup:RetentionCount` | `Backup__RetentionCount` | `14` | nein | So viele Backups (`lager-backup-…`) bleiben nach jedem neuen Backup erhalten, ältere werden gelöscht. `0` oder weniger = nie automatisch löschen. Die Sicherheitskopien vor einem Restore (`lager-before-restore-…`) zählen nicht mit und werden nie automatisch gelöscht. |

Backup und Restore gibt es nur für SQLite, in der Oberfläche unter **System > Backup & Restore** (Rolle Admin) und per API (`/api/admin/backups`, `/api/admin/backup-settings`, `/api/admin/restore`, siehe [API.md](API.md#system)). Die Seite zeigt die Werte dieser Tabelle nur an, sie ändert sie nicht. In Docker reicht die `docker-compose.yml` `Backup__Schedule` und `Backup__RetentionCount` aus der `.env` durch (z. B. `Backup__Schedule=02:00`). Änderungen an `appsettings*.json` wirken ohne Neustart (die Einstellungen werden bei jedem Zugriff frisch gelesen), Umgebungsvariablen erst mit einem Neustart. Backups sind **unverschlüsselt** und enthalten alle Daten samt Passwort-Hashes. Für MySQL `mysqldump` verwenden (der Zeitplan läuft dort nicht). Ablauf und Regeln: [GETTING_STARTED.md](GETTING_STARTED.md#backup-und-restore), Bedienung: [USAGE.md](USAGE.md#backup-und-restore), Einzelheiten: [features/backup-restore.md](features/backup-restore.md).

---

## Demo-Modus und Demo-Daten

| Schlüssel | Umgebungsvariable | Standard | Wirkung |
|---|---|---|---|
| `Demo:Enabled` | `Demo__Enabled` | `false` | Legt beim Start den **Demo-Datensatz** an: einen erfundenen, realistischen Betrieb mit rund 60 Tagen Historie und Demo-Benutzern. Nur in eine **leere** Datenbank (keine Artikel, Lager, Lagerplätze, Bestände, Bestellungen, Kunden, Lieferanten; Benutzer zählen nicht); bestehende Daten werden nie ergänzt oder repariert. Alles in einer Transaktion. |
| `Demo:AllowInProduction` | `Demo__AllowInProduction` | `false` | In `Production` bricht der Start mit `Demo:Enabled=true` mit einer klaren Meldung ab, solange dieser Schalter nicht `true` ist. In anderen Umgebungen ist keine Freigabe nötig. Das Docker-Image läuft als `Production` und braucht ihn deshalb (in Docker zusammen mit `Demo__Enabled` in der `.env` setzen, beide reicht die `docker-compose.yml` durch). |
| `Database:Seed` | `Database__Seed` | `false` (`true` in `appsettings.Development.json`) | **Veraltet.** Nur der kleine Altbestand-Datensatz (20 Artikel, 25 Bestandszeilen ohne Ledger, 8 Bestellungen `ORD-DEMO-01` bis `ORD-DEMO-08`, 5 Wände, 3 Pickpunkte; ohne Kunden, Lieferanten und Historie), mit Warnung bei jedem Start. Bei aktivem `Demo:Enabled` wird er ignoriert. In `Production` legt er nur mit `Demo:AllowInProduction=true` an. |

**Kurzform:** Die Umgebungsvariable `LAGER_DEMO` setzt `Demo:Enabled`: `1`, `true`, `yes` oder `on` schaltet ein, `0`, `false`, `no` oder `off` aus (schlägt die Konfigurationsdateien; leer oder andere Werte ändern nichts). Beispiel: `LAGER_DEMO=1 dotnet run --project src/Lager.Api`.

Die Zugangsdaten der Demo-Benutzer (`manager`, `picker`, `picker2`, `picker3`, `packer`, `receiver`, `viewer`) sind zufällig, stehen **einmalig** in der Konsole (Docker: im Container-Log) und nie in der Logdatei; feste Passwörter gibt es nicht. Umfang und Entstehung des Datensatzes: [features/demo-modus.md](features/demo-modus.md), Bedienung und Docker: [GETTING_STARTED.md](GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden).


---

## Logging

Das Logging läuft über Serilog (`LagerLogging` in `src/Lager.Api/Program.cs`). Die Grundeinstellungen stehen im Code, Verzeichnis, Aufbewahrung und Log-Level lassen sich ohne Neubau ändern:

| Schlüssel | Umgebungsvariable | Standard | Wirkung |
|---|---|---|---|
| `Logging:Directory` | `Logging__Directory` | `logs` | Verzeichnis der Logdateien `lager-<JJJJMMTT>.log`. Ein relativer Pfad gilt relativ zum **Arbeitsverzeichnis** des Prozesses (bei einem Dienst ein festes `WorkingDirectory` setzen oder einen absoluten Pfad angeben). Das Verzeichnis wird bei Bedarf angelegt. |
| `Logging:RetainedFileCount` | `Logging__RetainedFileCount` | `14` | So viele Tagesdateien bleiben erhalten, ältere werden gelöscht. Ganze Zahl >= 1, sonst Startabbruch. |
| `Serilog:MinimumLevel:Default` | `Serilog__MinimumLevel__Default` | `Information` | Mindest-Level (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`). |
| `Serilog:MinimumLevel:Override:<Namespace>` | `Serilog__MinimumLevel__Override__Microsoft.AspNetCore` | `Microsoft.AspNetCore` und `Microsoft.EntityFrameworkCore`: `Warning` | Mindest-Level je Namespace. Was im Abschnitt `Serilog` steht, schlägt die Vorgaben im Code (`ReadFrom.Configuration`), auch weitere Senken unter `Serilog:WriteTo`. |

- Ausgabe auf die Konsole und in die Tagesdatei; die Datei rollt täglich.
- Der Abschnitt `Logging:LogLevel` (der aus der Standardvorlage von ASP.NET Core; er steht in keiner mitgelieferten Konfigurationsdatei mehr) hat **keine Wirkung**: Serilog ersetzt die Microsoft-Logging-Konfiguration. Das Level stellt man im Abschnitt `Serilog` ein; `appsettings.Development.json` tut das für die Entwicklung (`Serilog:MinimumLevel`).
- Die **Health-Probes** (`/health/live`, `/health/ready`; der Compose-Healthcheck ruft sie alle 30 Sekunden auf) erscheinen im Request-Log nur auf Level `Debug`, damit sie das Log nicht füllen; eine fehlgeschlagene Probe (5xx) ist eine Warnung. Wer sie sehen will, setzt `Serilog__MinimumLevel__Default=Debug`.
- Die einmaligen **Demo-Zugangsdaten** stehen nur in der Konsole, nie in der Logdatei.
- Beim Start schreibt die Anwendung Umgebung, Datenbank-Art (`Sqlite` oder `MySql`, nie den Verbindungsstring), die SQLite-Datei, das Log-Verzeichnis und ob ein Frontend ausgeliefert wird.
- Jede Anfrage trägt eine Korrelations-ID (Header `X-Correlation-Id`, wird übernommen, wenn sie aus 1 bis 64 Zeichen `A-Za-z0-9._-` besteht, sonst neu erzeugt). Dieselbe ID steht in Fehlerantworten (`correlationId`) und in den Logzeilen.
- Fachliche Fehler (409, 404, 400) erscheinen ohne Stacktrace, unerwartete Fehler (500) als `Error` mit Korrelations-ID.
- Sicherheitsereignisse (Login, Fehlversuch, Sperre, Passwort- und Rollenänderung, abgelehnte Tokens) erscheinen mit dem Kontext `SecurityAudit`; Passwörter und Hashes stehen nie im Log. Das Einmalpasswort des Bootstrap-Admins steht **nicht** im Log, nur auf der Konsole.

---

## Frontend-Auslieferung und Entwicklungsserver

**Auslieferung durch das Backend.** Liegt im Web-Root der Anwendung (`wwwroot` im Arbeitsverzeichnis, bei einer veröffentlichten Anwendung also neben `Lager.Api.dll`) eine `index.html`, liefert das Backend das gebaute Frontend selbst aus: ein Prozess, ein Port, eine Origin (das Frontend ruft `/api` mit relativen Pfaden auf, CORS ist dann nicht nötig). Einen eigenen Schlüssel dafür gibt es nicht; die Auslieferung ist an, sobald die Datei da ist.

- Ohne `wwwroot/index.html` bleibt das Backend eine reine API. Das ist der Normalfall bei `dotnet run` und bei `dotnet publish`: **`dotnet publish` kopiert das Frontend nicht nach `wwwroot`**, das Frontend muss extra gebaut (`npm run build` in `frontend/lager-ui`, Ausgabe `dist/`) und dort abgelegt oder von einem Webserver ausgeliefert werden (siehe [GETTING_STARTED.md](GETTING_STARTED.md#7-production-setup-kurz)).
- Pfade ohne Dateiendung (Deep-Links wie `/orders`, ein Neuladen der Seite) liefern `index.html` (SPA-Fallback). Ausgenommen sind `/api`, `/health` und `/swagger`: ein unbekannter API-Pfad bleibt ein 404 als JSON. Ein fehlendes `/assets/x.js` bleibt ebenfalls ein 404.
- Cache-Regeln: die gehashten Dateien unter `/assets/` gelten ein Jahr als unveränderlich (`public, max-age=31536000, immutable`); `index.html`, `sw.js`, Manifest und Icons haben `Cache-Control: no-cache` (Rückfrage vor jeder Nutzung), damit nach einem Update nicht die alte Oberfläche oder der alte Service-Worker aktiv bleibt.
- Statische Dateien sind öffentlich (kein Login), zählen nicht auf das Rate-Limit und tragen die Sicherheits-Header. Antworten werden komprimiert (Brotli/Gzip).
- Beim Start steht im Log, ob ein Frontend ausgeliefert wird; liegt ein `wwwroot` ohne `index.html` vor, gibt es eine Warnung.

**Entwicklungsserver (Vite).**

| Variable | Standard | Wirkung |
|---|---|---|
| `VITE_API_TARGET` | `http://localhost:5099` | Ziel des Vite-Dev-Proxys für `/api` (`npm run dev`), z. B. `VITE_API_TARGET=http://192.168.1.20:5099`. Auch über `frontend/lager-ui/.env.local`. Gilt nur für den Entwicklungsserver. |

Das gebaute Frontend (`npm run build`, Ausgabe `frontend/lager-ui/dist/`) enthält keine Konfiguration: es ruft immer relative `/api`-Pfade auf. Wer es nicht über das Backend ausliefert, muss `/api` an das Backend weiterleiten.

---

## Health-Endpunkte

Für Reverse-Proxys, Monitoring und Container-Healthchecks gibt es zwei anonyme Endpunkte ohne Login und ohne Konfiguration:

| Pfad | Prüft | Antwort |
|---|---|---|
| `GET /health/live` | nur, dass der Prozess Anfragen beantwortet | immer `200` mit `{"status":"Healthy"}` |
| `GET /health/ready` | ob die Datenbank erreichbar ist (Verbindungsprüfung mit 3 Sekunden Timeout) | `200` mit `{"status":"Healthy"}`, sonst `503` mit `{"status":"Unhealthy"}` |

Die Antwort enthält nur den Status, keine Details zu Datenbank oder Pfaden (die Ursache steht im Log). Der Host-Header-Filter (`AllowedHosts`) gilt auch hier: ein Healthcheck von außen braucht einen erlaubten Host (`localhost` genügt für Prüfungen im selben Container bzw. auf demselben Rechner).

Das Docker-Image und die `docker-compose.yml` nutzen `/health/ready` für den Healthcheck (siehe [Docker](#docker)).

---

## Docker

Im Wurzelverzeichnis des Repositorys liegen `Dockerfile`, `docker-compose.yml`, `docker-compose.mysql.yml`, `.env.example` und `deploy/Caddyfile`. Die Schritt-für-Schritt-Anleitung steht unter [GETTING_STARTED.md](GETTING_STARTED.md#schnellstart-mit-docker); hier die Einstellungen. Alle Schlüssel sind dieselben wie oben, in der Schreibweise mit `__`.

**Vorgaben des Images** (`ENV` im `Dockerfile`; jede per Umgebungsvariable überschreibbar):

| Umgebungsvariable | Wert im Image | Wirkung |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | kein Swagger (außer mit `Swagger__Enabled=true`), kein Reseed, Restore gesperrt, Demo-Modus nur mit Freigabe |
| `ASPNETCORE_URLS` | `http://+:8080` | Adresse im Container; Compose veröffentlicht sie als Port 8080 |
| `Database__ConnectionString` | `Data Source=/data/lager.db` | SQLite-Datei auf dem Volume `/data` (MySQL: siehe `docker-compose.mysql.yml`) |
| `Jwt__KeyFile` | `/data/jwt.key` | der Schlüssel wird beim ersten Start erzeugt und hier abgelegt; `Jwt__SigningKey` hat Vorrang |
| `Logging__Directory` | `/data/logs` | Logdateien auf dem Volume |
| `Backup__Directory` | `/data/backups` | Backups auf dem Volume (unverschlüsselt, mit Passwort-Hashes) |

Das gebaute Frontend liegt im Image in `wwwroot`: das Backend liefert die Oberfläche selbst aus (siehe [Frontend-Auslieferung](#frontend-auslieferung-und-entwicklungsserver)). Das Image läuft unter einem unprivilegierten Benutzer (ID 1654); ein benanntes Volume bekommt die Rechte selbst, ein Bind-Mount muss für diese ID beschreibbar sein. Der Healthcheck ruft `GET /health/ready`: `localhost` muss in `AllowedHosts` erlaubt bleiben (die Compose-Datei hängt es immer an).

**Was die `docker-compose.yml` an den Container durchreicht.** Nur diese Einstellungen gelangen aus der `.env` (oder der Shell) in den Container, bewusst keine ganze Datei, damit z. B. das Root-Passwort der Datenbank nie im Lager-Container landet:

| Variable (in `.env`) | Standard | Wirkung |
|---|---|---|
| `AllowedHosts` | `localhost` (`localhost;127.0.0.1` werden immer ergänzt) | erlaubte Host-Namen, siehe [CORS und erlaubte Hosts](#cors-und-erlaubte-hosts) |
| `Security__ForwardedHeaders__Enabled` | `false` | Proxy-Header auswerten (nötig hinter Caddy) |
| `Security__ForwardedHeaders__KnownNetworks__0` | `172.16.0.0/12` | vertrauenswürdiges Netz (Docker-Standardnetze) |
| `Auth__BootstrapAdminPassword`, `Jwt__SigningKey`, `Cors__AllowedOrigins__0`, `Backup__AllowRestore`, `Database__Seed`, `Serilog__MinimumLevel__Default`, `Logging__RetainedFileCount` | aus dem Image bzw. den Standardwerten | nur wenn in `.env` gesetzt |
| `Backup__Schedule`, `Backup__RetentionCount` | leer (kein Zeitplan), Aufbewahrung 14 | automatisches Backup, siehe [Backup und Restore](#backup-und-restore); ohne Wert in der `.env` gilt der Standard der Anwendung |
| `Demo__Enabled`, `Demo__AllowInProduction` | aus | Demo-Modus, siehe [Demo-Modus und Demo-Daten](#demo-modus-und-demo-daten); das Image läuft als `Production`, dort braucht er **beide** Schalter (Beispiel: [Demo-Modus in Docker](GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden)) |

Jede weitere Einstellung (`Swagger__Enabled`, `Security__RateLimiting__Enabled` ...) steht **nicht** auf dieser Liste: sie gehört in den Block `environment` der `docker-compose.yml` oder in eine Zusatzdatei, die Compose mit `-f` zusammenführt.

**Nur für Compose** (keine Anwendungseinstellungen):

| Variable (in `.env`) | Standard | Wirkung |
|---|---|---|
| `LAGER_PORT` | `8080` | Port auf dem Docker-Host; `127.0.0.1:8080` bindet nur an den eigenen Rechner |
| `LAGER_DOMAIN` | `localhost` | Name für das Caddy-Profil `https` (`deploy/Caddyfile`) |
| `LAGER_DB_PASSWORD`, `LAGER_DB_ROOT_PASSWORD` | – (Pflicht bei der MySQL-Zusatzdatei) | Passwörter der MySQL-Variante; in einem Verbindungsstring, deshalb ohne `;` und `=` |

Volumes: `lager-data` (Anwendungsdaten), bei MySQL `mysql-data`, beim Profil `https` `caddy-data` und `caddy-config`. `docker compose down -v` löscht sie alle.

---

## Beispiele

**PowerShell (aktuelle Sitzung):**

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5099"
$env:Jwt__SigningKey = "<mindestens-32-Zeichen-Zufall>"
$env:Database__ConnectionString = "Data Source=D:\lager-daten\lager.db"
$env:AllowedHosts = "lager.example.com"
$env:Cors__AllowedOrigins__0 = "https://lager.example.com"
dotnet run --project src/Lager.Api --no-launch-profile
```

**Bash:**

```bash
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS=http://127.0.0.1:5099
export Jwt__SigningKey="$(openssl rand -base64 48)"
export Database__ConnectionString="Data Source=/var/lib/lager/lager.db"
export AllowedHosts=lager.example.com
export Security__ForwardedHeaders__Enabled=true
dotnet Lager.Api.dll
```

**systemd (`/etc/systemd/system/lager.service`, Auszug):**

```ini
[Service]
WorkingDirectory=/opt/lager
ExecStart=/usr/bin/dotnet /opt/lager/Lager.Api.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5099
Environment=Database__ConnectionString=Data Source=/var/lib/lager/lager.db
Environment=Jwt__KeyFile=/var/lib/lager/jwt.key
Environment=AllowedHosts=lager.example.com
Environment=Security__ForwardedHeaders__Enabled=true
```

Den JWT-Schlüssel und ein Bootstrap-Passwort besser über `EnvironmentFile=` (Datei mit Rechten `0600`) als direkt in die Unit-Datei schreiben.
