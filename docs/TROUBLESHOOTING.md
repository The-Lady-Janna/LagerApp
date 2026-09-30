# Fehlersuche

Häufige Probleme beim Betrieb, mit Ursache und Lösung. Erst die Logdatei ansehen (`logs/lager-<Datum>.log`, Verzeichnis über `Logging:Directory` einstellbar, siehe [CONFIGURATION.md](CONFIGURATION.md#logging)); jede Fehlerantwort der API nennt eine `correlationId`, mit der sich die passende Logzeile finden lässt. Einrichtung: [GETTING_STARTED.md](GETTING_STARTED.md), Bedienung: [USAGE.md](USAGE.md).

---

## Inhalt

1. [Anmeldung und Konten](#anmeldung-und-konten)
2. [Passwort verloren oder kein Admin mehr](#passwort-verloren-oder-kein-admin-mehr)
3. [Start schlägt fehl](#start-schlägt-fehl)
4. [Docker](#docker)
5. [Netzwerk, HTTPS und Reverse-Proxy](#netzwerk-https-und-reverse-proxy)
6. [Datenbank](#datenbank)
7. [MySQL](#mysql)
8. [Demo-Modus](#demo-modus)
9. [Backup und Restore](#backup-und-restore)
10. [CSV-Import und -Export](#csv-import-und--export)
11. [Oberfläche und Scanner](#oberfläche-und-scanner)
12. [Etiketten und Druck](#etiketten-und-druck)
13. [Fachliche Meldungen](#fachliche-meldungen)

---

## Anmeldung und Konten

| Symptom | Ursache und Lösung |
|---|---|
| Login meldet "Anmeldung fehlgeschlagen", obwohl das Passwort stimmt | Die Meldung ist bewusst für **jeden** Fehler gleich (unbekannter Benutzer, falsches Passwort, gesperrtes oder deaktiviertes Konto). Den Grund nennt nur das Sicherheits-Log: Zeilen `Security LoginFailed`, `Security LoginBlocked` oder `Security LoginLockedOut` (Kontext `SecurityAudit`). |
| Konto gesperrt | Nach 5 Fehlversuchen ist das Konto 15 Minuten gesperrt (`Auth:MaxFailedAttempts`, `Auth:LockoutMinutes`). Warten, oder als anderer Admin unter **Benutzer** das Passwort zurücksetzen (**PW reset**): das hebt die Sperre auf. Ist der einzige Admin gesperrt und ein Warten nicht möglich, hilft die [Wiederherstellung](#passwort-verloren-oder-kein-admin-mehr). |
| HTTP 429 beim Login oder allgemein | Rate-Limit je Client-IP (Login 10 pro Minute, gesamt 600 pro Minute). `Retry-After` abwarten. Läuft die API hinter einem Proxy ohne `Security:ForwardedHeaders:Enabled=true`, sieht sie alle Nutzer unter derselben Proxy-IP und das Limit trifft alle gemeinsam. |
| 401 auf einmal überall | Das Token ist abgelaufen (8 Stunden) oder wurde widerrufen: Das Konto wurde deaktiviert, das Passwort geändert bzw. zurückgesetzt oder die Rollen wurden geändert. Neu anmelden. Nach einem Neustart im Entwicklungsmodus ohne festen Key (`Development`, flüchtiger Key) sind alle Tokens ungültig. |
| 403 mit `password_change_required` | Das Konto muss sein Passwort ändern (erster Login des Bootstrap-Admins, Admin-Reset). Die Weboberfläche zeigt dafür einen Dialog; per API `POST /api/auth/change-password`, danach mit dem gelieferten neuen Token weiterarbeiten. |
| 403 "Keine Berechtigung" | Die Rolle reicht nicht für die Aktion. Die Rollenmatrix steht in [API.md](API.md#rollen); unter **Benutzer** (nur Admin) lassen sich Rollen ändern. |
| Das Einmalpasswort des ersten Starts ist weg | Es steht nur einmal auf der Konsole und nie in der Logdatei. Siehe unten. |

---

## Passwort verloren oder kein Admin mehr

Der Bootstrap-Admin entsteht ausschließlich, wenn die Tabelle `Users` **leer** ist. Ein eingebauter Notfallzugang oder ein Passwort-Reset per Kommandozeile existiert nicht. Wenn niemand mehr als Admin anmelden kann:

1. **Sicherheitskopie** der Datenbankdatei anlegen (bei SQLite die Datei `lager.db`, dazu `-wal`/`-shm`, mit gestoppter Anwendung kopieren).
2. Anwendung **stoppen**.
3. Alle Benutzer löschen. Das ist unkritisch für die übrigen Daten: der Audit-Trail speichert Benutzernamen als Text, keine Tabelle verweist per Fremdschlüssel auf `Users`. SQLite: `sqlite3 lager.db "DELETE FROM Users;"`. MySQL: `DELETE FROM Users;` im `mysql`-Client.
4. Optional ein eigenes Startpasswort setzen: `Auth__BootstrapAdminPassword` (mindestens 10 Zeichen) und gegebenenfalls `Auth__BootstrapAdminUsername`. Ohne Angabe erzeugt die Anwendung ein Einmalpasswort.
5. Anwendung **starten**. Sie legt einen neuen Admin an (`admin`) und gibt das Einmalpasswort einmalig auf der **Konsole** aus. Damit anmelden; die Anwendung verlangt sofort ein neues Passwort.
6. Danach die anderen Benutzer neu anlegen (**Benutzer** > **+ Neuer Benutzer**), alle Rollen und Konten sind mit dem Löschen verschwunden. Artikel, Bestand, Bestellungen und alle übrigen Daten bleiben unverändert.

Das gilt auch für den Fall, dass die Anwendung nur als Dienst ohne sichtbare Konsole läuft: dann vor dem Start `Auth__BootstrapAdminPassword` setzen, statt das Einmalpasswort zu suchen. Nach dem ersten Login das Passwort ändern und die Variable wieder entfernen.

**Docker:** Das Einmalpasswort steht im Container-Log (`docker compose logs lager`), aber nur beim allerersten Start mit einem **leeren** Volume. Gibt es das Volume schon (mit Benutzern), entsteht kein neues Passwort. Ist der Admin-Zugang weg, liegt die Datenbank im Volume `lager-data` unter `/data/lager.db`: das Vorgehen oben gilt entsprechend, die Datei muss dazu aus dem Volume bearbeitet werden (Container stoppen, das Volume in einem temporären Container einhängen; Dateirechte beachten: die Anwendung läuft mit der Benutzer-ID 1654). Dieser Weg ist nicht praktisch erprobt. Enthält das Volume noch keine Daten, die man behalten will, ist `docker compose down -v` mit anschließendem Neustart der einfachste Weg (**löscht alle Daten**).

---

## Start schlägt fehl

| Meldung (Auszug) | Ursache und Lösung |
|---|---|
| "Es ist kein JWT-Signing-Key konfiguriert" | Außerhalb von `Development` braucht die API einen Key: `Jwt__SigningKey` (mindestens 32 Bytes) **oder** `Jwt__KeyFile`. Siehe [CONFIGURATION.md](CONFIGURATION.md#jwt-und-anmeldung). |
| "Der JWT-Signing-Key ... ist zu kurz" | Weniger als 32 Bytes. Neuen Key erzeugen, z. B. `openssl rand -base64 48`. |
| "... ist der bekannte Entwicklungs-Key" | Der Wert beginnt mit `DEV-ONLY`: das war der frühere öffentliche Beispiel-Key. Einen eigenen Zufallswert setzen. |
| "Auth:BootstrapAdminPassword ist nicht zulässig" | Das gesetzte Startpasswort erfüllt die Regeln nicht (mindestens 10 Zeichen, höchstens 72 Bytes, nicht gleich dem Benutzernamen). Stärkeres Passwort setzen oder die Variable weglassen. |
| "Konfiguration ... muss eine ganze Zahl >= 1 sein" | Ein Wert von `Auth:*`, `Security:*` oder `Logging:RetainedFileCount` ist keine positive ganze Zahl. |
| "Unsupported Database:Provider" / "Missing Database:ConnectionString" | Provider nur `Sqlite` oder `MySql`; der Verbindungsstring ist Pflicht. |
| "Schema-Step ... ist fehlgeschlagen; die Datenbank konnte nicht aktualisiert werden" | Ein kritischer Schritt der Schema-Aktualisierung ist gescheitert; die Anwendung startet bewusst nicht auf einem halb aktualisierten Schema. Details in der Logdatei (`Kritischer Schema-Step`). Bei SQLite ist der Schritt zurückgerollt. Sicherheitskopie einspielen und den Fehler melden. |
| "Demo:Enabled (bzw. LAGER_DEMO=1) ist in der Umgebung Production nicht erlaubt" | Der Demo-Modus ist in `Production` nur mit ausdrücklicher Freigabe erlaubt. Demo-Modus ausschalten (`Demo__Enabled`/`LAGER_DEMO` entfernen) oder für eine Vorführung `Demo__AllowInProduction=true` setzen. Siehe [Demo-Modus](#demo-modus). |
| Adresse `5099`/`5173` schon belegt | Anderen Port wählen: Backend in `src/Lager.Api/Properties/launchSettings.json` (oder `ASPNETCORE_URLS`), Frontend-Port in `frontend/lager-ui/vite.config.ts`. Ändert sich der Backend-Port, muss `VITE_API_TARGET` bzw. das Proxy-Ziel in `vite.config.ts` mit. |

---

## Docker

Anleitung: [GETTING_STARTED.md](GETTING_STARTED.md#schnellstart-mit-docker). Erst `docker compose logs lager` ansehen: dort stehen Start-Meldungen, Fehler und beim ersten Start das Einmalpasswort.

| Symptom | Ursache und Lösung |
|---|---|
| `docker compose up --build` bricht ab oder findet den Docker-Dienst nicht | Docker (Desktop) läuft nicht, oder es fehlt die Verbindung zum Internet (Basis-Images, npm- und NuGet-Pakete). `docker compose version` und `docker compose config` prüfen (letzteres zeigt Fehler in den Compose-Dateien und **die aufgelösten Werte samt Passwörtern**: nicht weitergeben). Bei einem Fehler im Build steht der fehlgeschlagene Schritt (Frontend, API oder Laufzeit-Image) in der Ausgabe. |
| Der Container ist `unhealthy` oder startet immer wieder neu | `docker compose logs lager` lesen. `GET /health/ready` antwortet 503, wenn die Datenbank nicht erreichbar ist (MySQL noch nicht bereit, Volume nicht beschreibbar). `localhost` muss in `AllowedHosts` erlaubt bleiben, sonst antwortet auch der Healthcheck mit 400 (die Compose-Datei hängt es immer an). |
| Zugriff über die IP oder einen Namen im LAN: "Bad Request - Invalid Hostname" (400) | `AllowedHosts` in der `.env` fehlt der Name, z. B. `AllowedHosts=lager.example.com;192.168.1.20`, danach `docker compose up -d`. |
| Eine Einstellung in der `.env` wirkt nicht | Die Compose-Datei reicht nur eine feste Liste von Einstellungen durch (`AllowedHosts`, `Auth__BootstrapAdminPassword`, `Jwt__SigningKey`, `Cors__AllowedOrigins__0`, `Backup__AllowRestore`, `Backup__Schedule`, `Backup__RetentionCount`, `Demo__Enabled`, `Demo__AllowInProduction`, `Database__Seed`, Log-Level u. a.; Liste: [CONFIGURATION.md](CONFIGURATION.md#docker)). Alle anderen (z. B. `Swagger__Enabled`) gehören in den Block `environment` der `docker-compose.yml` oder in eine Zusatzdatei. Nach jeder Änderung `docker compose up -d` (ein bloßes `restart` liest die `.env` nicht neu). |
| Nach `docker compose down -v` sind die Daten weg | `-v` löscht die Volumes (Datenbank, Schlüssel, Logs, Backups). Ohne `-v` bleiben sie. Backups vorher aus dem Volume kopieren (Oberfläche: Herunterladen). |
| "Permission denied" für `/data` (Datenbank, `jwt.key`, Logs) | Bei einem Bind-Mount (ein Verzeichnis des Hosts statt des benannten Volumes) muss es für die Benutzer-ID 1654 beschreibbar sein. Ein benanntes Volume bekommt die Rechte selbst. |
| MySQL-Variante: "required variable LAGER_DB_PASSWORD is missing" | `LAGER_DB_PASSWORD` und `LAGER_DB_ROOT_PASSWORD` gehören in die `.env` (Pflicht bei `docker-compose.mysql.yml`). Zeichen wie `;` oder `=` in den Passwörtern vermeiden, sie landen in einem Verbindungsstring. |
| MySQL-Variante: Lager startet spät oder gar nicht | Lager wartet, bis der MySQL-Dienst `healthy` ist (beim ersten Start einige Dutzend Sekunden, Einrichtung der Datenbank). `docker compose logs mysql lager` zeigen, woran es hängt. |
| Caddy (Profil `https`): Zertifikat wird nicht ausgestellt oder der Browser warnt | Für einen öffentlichen Namen müssen die Ports 80 und 443 aus dem Internet erreichbar und `LAGER_DOMAIN` der echte Name sein. Für einen lokalen Namen ohne öffentliches DNS in `deploy/Caddyfile` `tls internal` einkommentieren und das Root-Zertifikat von Caddy (Volume `caddy-data`, `pki/authorities/local/root.crt`) auf den Geräten installieren. |
| Hinter Caddy: alle Nutzer teilen sich ein Rate-Limit, oder Weiterleitungsschleife | `Security__ForwardedHeaders__Enabled=true` in die `.env` (Client-IP aus `X-Forwarded-For`), `Security__RequireHttps` aus lassen. Siehe [Netzwerk](#netzwerk-https-und-reverse-proxy). |
| Lieferschein-PDF scheitert nur im Container | Die PDF-Erzeugung (QuestPDF/SkiaSharp) braucht unter Linux `fontconfig` und eine Schrift; das mitgelieferte Image bringt beides mit. Bei einem selbst gebauten Basis-Image nachinstallieren; die Ursache steht im Log. |
| Nach einem Restore läuft die App noch mit alten Daten | `docker compose restart` (oder `up -d`): nach einem Restore ist ein Neustart nötig. |

---

## Netzwerk, HTTPS und Reverse-Proxy

| Symptom | Ursache und Lösung |
|---|---|
| **HTTP 400**, HTML-Seite "Bad Request - Invalid Hostname" | Der Host-Header der Anfrage steht nicht in `AllowedHosts` (Standard `localhost;127.0.0.1;[::1]`). Den eigenen Hostnamen eintragen, z. B. `AllowedHosts=lager.example.com` (mehrere mit `;`). Das betrifft jeden Zugriff aus dem LAN oder über einen Proxy, der den Host-Header weiterreicht. |
| Browser meldet CORS-Fehler | Die Origin des Frontends steht nicht in `Cors:AllowedOrigins` (Schema, Host und Port genau wie im Browser, ohne `/` am Ende). Einfacher: Frontend und `/api` über denselben Proxy unter **einer** Adresse ausliefern, dann ist CORS nicht im Spiel. |
| Weiterleitungsschleife oder Endlosumleitung auf HTTPS | `Security:RequireHttps=true` hinter einem TLS-terminierenden Proxy: die API sieht nur HTTP und leitet jedes Mal um. Bei einem Proxy `RequireHttps=false` lassen, die Umleitung dem Proxy überlassen und `Security:ForwardedHeaders:Enabled=true` setzen (und bei Bedarf `KnownProxies`/`KnownNetworks`). |
| Log-Warnung "Security:RequireHttps ist aktiv, aber es ist kein HTTPS-Port ermittelbar" | Ohne Port bleibt die Weiterleitung aus. `Security:HttpsPort`, `ASPNETCORE_HTTPS_PORT` oder eine `https://`-Adresse in `ASPNETCORE_URLS` setzen. |
| Client-IP im Log und im Rate-Limit ist immer die des Proxys | `Security:ForwardedHeaders:Enabled=true` fehlt oder der Proxy steht nicht in `KnownProxies`/`KnownNetworks`. Ohne Listen vertraut die API nur Loopback-Proxys. |
| Swagger (`/swagger`) fehlt | Swagger läuft in der Umgebung `Development` oder mit `Swagger:Enabled=true`; `Swagger:Enabled=false` schaltet es auch in `Development` ab. In Produktion und im Docker-Image ist es ohne Schalter aus (404). Zum Einschalten `Swagger__Enabled=true` setzen (in Docker in den Block `environment` der `docker-compose.yml`, die `.env` reicht es nicht durch) und neu starten; die Beschreibung ist dann ohne Anmeldung lesbar. Siehe [API.md](API.md#swagger-und-openapi) und [features/openapi.md](features/openapi.md). |
| Frontend zeigt nach dem Deployment leere Seite oder 404 auf `/orders` | Entweder liefert ein eigener Webserver das Frontend aus: dann braucht er den SPA-Fallback (unbekannte Pfade auf `index.html`) **und** die Weiterleitung von `/api` an das Backend. Oder das Backend soll es ausliefern: dann muss `wwwroot/index.html` neben `Lager.Api.dll` liegen (`dotnet publish` kopiert das Frontend nicht) und die Anwendung im Verzeichnis der veröffentlichten Dateien gestartet werden. Beispiele: [GETTING_STARTED.md](GETTING_STARTED.md#frontend-ausliefern). |
| `/` liefert 401 oder 404 statt der Oberfläche | Das Backend hat kein `wwwroot/index.html` gefunden und läuft als reine API. Im Start-Log steht dazu "Kein Frontend in wwwroot" (bzw. eine Warnung, wenn `wwwroot` ohne `index.html` existiert). Meist wurde die Anwendung aus dem falschen Verzeichnis gestartet: Content-Root ist das **Arbeitsverzeichnis**, dort werden auch `appsettings.json` und `wwwroot` gesucht. |
| Die Standardwerte aus `appsettings.json` gelten nicht (z. B. kein Host-Filter, andere Ports) | Die Anwendung wurde nicht aus dem Verzeichnis der veröffentlichten Dateien gestartet, `appsettings.json` wurde nicht gefunden. Mit `cd publish` bzw. `WorkingDirectory=` im Dienst starten. |
| Unbekannter API-Pfad antwortet 401 statt 404 | Ohne Token liefert die geschlossene API für **jeden** nicht angemeldeten Aufruf 401, auch für Pfade, die es gar nicht gibt. Mit Token kommt das 404. |
| `GET /health/ready` antwortet 503 (Monitoring/Container "unhealthy") | Die Datenbank ist nicht erreichbar (SQLite-Datei nicht lesbar, MySQL-Server nicht erreichbar, Verbindungsprüfung nach 3 Sekunden abgebrochen); die Ursache steht im Log, nie in der Antwort. `GET /health/live` prüft nur den Prozess. Ein Aufruf mit fremdem Host-Header (nicht in `AllowedHosts`) bekommt 400. |

---

## Datenbank

| Symptom | Ursache und Lösung |
|---|---|
| Nach einem Neustart/Umzug ist alles leer, ein neuer Admin wurde angelegt | Ein **relativer** SQLite-Pfad (`Data Source=lager.db`) gilt relativ zum Arbeitsverzeichnis (Content-Root) beim Start. Bei einem anderen Startverzeichnis (Dienst, Task-Planer) entsteht eine neue, leere Datenbank. Einen **absoluten** Pfad setzen und die alte Datei dorthin verschieben. |
| "database is locked" (SQLite) | Zwei Prozesse nutzen dieselbe Datei, oder die Datei liegt auf einem Netzlaufwerk. SQLite läuft hier im WAL-Modus mit 5 Sekunden Wartezeit; ein zweiter Schreibprozess (Sicherung per Dateikopie, zweite Instanz) kann trotzdem blockieren. Pro Datenbank genau eine Instanz, Datei auf einem lokalen Laufwerk, Backups über die Seite **Backup & Restore** bzw. `POST /api/admin/backups`. |
| Demo-Daten fehlen | Der Seeder legt nur in eine **leere** Datenbank an (nie in eine vorhandene) und in `Production` nur mit `Demo:AllowInProduction=true`. Der Demo-Modus braucht `Demo:Enabled=true` bzw. `LAGER_DEMO=1` (Standard aus); siehe [Demo-Modus](#demo-modus). Der veraltete `Database:Seed` (in `appsettings.Development.json`) legt nur einen kleinen Datensatz an. |
| Bestehende Datenbank nach einem Update | Beim Start bringt der `SchemaUpgrader` die Datenbank automatisch auf den neuen Stand. Vorher eine **Kopie** der echten Datenbank testen und ein Backup anlegen; MySQL-Schritte sind nicht gegen einen echten Server getestet. |
| Zeitangaben sind um Stunden verschoben | Alle Zeitstempel sind UTC (mit `Z`); die Oberfläche rechnet in die Ortszeit des Browsers um. Reine Kalendertage (Fälligkeit) laufen als UTC-Mitternacht. |

---

## MySQL

Der MySQL-Betrieb (Provider `MySql`, Pomelo) ist weniger erprobt als SQLite: es gibt keine Tests gegen einen echten MySQL-Server. Stolpersteine:

- **Server muss beim Start erreichbar sein**, denn ohne `Database:MySqlServerVersion` fragt die Anwendung die Serverversion ab. Mit fester Version (z. B. `8.0.36`) entfällt das.
- **Datenbank und Benutzer vorab anlegen**, mit `utf8mb4`: `CREATE DATABASE lager CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;` und einen eigenen Benutzer mit Rechten auf diese Datenbank (nicht `root`). Ob die Anwendung eine nicht vorhandene Datenbank selbst anlegt, ist nicht abgesichert.
- **Große Texte:** Audit-Diffs, Wandpunkte, Wegpunkte und Wellen-Zuordnungen brauchen `LONGTEXT`. Neue Datenbanken legen die Spalten so an; eine ältere MySQL-Datenbank stellt der (nicht kritische) Schritt `0040_MySqlLongTextColumns` um. Steht im Log ein Fehler dazu, läuft die Anwendung mit der alten Grenze (64 KB) weiter und versucht es beim nächsten Start erneut.
- **Sicherung:** Backup und Restore über die API gibt es nur für SQLite. Für MySQL `mysqldump` bzw. die Sicherung des Datenbankservers verwenden.
- **Groß-/Kleinschreibung:** MySQL vergleicht SKU, Bestellnummer und Benutzername ohnehin ohne Beachtung der Schreibweise, SQLite wird darauf angeglichen.

---

## Demo-Modus

Anleitung: [GETTING_STARTED.md](GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden), Datensatz: [features/demo-modus.md](features/demo-modus.md).

| Symptom | Ursache und Lösung |
|---|---|
| Demo-Modus eingeschaltet, aber keine Demo-Daten | Die Datenbank war nicht leer: das Log meldet "Demo:Enabled ist aktiv, die Datenbank enthält aber schon Daten". Der Seeder ergänzt nie eine vorhandene Datenbank. Anwendung stoppen, Datenbankdatei samt `-wal`/`-shm` löschen (Docker: `docker compose down -v`) und neu starten. Unter `Development` kann schon der veraltete `Database:Seed` die Datenbank mit dem kleinen Datensatz gefüllt haben: `LAGER_DEMO=1` gleich beim allerersten Start setzen. |
| `LAGER_DEMO=1` oder `Demo__Enabled` wirkt in Docker nicht | Die Compose-Datei reicht `Demo__Enabled` (nicht die Kurzform `LAGER_DEMO`) und `Demo__AllowInProduction` aus der `.env` durch, und das Image läuft als `Production`: **beide** Zeilen (`Demo__Enabled=true` und `Demo__AllowInProduction=true`) in die `.env` neben der `docker-compose.yml` eintragen, dann `docker compose up -d`. Der Demo-Modus legt nur in eine **leere** Datenbank an (siehe oben), siehe [GETTING_STARTED.md](GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden). |
| Die Demo-Passwörter sind nicht zu finden | Sie stehen nur **einmal** in der Konsole bzw. im Container-Log (`docker compose logs lager`, nach "Demo-Zugangsdaten" suchen), nie in der Logdatei. Verpasst: als Admin unter **System > Benutzer** ein neues Passwort vergeben (**PW reset**). |
| Der erste Start dauert lange | Der Seeder simuliert rund 60 Tage Betrieb: je nach Rechner 5 bis 30 Sekunden, in denen die Anwendung noch keine Anfragen annimmt. Das Log meldet Beginn und Ende. |
| Log-Warnung "Database:Seed ist veraltet" | Der alte Schalter legt nur einen kleinen Datensatz an. `Database:Seed` entfernen und `Demo:Enabled` (bzw. `LAGER_DEMO=1`) nutzen. |
| Reseed (`POST /api/admin/reseed`) stellt den Demo-Datensatz nicht wieder her | Der Reseed (nur `Development`) legt nur den kleinen Altbestand-Datensatz an. Für den vollen Demo-Datensatz die Datenbank löschen und neu starten. |

---

## Backup und Restore

Bedienung: [USAGE.md](USAGE.md#backup-und-restore), Regeln und Fehlercodes: [features/backup-restore.md](features/backup-restore.md).

| Symptom | Ursache und Lösung |
|---|---|
| Die Seite **Backup & Restore** zeigt statt der Liste einen `mysqldump`-Aufruf | Backup und Restore der Anwendung gibt es nur für SQLite (`sqlite_only`). Bei MySQL `mysqldump` nutzen, siehe [MySQL](#mysql). |
| Kein automatisches Backup entsteht | `Backup__Schedule` ist nicht gesetzt (Standard: kein Zeitplan), hat kein gültiges `HH:mm` (das Log warnt) oder erreicht den Container nicht (in Docker reicht die `docker-compose.yml` es aus der `.env` durch; nach einer Änderung `docker compose up -d`). Die Uhrzeit ist **UTC**. Ein Lauf, den ein gestoppter Server verpasst hat, wird nicht nachgeholt. Schlägt ein Lauf fehl (Platte voll), steht "Zeitgesteuertes Backup fehlgeschlagen" im Log. |
| Backups sammeln sich an oder verschwinden zu früh | Nach jedem neuen Backup bleiben die neuesten `Backup__RetentionCount` (Standard 14) erhalten, ältere werden gelöscht; `0` schaltet das Löschen ab. Die Sicherheitskopien `lager-before-restore-…` werden nie automatisch gelöscht (von Hand unter **Löschen**). |
| `POST /api/admin/restore` oder der Restore in der Oberfläche antwortet 403 `restore_disabled` | Restore ist außerhalb von `Development` nur mit `Backup:AllowRestore=true` erlaubt. Für die Dauer des Restores setzen, danach wieder entfernen. |
| **Jetzt wiederherstellen** bleibt grau | Zur Bestätigung muss im Feld genau `RESTORE` (Großbuchstaben) stehen. |
| Restore antwortet 409 "Datenbankdatei ist noch in Benutzung" | Auf Windows hält ein Prozess die Datei. In einem ruhigen Moment wiederholen oder **offline** tauschen: Dienst stoppen, `lager.db` (und `-wal`/`-shm`) durch das Backup ersetzen, Dienst starten. |
| Restore lehnt die Datei ab (`invalid_backup_file`: "neueren Programmversion", "keine SQLite-Datenbank") | Das Backup stammt von einer neueren Programmversion oder ist keine gültige Lager-Datenbank. Die App vor dem Restore auf den Stand des Backups bringen. Die laufende Datenbank bleibt bei einer abgelehnten Datei unberührt. |
| Der Upload eines großen Backups scheitert | Limit 1 GB. Ein Reverse-Proxy braucht ein passendes Limit (nginx `client_max_body_size 1g;`). Sehr große Backups besser direkt vom Server kopieren: der Download über die Oberfläche läuft durch den Arbeitsspeicher des Browsers. |
| Nach erfolgreichem Restore läuft die App noch mit alten Daten | Nach dem Restore ist ein **Neustart** der Anwendung nötig (Docker: `docker compose restart`); die Datenbankverbindung lässt sich zur Laufzeit nicht austauschen. Vor dem Austausch legt der Restore eine Sicherheitskopie `lager-before-restore-<Zeit>.db` an, mit der sich ein Irrtum rückgängig machen lässt. |
| Backup-Datei ist auffällig groß oder gehört nicht ins Repository | Backups enthalten **alle Daten samt Passwort-Hashes** und sind unverschlüsselt. Auf ein anderes, geschütztes Medium legen; `*.db`-Dateien sind in `.gitignore` ausgeschlossen. Ein Backup auf demselben Laufwerk schützt nicht vor einem Plattenausfall. |

---

## CSV-Import und -Export

Bedienung: [USAGE.md](USAGE.md#csv-import-und--export), Formate und alle Fehlercodes: [features/csv-import-export.md](features/csv-import-export.md).

| Symptom | Ursache und Lösung |
|---|---|
| Umlaute sind im Import kaputt | Der Import erkennt UTF-8 (mit und ohne BOM), UTF-16 mit BOM und Windows-1252 selbst. Andere Kodierungen nicht: in Excel als "CSV UTF-8 (durch Trennzeichen getrennt)" speichern. |
| "Pflichtspalte fehlt" oder die ganze Zeile steht in einer Spalte | Meist das falsche **Trennzeichen**: deutsche Excel-Dateien nutzen Semikolon, englische Komma. Im Dialog das Trennzeichen umstellen und erneut prüfen (die Fehlermeldung nennt die gefundenen Spalten). Spaltennamen müssen wie im Export heißen (Groß-/Kleinschreibung, Leerzeichen und Unterstriche egal). |
| Im Excel-Export steht alles in Spalte A | Das gewählte Trennzeichen passt nicht zu den Excel-Einstellungen: Semikolon für deutsche, Komma für englische Excel-Versionen (Auswahl oben auf der Seite **Import & Export**). |
| Eine Zeile wird abgelehnt: zu viele Zellen | Ein Trennzeichen steckt in einem nicht in Anführungszeichen stehenden Text; alle folgenden Zellen wären verschoben. Das Feld in Anführungszeichen setzen. |
| **Übernehmen** ist gesperrt | Die Prüfung hat Zeilenfehler gefunden. Die Tabelle nennt Zeile, Schlüssel und Grund: die Datei korrigieren und neu prüfen, oder **"Fehlerhafte Zeilen auslassen"** setzen (dann gilt nur der fehlerfreie Rest). Wer Datei oder Trennzeichen ändert, muss neu prüfen. |
| Zahlen werden falsch gelesen (`1.234`) | `1.234` (genau drei Ziffern hinter einem Punkt) heißt 1234. Preise mit mehr als zwei Nachkommastellen sind ein Fehler (`invalid_price`), es wird nicht gerundet. |
| Bestandsimport: `unknown_location` oder `unknown_sku` | Lagerplätze (Lagerstruktur) und Artikel müssen vorher existieren. Reihenfolge beim Start: Artikel, dann Bestand, dann Bestellungen. |
| Bestellimport: `duplicate_order_number` | Die Bestellnummer gibt es schon; bestehende Bestellungen werden nie geändert. Dieselbe `ExternalReference` zählt als Wiederholung ("unverändert"). |
| Der Import sagt "0 neu, 0 aktualisiert" | Alles ist schon so vorhanden: der Import ist idempotent. |
| "Die Datei ist größer als 5 MB" oder zu viele Zeilen | Grenzen: 5 MB und 20.000 Datenzeilen je Datei. In mehrere Dateien aufteilen. |
| Der Import dauert lange oder andere Schreibzugriffe scheitern | Die Übernahme läuft in einer Transaktion und bucht Zeile für Zeile über die Dienste (Regeln und Ledger bleiben erhalten): 1000 Artikel brauchen wenige Sekunden, 20.000 neue Artikel rund eine bis zwei Minuten. Bei SQLite warten andere Schreiber bis zu 5 Sekunden, danach scheitern sie. Große Importe außerhalb der Stoßzeiten laufen lassen. |
| 403 auf der Seite oder den Endpunkten | Import und Export verlangen die Rolle **Manager** (oder Admin). |
| Eine Zelle im Export beginnt mit einem Apostroph | Schutz vor Formel-Injection: Text, den Excel als Formel auswerten würde (beginnt mit `=`, `+`, `-` oder `@`), bekommt im Export ein Apostroph vorangestellt. Der Import nimmt es wieder weg. |

---

## Oberfläche und Scanner

| Symptom | Ursache und Lösung |
|---|---|
| Kamera-Scan im Mobile-Picker startet nicht | Der Kamerazugriff des Browsers funktioniert nur über **HTTPS oder `localhost`**. Über `http://<IP>:5173` aus dem LAN sperrt der Browser die Kamera; per HTTPS (Reverse-Proxy) ausliefern. Außerdem braucht die Erkennung die `BarcodeDetector`-API (Chrome/Edge, vor allem Android); sonst bleibt das Textfeld zur manuellen Eingabe. |
| Änderungen im Browser erscheinen nicht / alter Stand | Ein installierter Service-Worker liefert eine ältere Version. Er läuft nur im Produktions-Build und braucht HTTPS oder `localhost`. Ein Banner "Neue Version verfügbar" bietet das Neuladen an; sonst einmal hart neu laden (`Strg+F5`). Im Entwicklungsmodus (`npm run dev`) entfernt die App installierte Service-Worker selbst. |
| "Invalid hook call" im Browser (Entwicklung) | Der Vite-Cache ist veraltet. `Remove-Item -Recurse -Force frontend/lager-ui/node_modules/.vite`, dann `npm run dev` neu starten. |
| Nach dem Login sofort wieder die Anmeldeseite | Die Sitzung ist abgelaufen oder widerrufen (siehe oben), oder der Browser blockiert `localStorage` (Privatmodus). |
| Ein Menüpunkt fehlt | Menüpunkte werden nach Rolle angezeigt: **Pickwagen**, **Lagerstruktur**, **Import & Export** und **Audit** ab Manager, **Etiketten** ab Picker, **Backup & Restore** und **Benutzer** nur für Admin. |
| Die Oberfläche erscheint in der falschen Sprache, oder Fehlermeldungen sind deutsch | Die Sprache (Deutsch, Englisch) wählt der Umschalter **DE / EN** im Seitenleisten-Fuß und auf der Anmeldeseite; die Wahl gilt pro Browser (`localStorage` `lager.lang`), ohne Wahl folgt sie der Browsersprache. **Meldungen des Servers bleiben deutsch**, auch in der englischen Oberfläche; nur häufige Fehlercodes werden übersetzt. Das ist keine Fehlkonfiguration, siehe [features/i18n.md](features/i18n.md#grenzen). |
| Seite zeigt "Daten konnten nicht geladen werden" | Der Server hat mit einem Fehler geantwortet (Text und Referenz-ID stehen im Hinweis). Die Referenz-ID im Log suchen; bei 401 neu anmelden. |

---

## Etiketten und Druck

Bedienung: [USAGE.md](USAGE.md#etiketten), Referenz: [features/etiketten.md](features/etiketten.md).

| Symptom | Ursache und Lösung |
|---|---|
| Das Raster passt nicht auf den Etikettenbogen (verschoben, verkleinert) | Im Druckdialog des Browsers **Skalierung 100 % ("Tatsächliche Größe")** und **Ränder "Keine"** einstellen. Druckskalierung und Ränder unterscheiden sich je Browser und Drucker; den Bogen einmal auf Papier gegen die Etiketten halten. |
| Ein Etikett zeigt nur Text statt eines Barcodes, die Seite meldet einen Fehler | Code 128 kann das Zeichen nicht darstellen (z. B. Umlaute oder Zeichen außerhalb von ASCII 32 bis 126 in der SKU oder im Lagerplatz-Code). Den Code ändern oder ein solches Etikett nicht drucken. |
| "zu lang für dieses Format" | Der Code würde auf dem gewählten Format zu dicht (Balken schmaler als 0,19 mm). Ein größeres Format (A4-Bogen statt Einzeletikett) oder einen kürzeren Code wählen. |
| Keine Vorschau bei sehr vielen Etiketten | Mehr als 1000 Etiketten samt Kopien druckt der Browser nicht: die Auswahl aufteilen (z. B. je Gang). Der ZPL-Download geht weiter. |
| Der ZPL-Download bricht mit einer Meldung ab (429) | Der Download ruft je Etikett einen Endpunkt auf; bei aktivem Rate-Limit (`Security:GlobalRateLimitPerMinute`, Standard 600 je Minute) sind gut 500 Etiketten je Download möglich. Die Auswahl aufteilen oder das Limit anheben. Es wird keine Teildatei gespeichert. |
| Der Barcode lässt sich mit dem Handy nicht lesen | Ob ein Ausdruck lesbar ist, ist noch nicht mit einem echten Handy geprüft (nur Encoder, Modulfolge und Raster sind getestet). Druckauflösung und Skalierung (100 %) prüfen, den Bogen in ausreichender Größe und Schwärzung drucken. Die Kamera im Mobile-Picker braucht HTTPS (siehe oben). |
| `GET /api/labels/...zpl?copies=n` antwortet 400 | `copies` muss zwischen 1 und 500 liegen. |

---

## Fachliche Meldungen

| Meldung / Code | Bedeutung |
|---|---|
| 409 `concurrency_conflict` ("... von jemand anderem geändert") | Ein anderer Benutzer hat denselben Beleg gleichzeitig bearbeitet. Neu laden und die Aktion wiederholen. Die Oberfläche lädt nicht selbst neu. |
| 409 `in_use` beim Löschen | Der Datensatz wird noch von Bestand oder Belegen verwendet (z. B. ein Lagerplatz mit Bestand). Erst leeren bzw. den Verweis auflösen. |
| 409 `duplicate` | Ein eindeutiger Wert (SKU, Bestellnummer, Benutzername ...) existiert schon. Groß-/Kleinschreibung zählt nicht. |
| 409 `lot_expiry_mismatch` | Dieselbe Charge im selben Lagerplatz mit **anderem MHD**: eine Charge hat genau ein MHD. |
| Bestellung "kein Stock" oder "FIFO-Konflikt" | Bestand reicht nicht (mehr) bzw. ältere oder dringendere Bestellungen verbrauchen ihn zuerst. Abgelaufene Chargen (MHD vor heute) zählen nicht als verfügbar. |
| Pickliste lässt sich nicht erzeugen ("Nur neue Bestellungen können kommissioniert werden") | Die Bestellung steht nicht auf `New` (schon auf einer Pickliste, verpackt, versendet oder storniert). |
| "Sendungen gibt es nur für gepackte Bestellungen" | Erst die Pickliste verpacken (Bestellung `Packed`), dann die Sendung anlegen. |
| Wareneingang "hat keine Positionen" | Ein Wareneingang lässt sich nur mit mindestens einer Position buchen. Die Erfassungsmaske legt Kopf und Positionen gemeinsam an; ein leerer Kopf entsteht nur über die API ohne `lines`. Siehe [USAGE.md](USAGE.md#wareneingang). |
| 409 `duplicate_code` (Lagerstruktur) | Ein Lager-, Zonen-, Gang-, Regal- oder Lagerplatz-Code gibt es schon (Lager und Lagerplatz im ganzen System, die anderen im Elternknoten; Groß-/Kleinschreibung zählt nicht). |
| 409 `warehouse_not_empty` | Ein Lager, eine Zone, ein Gang oder ein Regal lässt sich nicht löschen, solange darunter Bestand liegt oder eine offene Aufgabe (Nachschub, laufende Inventur) auf einen Lagerplatz zeigt. Die Meldung nennt die Lagerplätze. Ein Lagerplatz, auf den Belege verweisen, bleibt bestehen (409 `in_use`). |
| 409 `ambiguous_code` (`GET /api/articles/by-code/...`) | Der gescannte Code passt auf mehrere Artikel (z. B. dieselbe Alternativ-SKU an zwei Artikeln); die Antwort nennt die Kandidaten. Alternativ-SKUs eindeutig halten. |
| 409 `article_not_orderable` | Ein Artikel der Bestellung liegt außerhalb seines Saison-Fensters (Gültig ab/bis am Artikel). |
