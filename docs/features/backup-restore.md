# Backup und Restore: Oberfläche, Zeitplan, Aufbewahrung, sicherer Restore

Ausführliche Referenz zu Backup und Restore (Einstellungen, Zeitplan, Aufbewahrung, API, Grenzen). Die Kurzfassung für die Bedienung steht in [USAGE.md](../USAGE.md#backup-und-restore), der Betrieb in [GETTING_STARTED.md](../GETTING_STARTED.md#backup-und-restore).

Baut auf dem konsistenten Backup/Restore per `VACUUM INTO` auf. Dazu gehören eine Seite **System > Backup & Restore** (nur Admin), Download,
ein zeitgesteuerter Hintergrundjob mit Aufbewahrung und ein Restore, der aus der Oberfläche mit Bestätigung und ohne Umweg über
Swagger läuft. Alles gilt für **SQLite**; bei MySQL zeigt die Seite den `mysqldump`-Aufruf.

## Bedienung

Die Seite steht unter **System > Backup & Restore** (`/system`, Rolle Admin).

| Bereich | Was man dort tut |
|---|---|
| Einstellungen (nur lesen) | Zeitplan (`täglich 02:00 UTC` oder `aus`), nächster Lauf (in Ortszeit), Aufbewahrung, ob der Restore freigegeben ist. Die Werte kommen aus der Server-Konfiguration (siehe unten), die Seite ändert sie nicht. |
| **Backup jetzt erstellen** | legt sofort ein Backup an; danach greift die Aufbewahrung |
| Liste | Name, Art (**Backup** oder **Vor Restore**), Größe, Zeit; je Zeile **Herunterladen**, **Wiederherstellen**, **Löschen** |
| **Backup-Datei einspielen…** | Restore aus einer Datei, die man hochlädt (z. B. ein heruntergeladenes Backup von einem anderen Rechner) |

**Herunterladen** holt die Datei mit dem Login-Token (ein einfacher Link würde mit 401 scheitern); sie landet über den Browser-Download
im Download-Ordner. Sehr große Backups laufen dabei durch den Arbeitsspeicher des Browsers; für große Datenbanken die Datei besser direkt
vom Server (Backup-Verzeichnis) kopieren.

**Löschen** fragt vorher nach und entfernt die Datei endgültig. Auch die Sicherheitskopie aus einem Restore lässt sich löschen (mit Hinweis).

### Restore

1. In der Zeile **Wiederherstellen** wählen (oder **Backup-Datei einspielen…** und eine Datei wählen).
2. Der Dialog warnt deutlich: die laufende Datenbank wird ersetzt, alle Änderungen seit dem Stand der Sicherung gehen verloren, danach ist ein Neustart nötig.
3. Zur Bestätigung **`RESTORE`** eintippen (genau so, Großbuchstaben). Erst dann ist **Jetzt wiederherstellen** aktiv.
4. Der Server prüft die Datei, sichert den aktuellen Stand als `lager-before-restore-<UTC-Zeit>.db` und tauscht die Datenbank aus.
5. Die Seite zeigt **Neustart erforderlich** samt Name der Sicherheitskopie. **Den Server neu starten** (Docker: `docker compose restart`, sonst Dienst bzw. Prozess neu starten)
   und neu anmelden. Bis zum Neustart sind weitere Restores auf der Seite gesperrt.

Warum der Neustart: Die Datenbankverbindung der laufenden Anwendung lässt sich nicht austauschen. Der Restore leert zwar alle gepoolten
Verbindungen und ersetzt die Datei atomar (Umbenennen im selben Verzeichnis), sauber ist der Stand aber erst nach dem Neustart. In Docker
sorgt eine Restart-Policy (`restart: unless-stopped`) dafür, dass ein beendeter Container wieder anläuft.

Der Restore ist **kein Rückgängig-Knopf für einen halben Fehler**: ist er durch, ist der neue Stand aktiv. Wer sich vertan hat, spielt die
Sicherheitskopie `lager-before-restore-…` ein.

## Konfiguration

Abschnitt `Backup` (appsettings oder Umgebungsvariablen mit `__`). Alle Werte haben einen Standard im Code; die `appsettings.json` braucht keinen Eintrag.

| Schlüssel | Umgebungsvariable | Standard | Bedeutung |
|---|---|---|---|
| `Backup:Directory` | `Backup__Directory` | Verzeichnis der SQLite-Datei | Zielverzeichnis für Backups und die Sicherheitskopie vor einem Restore. Relativ = relativ zum Content-Root. In Docker ein eigenes Volume (siehe Grenzen). |
| `Backup:Schedule` | `Backup__Schedule` | leer = kein Zeitplan | tägliche Startzeit als `HH:mm` in **UTC**, z. B. `02:00`. Ungültiger Wert: Warnung im Log, wie "aus". |
| `Backup:RetentionCount` | `Backup__RetentionCount` | `14` | so viele Backups bleiben erhalten; ältere werden nach jedem neuen Backup gelöscht. `0` oder weniger = nie automatisch löschen. |
| `Backup:AllowRestore` | `Backup__AllowRestore` | `false` | erlaubt den Restore **außerhalb von `Development`**. Ohne das Flag antwortet der Restore in Produktion mit 403 `restore_disabled`. Nur für die Dauer des Restores setzen. |

Änderungen an `appsettings*.json` wirken ohne Neustart (die Einstellungen werden bei jedem Zugriff frisch gelesen); Umgebungsvariablen
setzen sich erst mit dem Prozess durch, also mit einem Neustart.

Beispiel (Docker Compose):

```yaml
environment:
  Backup__Directory: /backups          # eigenes Volume
  Backup__Schedule: "02:00"            # täglich 02:00 UTC
  Backup__RetentionCount: "14"
volumes:
  - lager-backups:/backups
```

Die mitgelieferte `docker-compose.yml` reicht `Backup__Schedule` und `Backup__RetentionCount` aus der `.env` durch (z. B. `Backup__Schedule=02:00`); das Beispiel oben gilt für eine eigene Compose-Datei mit einem eigenen Backup-Volume.

### Zeitplan

Der Hintergrunddienst (`BackupHostedService`) legt täglich zur Uhrzeit aus `Backup:Schedule` ein Backup an. Die Zeit ist **UTC**, nicht Ortszeit
(keine Sommerzeit-Lücken, im Container ohnehin die Vorgabe): `02:00` sind im deutschen Winter 03:00 Uhr, im Sommer 04:00 Uhr. Die Seite zeigt
den nächsten Lauf in der Ortszeit des Browsers. Ein Lauf, der verpasst wurde, weil der Server zur Startzeit aus war, wird **nicht nachgeholt**.
Ein Fehler beim Backup (Platte voll, Datei gesperrt) wird nur ins Log geschrieben (`Zeitgesteuertes Backup fehlgeschlagen`); der nächste Lauf versucht es erneut.

### Aufbewahrung

Nach **jedem** neuen Backup (manuell oder per Zeitplan) bleiben die neuesten `Backup:RetentionCount` Backups (`lager-backup-…`) erhalten, die älteren werden
gelöscht. "Älter" heißt: der Zeitpunkt im Dateinamen, nicht das Änderungsdatum der Datei (ein kopiertes Backup wird nicht "jünger"). Die Sicherheitskopien
`lager-before-restore-…` zählen nicht mit und werden **nie** automatisch gelöscht; auch fremde Dateien im Verzeichnis (die Live-Datenbank, Notizen) bleiben unberührt.

## Dateien und Namen

Sicherungsdateien heißen `<db-name>-backup-<UTC>.db` bzw. `<db-name>-before-restore-<UTC>.db`, z. B. `lager-backup-20260930-020000-000.db`
(Datum, Uhrzeit, Millisekunden in UTC; bei einer Kollision kommt ein Zähler dazu, nichts wird überschrieben). Ein Backup entsteht unter einem Zwischennamen und wird
erst fertig umbenannt: eine halb geschriebene Datei taucht nie in der Liste auf.

Die Liste, der Download, das Löschen und der Restore aus einem Backup akzeptieren **nur Namen, die genau diesem Muster entsprechen**. Ein Name mit
Pfadanteil (`../lager.db`), die Live-Datenbank oder eine fremde Datei ergibt 400 `invalid_backup_name`, nie eine andere Datei. Antworten nennen nur Dateinamen, nie einen Serverpfad.

Standardmäßig liegen die Backups **neben der Datenbank** (wie vor der Oberfläche; ein Unterordner `backups` wäre für bestehende Skripte ein Bruch).
Wer sie trennen will, setzt `Backup:Directory`.

## API

Alle Endpunkte: Rolle **Admin**, nur SQLite (bei MySQL 400 `sqlite_only`, `mysqldump` nutzen). Fehlerformat wie überall (`application/problem+json` mit `code`).

| Endpunkt | Wirkung |
|---|---|
| `GET /api/admin/backup-settings` | Einstellungen und Zustand: `supported`, `schedule`, `scheduleValid`, `nextRunUtc`, `retentionCount`, `allowRestore`, `restoreAllowed`, `customDirectory`, `maxRestoreBytes`; bei MySQL `mysqlDumpCommand`/`mysqlRestoreCommand`. Kein Pfad. |
| `GET /api/admin/backups` | Liste: `name`, `sizeBytes`, `createdUtc`, `kind` (`backup` / `before-restore`), neueste zuerst |
| `POST /api/admin/backups` | Backup jetzt anlegen; 201 mit dem Listeneintrag und `Location` auf den Download |
| `GET /api/admin/backups/{name}` | Datei-Stream als Download (`Content-Disposition: attachment`, `Cache-Control: no-store`); 400 bei ungültigem Namen, 404 wenn es die Datei nicht gibt |
| `DELETE /api/admin/backups/{name}` | löscht die Datei; 204, 400, 404 |
| `POST /api/admin/restore` | Restore, siehe unten |
| `POST /api/admin/backup` | wie `POST /api/admin/backups`, aber mit der älteren Antwortform (`message`, `fileName`, `sizeBytes`, Status 200); bleibt für bestehende Skripte |

**`POST /api/admin/restore`** ist ein `multipart/form-data`-Formular mit einer der beiden Quellen:

| Feld | Bedeutung |
|---|---|
| `file` | eine hochgeladene SQLite-Datei (bis 1 GB; das Standardlimit von rund 30 MB ist für diesen Endpunkt angehoben) |
| `backupName` | Name eines vorhandenen Backups aus der Liste (das Backup selbst bleibt unverändert) |
| `confirm` | muss `RESTORE` sein. **Pflicht** bei `backupName`; beim Upload wird es geprüft, sobald es gesendet wird (die Oberfläche sendet es immer; ein Upload ohne das Feld bleibt aus Rückwärtskompatibilität zur älteren Schnittstelle möglich). |

Beides gleichzeitig oder nichts von beidem ergibt 400. Ablauf: erlaubt in `Development` oder mit `Backup:AllowRestore=true` (sonst 403 `restore_disabled`) →
Quelle in eine Temp-Datei neben die Live-DB schreiben und **prüfen** (SQLite-Header, `PRAGMA integrity_check`, Pflichttabellen, Schemastand nicht neuer als die App;
sonst 400 `invalid_backup_file`, die Live-DB bleibt unberührt) → Sicherheitskopie der Live-DB (`before-restore`) → alle Verbindungen schließen, `-wal`/`-shm`/`-journal` der
alten DB entfernen, Datei atomar austauschen. Antwort: `restartRequired: true`, `safetyBackup` (Dateiname), `sizeBytes`. Hält ein anderer Prozess die Datei offen (Windows),
antwortet der Restore mit 409 `database_in_use` und tauscht nichts.

Fehlercodes im Überblick: `restore_disabled` (403), `invalid_backup_name` (400), `confirmation_required` (400), `invalid_backup_file` (400), `validation_failed` (400, keine oder zwei Quellen),
`not_found` (404), `payload_too_large` (413), `database_in_use` (409), `backup_in_use` (409, Löschen einer Datei, die gerade gelesen wird), `sqlite_only` (400), `database_not_file_based` (400, In-Memory).

## MySQL

Über die Anwendung gibt es kein Backup und keinen Restore. Die Seite zeigt statt der Liste den Aufruf (Platzhalter ersetzen):

```bash
mysqldump --single-transaction --routines --triggers -h <host> -u <benutzer> -p <datenbank> > lager-backup.sql
mysql -h <host> -u <benutzer> -p <datenbank> < lager-backup.sql     # Wiederherstellen
```

Stolpersteine (Zeichensatz, große Textspalten, Server-Version): [TROUBLESHOOTING.md](../TROUBLESHOOTING.md#mysql). Der Zeitplan-Job läuft bei MySQL nicht.

## Grenzen

- **Ein Backup auf demselben Laufwerk schützt nicht vor einem Plattenausfall.** Das Backup-Verzeichnis gehört auf ein anderes Medium (eigenes Volume, Netzlaufwerk,
  regelmäßiges Kopieren mit `rsync`/Robocopy/Cloud-Sync). Die Anwendung kopiert nichts nach außen; die Aufbewahrung räumt nur das Verzeichnis auf.
- **Backups sind unverschlüsselt** und enthalten alle Daten samt Passwort-Hashes: geschützt ablegen, nie ins Repository (`*.db` ist in `.gitignore`).
- Der **Neustart nach einem Restore** ist nötig (siehe oben); ohne Restart-Policy in Docker den Container von Hand starten.
- Der Restore nimmt eine Datenbank nur an, deren Schema **nicht neuer** als das der Anwendung ist. Ein Backup von einer älteren Version funktioniert; der Schema-Upgrade läuft beim Neustart.
- Restore-Upload höchstens **1 GB**. Reverse-Proxys (nginx `client_max_body_size`, Caddy) brauchen ein entsprechendes Limit, sonst lehnt der Proxy vorher ab.
- Auf **Windows** kann eine offene Datenbankdatei den Austausch verhindern (409 `database_in_use`); dann in einem ruhigen Moment wiederholen oder offline tauschen (Dienst stoppen, Datei samt `-wal`/`-shm` ersetzen, Dienst starten).
- **Pro Datenbank genau eine Instanz.** Der Zeitplan läuft in jeder Instanz; zwei Instanzen auf derselben Datei sind ohnehin nicht vorgesehen.
- Ein verpasster Zeitplan-Lauf wird nicht nachgeholt; wer Lücken nicht will, prüft die Liste (Zeitstempel) oder legt Backups zusätzlich von außen an (`POST /api/admin/backups` mit Token).

## Technik

- `src/Lager.Infrastructure/Backup/`: `BackupOptions` (Abschnitt `Backup`), `BackupService` (Liste, Backup, Aufbewahrung, Löschen, Restore; ein Zugriffsschutz serialisiert die
  Vorgänge), `BackupSchedule` (Zeitplan-Berechnung als reine Funktionen), `BackupHostedService` (Hintergrundjob, Uhr über `TimeProvider`). Die Datei-Logik (`VACUUM INTO`, Prüfung, Austausch) bleibt in
  `Persistence/DatabaseInitializer.cs` (`SqliteDatabaseFile`).
- `AdminController`: die Endpunkte oben; die Übersetzung "Datei in Benutzung" in 409 bleibt dort.
- Frontend: `frontend/lager-ui/src/features/system/` (Seite, Restore-Dialog, `route.tsx` für die Route-Registry), Hooks in `frontend/lager-ui/src/api/systemHooks.ts`.
- Tests: `tests/Lager.Tests/WP25/` (Backup unter Schreiblast, Download und Traversal, Aufbewahrung, Zeitplan mit Fake-Uhr, Restore mit Flag und Bestätigung, Rollen, MySQL) und `frontend/lager-ui/src/tests/WP25/` (Liste, Download, Restore-Bestätigungsfluss).
