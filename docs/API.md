# API-Überblick

Die REST-API des Backends (`/api/...`, JSON). Diese Seite erklärt Anmeldung, Rollen, Fehlerformat und listet die Endpunkte je Bereich. Die vollständige, immer aktuelle Beschreibung mit Beispiel-Bodies, Fehlerantworten und Kommentaren liefert Swagger (OpenAPI): es läuft in der Umgebung `Development` (`http://localhost:5099/swagger`, siehe [GETTING_STARTED.md](GETTING_STARTED.md#3-erster-start)) oder in jeder Umgebung mit `Swagger:Enabled=true`, Einzelheiten unter [Swagger und OpenAPI](#swagger-und-openapi) und in [features/openapi.md](features/openapi.md). Konfiguration: [CONFIGURATION.md](CONFIGURATION.md), Datenmodell: [DATA_MODEL.md](DATA_MODEL.md).

---

## Inhalt

1. [Grundregeln](#grundregeln)
2. [Anmeldung](#anmeldung)
3. [Rollen](#rollen)
4. [Fehlerformat](#fehlerformat)
5. [Fehlercodes](#fehlercodes)
6. [Endpunkte](#endpunkte)
7. [Health-Endpunkte](#health-endpunkte)
8. [Swagger und OpenAPI](#swagger-und-openapi)

---

## Grundregeln

- **Standardmäßig geschlossen:** Jeder Endpunkt außer `POST /api/auth/login` und den Health-Endpunkten (`GET /health/live`, `GET /health/ready`, außerhalb von `/api`, siehe [Health-Endpunkte](#health-endpunkte)) verlangt ein gültiges Token. Eine Anfrage ohne Token oder mit ungültigem Token bekommt **401**, auch für Pfade, die es gar nicht gibt (mit Token käme dort ein 404).
- **JSON:** Eigenschaften in camelCase. **Enums nur als Text** (`"Picking"`, nie `2`); ein Zahlenwert im Body ist ein Fehler (400).
- **Zeitstempel** sind immer UTC und tragen ein `Z` (`2026-09-30T12:00:00Z`). Kalendertage (Fälligkeit) laufen als UTC-Mitternacht.
- **IDs** sind GUIDs. Beträge sind ganze Cent-Werte (`purchasePriceCents`), Maße in Millimetern, Gewichte in Gramm.
- **Korrelations-ID:** Jede Antwort trägt den Header `X-Correlation-Id`. Wer selbst eine ID mitschickt (1 bis 64 Zeichen aus `A-Za-z0-9._-`), bekommt sie zurück; sie steht auch in der Logdatei und in Fehlerantworten.
- **Rate-Limit:** je Client-IP, streng am Login (Standard 10 pro Minute), grob global (600 pro Minute). Überschreitung: **429** mit `Retry-After`.
- **Eingabegrenzen:** Mengen 1 bis 1.000.000, Listen in Anfragen höchstens 500 Einträge (Pack-Anfragen 5000), Wände 2 bis 500 Punkte, Regal-Anlage höchstens 500 Fächer, Koordinaten und Maße höchstens 1.000.000 mm. Zeitraum-Parameter (`days`, `range`, `rangeDays`) 1 bis 3660 (die Auswertungen rechnen intern mit höchstens 365 Tagen, Dead-Stock mit 3650), Ergebnisgrenzen (`top`, `take`) 1 bis 1000. Verstöße liefern 400 mit `errors` je Feld. Für alle Anfragen gilt die Kestrel-Standardgrenze (rund 30 MB Body); nur der Restore-Upload erlaubt bis zu 1 GB.
- **Optimistische Sperre:** Bestand, Bestellungen, Picklisten, Wellen, Wareneingänge, Inventuren, Retouren, Einkaufsbestellungen, Sendungen und Nachschub-Aufgaben erkennen gleichzeitige Änderungen. Der zweite Schreiber bekommt **409** mit dem Code `concurrency_conflict` und muss neu laden. Stammdaten (Artikel, Kunden, Lagerplätze ...) sind "letzter Schreiber gewinnt".
- **Keine Paginierung:** Listen-Endpunkte liefern alles (Ausnahmen mit `take`: Audit; `GET /api/articles` filtert mit `?search=`). Das ist bei großen Beständen langsam und steht auf der Roadmap ([TODO.md](../TODO.md)). `GET /api/version` liefert die Version der Anwendung und den Stand des Datenbankschemas (siehe [System](#system)).
- **CSV und Dateien:** Import und Export laufen über eigene Endpunkte (siehe [CSV-Import und -Export](#csv-import-und--export)); Backup-Download, ZPL-Etiketten und das Lieferschein-PDF sind Datei-Antworten (kein JSON). Der Download per Browser-Link braucht das Token im Header (ein einfacher Link scheitert mit 401), die Oberfläche holt die Dateien deshalb mit dem Token.

---

## Anmeldung

1. `POST /api/auth/login` mit `{ "username": "...", "password": "..." }` liefert `{ token, expiresAt, user }`. Fehler (unbekannter Benutzer, falsches Passwort, gesperrtes oder deaktiviertes Konto) antworten **identisch** mit 401 und dem Code `invalid_credentials`; auch die Antwortzeit verrät nichts.
2. Das Token als `Authorization: Bearer <token>` an jede Anfrage hängen. Es ist standardmäßig 8 Stunden gültig und enthält Benutzer-ID (`sub`), Name, je Rolle einen `role`-Claim und einen Fingerabdruck des Passworts.
3. Der Server lädt den Benutzer bei **jeder** Anfrage aus der Datenbank: ein Token wird sofort ungültig (401), wenn das Konto deaktiviert oder gelöscht wurde, das Passwort geändert oder zurückgesetzt wurde oder sich die Rollen geändert haben.
4. **Passwortwechsel-Zwang:** Hat ein Benutzer `mustChangePassword` (erster Login des Bootstrap-Admins, Admin-Reset), antwortet jeder Endpunkt außer `GET /api/auth/me` und `POST /api/auth/change-password` mit **403** und dem Code `password_change_required`. `POST /api/auth/change-password` (`{ currentPassword, newPassword }`) liefert ein **neues** Token.
5. **Kontosperre:** Nach 5 Fehlversuchen (`Auth:MaxFailedAttempts`) ist das Konto 15 Minuten gesperrt (`Auth:LockoutMinutes`); die Antwort bleibt ein gewöhnliches 401. Ein Admin-Reset des Passworts hebt die Sperre auf.
6. **Passwortregeln:** mindestens 10 Zeichen, höchstens 72 Bytes, nicht gleich dem Benutzernamen, beim Wechsel nicht gleich dem bisherigen Passwort (Code `password_policy`; falsches Altpasswort: `invalid_current_password`, 400).

Beispiel:

```bash
TOKEN=$(curl -s -X POST http://localhost:5099/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<dein-passwort>"}' | jq -r .token)
curl -s http://localhost:5099/api/articles -H "Authorization: Bearer $TOKEN"
```

---

## Rollen

Ein Benutzer kann mehrere Rollen tragen. **Admin** darf alles, **Manager** alles außer den Admin-Bereichen. Die Rollen Receiver, Picker und Packer öffnen jeweils ihren operativen Bereich (plus Manager und Admin). **Viewer** darf nur lesen. Die Spalte "Rolle" in den Tabellen unten nennt die **Mindestberechtigung**:

| Wert | Bedeutung |
|---|---|
| `anonym` | ohne Anmeldung (Login; dazu die Health-Endpunkte außerhalb von `/api`) |
| `angemeldet` | jeder angemeldete Benutzer ohne ausstehenden Passwortwechsel, auch Viewer |
| `Receiver` | Receiver, Manager, Admin |
| `Picker` | Picker, Manager, Admin |
| `Packer` | Packer, Manager, Admin |
| `Manager` | Manager, Admin |
| `Admin` | nur Admin |

Wer eine Anfrage unterhalb der Mindestberechtigung stellt, bekommt **403** (Code `forbidden`), bevor die Aktion etwas ändert. Lesezugriffe sind für alle Angemeldeten offen, mit diesen Ausnahmen: Audit-Protokoll, Bestandsbewertung, CSV-Export, Etiketten, Waage (Rollen siehe Tabellen) sowie die Benutzerliste und die Backup-Verwaltung (Admin). Die Rollenmatrix wird durch Tests gegen die echten Routen geprüft (`tests/Lager.Tests/WP02/EndpointMatrix.cs`).

Das Frontend blendet Menüpunkte je Rolle ein und aus, ist aber **keine Sicherheitsgrenze**: maßgeblich ist immer der Server.

---

## Fehlerformat

Fehler kommen als `application/problem+json` (RFC 7807) mit diesen Feldern:

| Feld | Bedeutung |
|---|---|
| `type` | Link auf die Beschreibung des Statuscodes |
| `title` | kurzer deutscher Titel |
| `status` | HTTP-Statuscode |
| `detail` | Meldung für Menschen (bei fachlichen Fehlern eine bewusst formulierte Regel-Meldung, nie Datenbank-Interna) |
| `code` | maschinenlesbarer Fehlercode in snake_case, siehe [Fehlercodes](#fehlercodes) |
| `correlationId` | Referenz-ID, identisch mit dem Header `X-Correlation-Id` und den Logzeilen |
| `errors` | nur bei Validierungsfehlern (400): `{ "feld": ["Meldung", ...] }` |
| `error` | Kompatibilitätsfeld, gleicher Text wie `detail` (für Clients, die früher `{ error }` gelesen haben) |

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Konflikt",
  "status": 409,
  "detail": "Bestellung ORD-1 (Packed) lässt sich nicht mehr stornieren: sie ist bereits verpackt bzw. versendet",
  "code": "order_not_cancellable",
  "correlationId": "3f2c9a1e5b7d4c0f8a6e1d2b9c4f7a30",
  "error": "Bestellung ORD-1 (Packed) lässt sich nicht mehr stornieren: sie ist bereits verpackt bzw. versendet"
}
```

Statuscodes: **400** ungültige Eingabe, **401** nicht angemeldet, **403** keine Berechtigung, **404** nicht gefunden, **409** Regelverstoß oder Konflikt (fachliche Fehler, doppelte Werte, gleichzeitige Änderung), **413** zu groß, **429** zu viele Anfragen, **500** interner Fehler (ohne Details; in `Development` zusätzlich mit Ausnahme und Stacktrace).

Alle Fehler der Controller laufen durch einen zentralen Handler und kommen in diesem Format (fachliche Fehler, Validierung, doppelte Werte, gleichzeitige Änderungen, Datenbankfehler, unerwartete Fehler; auch leere 401/403/404/405 werden damit gefüllt). **Bekannte Abweichungen:**

- Der Login-Fehler (`invalid_credentials`) ist ein ProblemDetails, lässt aber `correlationId` im Body weg (sie steht im Header `X-Correlation-Id`).
- **429** (Rate-Limit) und **403 `password_change_required`** sind kurze JSON-Bodies `{ "code": "...", "error": "..." }` ohne `type`, `title`, `status` und `correlationId` (mit dem passenden Statuscode; bei 429 zusätzlich der Header `Retry-After`).
- Der Host-Filter (`AllowedHosts`) antwortet mit **400** und einer HTML-Seite ("Bad Request - Invalid Hostname"), nicht mit JSON.

Verlässlich ist deshalb in jeder JSON-Fehlerantwort der Statuscode sowie `code` und `error` (lesbarer Text, bei ProblemDetails identisch mit `detail`); `correlationId` fehlt nur in den genannten Fällen. Das Frontend wertet alle Formen aus.

---

## Fehlercodes

Ein Auszug der wichtigsten Codes. Fachliche Regeln tragen meist einen eigenen, sprechenden Code (die Liste ist nicht abschließend, im Code stehen weitere, etwa für unbekannte Verweise in Bestellungen und Einkauf; `detail` enthält immer den lesbaren Grund). Regelverstöße ohne eigenen Code kommen als `conflict`, ungültige Eingaben als `validation_failed`.

**Allgemein**

| Code | Status | Bedeutung |
|---|---|---|
| `validation_failed` | 400 | Eingabe ungültig (Pflichtfeld fehlt, Wert außerhalb der Grenzen, unbekannter Enum-Text); Feldfehler in `errors` |
| `unauthorized` | 401 | nicht angemeldet, Token ungültig oder abgelaufen |
| `invalid_credentials` | 401 | Login fehlgeschlagen (Grund bewusst nicht unterscheidbar) |
| `forbidden` | 403 | Rolle reicht nicht |
| `password_change_required` | 403 | Passwort muss zuerst geändert werden |
| `not_found` | 404 | Datensatz existiert nicht |
| `conflict` | 409 | Regelverstoß oder Konflikt mit dem aktuellen Zustand (Standardcode fachlicher Fehler) |
| `duplicate` | 409 | Eindeutiger Wert existiert schon (SKU, Bestellnummer, Benutzername ...); `detail` nennt die Spalte |
| `in_use` | 409 | Löschen scheitert, weil der Datensatz noch von Bestand oder Belegen verwendet wird (z. B. ein Lagerplatz) |
| `invalid_reference` | 409 | Verweis auf einen nicht vorhandenen Datensatz (Artikel, Lagerplatz, Kunde, Lieferant ...) |
| `concurrency_conflict` | 409 | Ein anderer Benutzer hat den Datensatz zwischenzeitlich geändert: neu laden und wiederholen |
| `payload_too_large` | 413 | Anfrage zu groß |
| `too_many_requests` | 429 | Rate-Limit überschritten (`Retry-After` beachten) |
| `internal_error` | 500 | Unerwarteter Fehler; die `correlationId` in der Meldung an den Betreiber weitergeben |

**Benutzer und Anmeldung**

| Code | Status | Bedeutung |
|---|---|---|
| `password_policy` | 400 | neues Passwort erfüllt die Regeln nicht |
| `invalid_current_password` | 400 | Altpasswort beim Passwortwechsel falsch |
| `unknown_role` | 400 | Rollenname unbekannt (erlaubt: Admin, Manager, Receiver, Picker, Packer, Viewer) |
| `user_rule_violation` | 409 | Schutzregel: der letzte aktive Admin bleibt bestehen, niemand deaktiviert sich selbst oder entzieht sich die Admin-Rolle |

**Artikel** (Details: [features/artikel-gtin.md](features/artikel-gtin.md))

| Code | Status | Bedeutung |
|---|---|---|
| `invalid_gtin` | 400 | GTIN ungültig (nur Ziffern, 8/12/13/14 Stellen, Prüfziffer nach GS1); über die Eingabeprüfung meldet die Antwort den Fehler meist als `validation_failed` mit dem Feld `Gtin` in `errors` |
| `duplicate_gtin` | 409 | die GTIN gehört schon einem anderen Artikel (auch in gleichwertiger Schreibweise, z. B. UPC-A und EAN-13) |
| `duplicate_bundle_component` | 400 | eine Komponente kommt mehrfach vor |
| `unknown_bundle_component` | 400 | Bundle-Komponente existiert nicht |
| `bundle_cycle` | 409 | Selbstreferenz oder Zyklus über mehrere Ebenen; die Meldung nennt den Weg |
| `bundle_too_deep` | 409 | Verschachtelung tiefer als 5 Ebenen |
| `ambiguous_code` | 409 | `GET /api/articles/by-code/...`: der Code passt auf mehrere Artikel; Feld `candidates` enthält sie |
| `article_not_orderable` | 409 | Bestellung mit Artikeln außerhalb ihres Saison-Fensters |

**Lagerstruktur** (Details: [features/lager-stammdaten.md](features/lager-stammdaten.md))

| Code | Status | Bedeutung |
|---|---|---|
| `duplicate_code` | 409 | Lager-, Zonen-, Gang-, Regal- oder Lagerplatz-Code existiert schon (ohne Beachtung der Schreibweise; Lager und Lagerplatz im ganzen System, sonst im Elternknoten); es wird nichts geändert |
| `warehouse_not_empty` | 409 | Löschen eines Lagers, einer Zone, eines Gangs oder Regals scheitert: darunter liegt Bestand oder eine offene Aufgabe zeigt auf einen Lagerplatz; die Meldung nennt die Lagerplätze |
| `shelf_bin_limit` | 409 | ein Regal fasst höchstens 500 Lagerplätze |

**Bestellungen, Kommissionierung, Versand**

| Code | Status | Bedeutung |
|---|---|---|
| `duplicate_order_number` | 409 | Bestellnummer existiert schon und gehört nicht zur mitgeschickten `externalReference` |
| `invalid_idempotency_key` | 400 | `Idempotency-Key` länger als 128 Zeichen |
| `idempotency_key_mismatch` | 400 | `Idempotency-Key` und `externalReference` im Body sind verschieden |
| `order_not_cancellable` | 409 | Storno nur aus New, Picking und Picked; ab Packed nicht mehr |
| `invalid_order_transition` | 409 | unzulässiger Statuswechsel |
| `insufficient_stock` | 409 | Bestand reicht nicht (manuelle Korrektur, Nachschub). Kommissionierung und Packen melden Fehlmengen als 409 mit dem Standardcode `conflict` und dem Klartext in `detail`. |
| `order_not_packed` | 409 | Sendungen gibt es nur für gepackte Bestellungen |
| `unknown_carrier` | 409 | Carrier unbekannt |
| `carrier_not_available` | 409 | Carrier nicht angebunden (DHL und UPS sind Platzhalter) |
| `tracking_number_required` | 400 | Tracking-Nr fehlt (Carrier ohne Anbindung: manuelle Eingabe) |

**Bestand, Wareneingang, Einkauf, Retouren**

| Code | Status | Bedeutung |
|---|---|---|
| `lot_expiry_mismatch` | 409 | dieselbe Charge im selben Lagerplatz mit anderem MHD: eine Charge hat genau ein MHD |
| `quantity_zero`, `quantity_out_of_range` | 400 | Menge 0 bzw. außerhalb der Grenzen |
| `article_not_found`, `bin_not_found` | 404 | Artikel bzw. Lagerplatz existiert nicht |
| `po_not_receivable` | 409 | Bestellung erwartet keine Ware (nur versendete oder teilweise gelieferte) |
| `inbound_draft_exists` | 409 | zur Bestellung gibt es schon einen offenen Wareneingang |
| `po_over_receipt` | 409 | Wareneingang übersteigt die offene Bestellmenge |
| `return_order_not_delivered` | 409 | Retoure zu einer Bestellung erst ab Packed |
| `return_quantity_exceeded` | 409 | Retourenmenge übersteigt die gelieferte Menge |
| `return_bundle_not_allowed` | 409 | Bundles werden nicht retourniert, nur ihre Komponenten |
| `invalid_qc_result` | 400 | unbekanntes QC-Ergebnis (erlaubt: Pending, Sellable, BGrade, Defect, Destroy) |
| `inbound_without_po` | 409 | beim Anlegen eines Wareneingangs mit `lines` ist kein Bezug zu einer Bestellzeile (`purchaseOrderLineId`) möglich: dafür `create-inbound` der Bestellung nutzen |

**System, Backup und Restore** (Details: [features/backup-restore.md](features/backup-restore.md))

| Code | Status | Bedeutung |
|---|---|---|
| `development_only` | 403 | Reseed und Bulk-Seed gibt es nur in der Umgebung `Development` |
| `restore_disabled` | 403 | Restore ist in dieser Umgebung gesperrt (`Backup:AllowRestore` fehlt) |
| `sqlite_only` | 400 | Backup und Restore der Anwendung gibt es nur für SQLite (MySQL: `mysqldump`) |
| `database_not_file_based` | 400 | die Datenbank liegt nicht in einer Datei (In-Memory): kein Backup, kein Restore |
| `invalid_backup_name` | 400 | der Dateiname entspricht nicht dem Muster einer Sicherung (kein Pfad, nicht die Live-Datenbank) |
| `confirmation_required` | 400 | beim Restore fehlt die Bestätigung: das Formularfeld `confirm` muss `RESTORE` enthalten |
| `invalid_backup_file` | 400 | die Datei ist keine gültige Sicherung (SQLite-Header, Integritätsprüfung, Pflichttabellen, Schemastand); die laufende Datenbank bleibt unberührt |
| `database_in_use` | 409 | die Datenbankdatei ist noch in Benutzung (Windows); nichts wurde getauscht |
| `backup_in_use` | 409 | das Backup wird gerade gelesen und lässt sich nicht löschen |
| `database_file_missing` | 500 | die Datenbankdatei wurde nicht gefunden |
| `reseed_failed` | 500 | Reseed abgebrochen und zurückgerollt |

**CSV-Import und -Export** (Details: [features/csv-import-export.md](features/csv-import-export.md))

| Code | Status | Bedeutung |
|---|---|---|
| `import_file_too_large` | 400 | die Datei ist größer als 5 MB |
| `import_empty` | 400 | die Datei ist leer |
| `import_no_rows` | 400 | die Datei hat nur eine Kopfzeile, keine Datenzeilen |
| `import_missing_column` | 400 | eine Pflichtspalte fehlt (die Meldung nennt die gefundenen Spalten und erinnert an das Trennzeichen) |
| `import_duplicate_column` | 400 | dieselbe Spalte kommt mehrfach vor |
| `import_too_many_rows` | 400 | mehr als 20.000 Datenzeilen |
| `csv_invalid` | 400 | die Datei ist kein gültiges CSV (z. B. nicht geschlossenes Anführungszeichen, mit Zeilennummer) |
| `invalid_delimiter` | 400 | `delimiter` ist weder `semicolon` noch `comma` |
| `invalid_range` | 400 | der Zeitraum (`from`/`to`) ist verkehrt oder nicht lesbar |

Fehler **einzelner Zeilen** eines Imports stehen nicht als HTTP-Fehler, sondern im Ergebnis (`errors[].code`); ihre Codes (`invalid_gtin`, `unknown_sku`, `duplicate_order_number` ...) stehen in [features/csv-import-export.md](features/csv-import-export.md#fehlercodes).

---

## Endpunkte

Alle Pfade beginnen mit `/api`. `{id}` und ähnliche Platzhalter sind GUIDs. Alle POST- und PUT-Endpunkte erwarten einen JSON-Body (Ausnahme: reine Zustandswechsel wie `.../cancel`, `.../release`).

### Auth

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| POST | /api/auth/login | anonym | Anmelden, liefert Token; je IP rate-limitiert |
| GET | /api/auth/me | angemeldet | aktueller Benutzer; auch bei ausstehendem Passwortwechsel |
| POST | /api/auth/change-password | angemeldet | Passwort ändern, liefert neues Token; auch bei ausstehendem Passwortwechsel |

### Benutzer

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/users | Admin | alle Benutzer |
| POST | /api/users | Admin | Benutzer anlegen (Passwortwechsel-Zwang standardmäßig an) |
| PUT | /api/users/{id} | Admin | Rollen, E-Mail und Anzeigename ändern |
| POST | /api/users/{id}/reset-password | Admin | Passwort zurücksetzen (hebt die Sperre auf) |
| POST | /api/users/{id}/activate | Admin | Konto aktivieren |
| POST | /api/users/{id}/deactivate | Admin | Konto deaktivieren (kein Löschen) |

### System

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| POST | /api/admin/reseed | Admin | Fachdaten löschen und Demo-Daten neu anlegen; **nur `Development`** (sonst 403). Benutzer und Audit-Trail bleiben. |
| POST | /api/admin/seed-bulk | Admin | zusätzliche Zufallsdaten (`?count=`, 1 bis 100000); **nur `Development`** |
| GET | /api/admin/backup-settings | Admin | Einstellungen und Zustand für die Oberfläche: Zeitplan, nächster Lauf (UTC), Aufbewahrung, `restoreAllowed`, bei MySQL die `mysqldump`-Aufrufe; keine Serverpfade |
| GET | /api/admin/backups | Admin | Sicherungsdateien (`name`, `sizeBytes`, `createdUtc`, `kind`: `backup` oder `before-restore`), neueste zuerst; nur SQLite |
| POST | /api/admin/backups | Admin | jetzt ein Backup anlegen (konsistenter Snapshot per `VACUUM INTO`), danach greift die Aufbewahrung; 201 mit dem Listeneintrag; nur SQLite, unverschlüsselt |
| GET | /api/admin/backups/{name} | Admin | Backup-Datei herunterladen (`Content-Disposition: attachment`, nicht zwischenspeicherbar); 400 bei ungültigem Namen, 404 wenn es sie nicht gibt |
| DELETE | /api/admin/backups/{name} | Admin | Backup oder Sicherheitskopie löschen; 204, 400, 404 |
| POST | /api/admin/backup | Admin | wie `POST /api/admin/backups`, aber mit der älteren Antwortform (`message`, `fileName`, `sizeBytes`, Status 200); bleibt für bestehende Skripte |
| POST | /api/admin/restore | Admin | `multipart/form-data` mit `file` (hochgeladene SQLite-Datei, bis 1 GB) **oder** `backupName` (Backup aus der Liste) und `confirm=RESTORE` (bei `backupName` Pflicht); nur `Development` oder mit `Backup:AllowRestore=true`, danach Neustart (`restartRequired`, `safetyBackup`) |
| GET | /api/audit | Manager | Audit-Protokoll, neueste zuerst (`entity`, `id`, `take`, höchstens 1000) |
| GET | /api/audit/entity-types | Manager | Entitätstypen im Protokoll |
| GET | /api/version | angemeldet | App-Version (`appVersion`) und Stand des Datenbankschemas (`schemaVersion`: höchste angewendete Schema-Schritt-Nummer, `schemaStep`: Name dieses Schritts); jeder Angemeldete, keine bestimmte Rolle |

### Artikel

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/articles | angemeldet | alle Artikel; mit `?search=` nur die, in deren Name, SKU, Alternativ-SKU oder GTIN der Suchtext vorkommt (Teilstring, ohne Paging) |
| GET | /api/articles/by-code/{**code} | angemeldet | Artikel zu einem gescannten Code auflösen: GTIN/EAN (auch als UPC-A oder GTIN-14), SKU oder, wenn beides nichts findet, Alternativ-SKU. 200 = Artikel, 404 = unbekannt, 409 `ambiguous_code` mit `candidates`; der Code darf `/` enthalten |
| GET | /api/articles/{id} | angemeldet | ein Artikel |
| POST | /api/articles | Manager | Artikel anlegen (auch GTIN, Alternativ-SKUs, Saison-Fenster, Bundle-Komponenten) |
| PUT | /api/articles/{id} | Manager | Artikel ändern; `gtin` und `bundleComponents` fehlend/`null` = unverändert, leerer Text bzw. leere Liste = entfernen (bei `alternativeSkus`, `validFrom` und `validUntil` setzt ein fehlender Wert zurück) |

### Lager und Layout

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/warehouse/layout | angemeldet | Lager, Zonen, Gänge, Regale, Lagerplätze |
| GET | /api/warehouse/{id} | angemeldet | ein Lager |
| POST | /api/warehouse | Manager | Lager anlegen (`code`, `name`); 409 `duplicate_code` |
| PUT | /api/warehouse/{id} | Manager | Lager umbenennen |
| DELETE | /api/warehouse/{id} | Manager | Lager samt leerer Unterstruktur, Wänden und Pickpunkten löschen (409 `warehouse_not_empty`, wenn darunter Bestand liegt oder eine offene Aufgabe auf einen Lagerplatz zeigt; 409 `in_use` bei Belegen) |
| POST | /api/warehouse/zones | Manager | Zone in einem Lager anlegen |
| PUT | /api/warehouse/zones/{id} | Manager | Zone ändern |
| DELETE | /api/warehouse/zones/{id} | Manager | Zone samt leerer Unterstruktur löschen |
| POST | /api/warehouse/aisles | Manager | Gang in einer Zone anlegen (Ausrichtung `AlongX` oder `AlongY`) |
| PUT | /api/warehouse/aisles/{id} | Manager | Gang ändern |
| DELETE | /api/warehouse/aisles/{id} | Manager | Gang samt leeren Regalen löschen |
| GET | /api/warehouse/storage-locations | angemeldet | alle Lagerplätze |
| POST | /api/warehouse/storage-locations | Manager | Lagerplatz in einem Regal anlegen |
| PUT | /api/warehouse/storage-locations/{id} | Manager | Code, Maße und Höchstgewicht eines Lagerplatzes ändern |
| PUT | /api/warehouse/storage-locations/{id}/position | Manager | Lagerplatz verschieben |
| PUT | /api/warehouse/storage-locations/{id}/bin-type | Manager | Typ (Standard, HotPick, Reserve) und Nachschub-Schwelle setzen |
| DELETE | /api/warehouse/storage-locations/{id} | Manager | Lagerplatz löschen (409 `in_use`, wenn Bestand oder Belege ihn verwenden) |
| POST | /api/warehouse/shelves | Manager | Regal in einem bestehenden Gang anlegen (optional mit Fächern) |
| PUT | /api/warehouse/shelves/{id} | Manager | Code und Abmessungen eines Regals ändern |
| DELETE | /api/warehouse/shelves/{id} | Manager | Regal samt leeren Lagerplätzen löschen |
| PUT | /api/warehouse/shelves/{id}/position | Manager | Regal verschieben |
| POST | /api/warehouse/shelves/{id}/bins | Manager | weiteren Lagerplatz im Regal anlegen |
| GET | /api/warehouse/walls | angemeldet | Wände |
| POST | /api/warehouse/walls | Manager | Wand (Polylinie) anlegen |
| PUT | /api/warehouse/walls/{id}/points | Manager | Wandpunkte ändern |
| DELETE | /api/warehouse/walls/{id} | Manager | Wand löschen |
| GET | /api/warehouse/pick-points | angemeldet | Pickpunkte (Start/Ende) |
| POST | /api/warehouse/pick-points | Manager | Pickpunkt anlegen |
| PUT | /api/warehouse/pick-points/{id} | Manager | Beschriftung und Typ ändern |
| PUT | /api/warehouse/pick-points/{id}/position | Manager | Pickpunkt verschieben |
| DELETE | /api/warehouse/pick-points/{id} | Manager | Pickpunkt löschen |

Die Lagerstruktur (Lager, Zonen, Gänge, Regale, Lagerplätze) lässt sich vollständig per API und in der Oberfläche (Seite **Lagerstruktur**) pflegen. Codes sind ohne Beachtung der Schreibweise eindeutig (Lagerplatz-Codes im ganzen System), Löschen prüft Bestand, offene Aufgaben und Belege vorab und löscht nie teilweise; alle Regeln und Grenzen: [features/lager-stammdaten.md](features/lager-stammdaten.md).

### Bestand

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/stock | angemeldet | Bestandszeilen (je Artikel, Lagerplatz, Charge) |
| GET | /api/stock/summary | angemeldet | Summe je Artikel |
| GET | /api/stock/article/{articleId} | angemeldet | Bestandszeilen eines Artikels |
| POST | /api/stock/adjust | Manager | manuelle Korrektur (`delta`, optional `lotNumber`, `expiryDate`); bucht ins Ledger |
| GET | /api/stock/alerts | angemeldet | Artikel unter Mindest- bzw. Meldebestand (`critical`, `warning`) |
| GET | /api/slotting/suggestions | angemeldet | Umlagerungs-Empfehlungen (`rangeDays`, `top`) |
| GET | /api/slotting/putaway | angemeldet | Einlagerungs-Vorschläge (`articleId`, `quantity`, `top`) |

### Wareneingang und Einkauf

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/inbound | angemeldet | Wareneingänge |
| GET | /api/inbound/{id} | angemeldet | ein Wareneingang |
| POST | /api/inbound | Receiver | Wareneingang im Entwurf anlegen (`shipmentNumber` ist Pflicht). Mit dem optionalen Feld `lines` (Artikel, Ziel-Lagerplatz, Menge, Charge, MHD; höchstens 500) entstehen Kopf und Positionen **atomar** in einem Aufruf: scheitert eine Zeile, entsteht nichts (Meldung `Zeile n: ...`). Ohne `lines` entsteht ein leerer Kopf, der über `.../lines` gefüllt wird |
| POST | /api/inbound/{id}/lines | Receiver | Position ergänzen (Artikel, Ziel-Lagerplatz, Menge, Charge, MHD) |
| DELETE | /api/inbound/{id}/lines/{lineId} | Receiver | Position entfernen |
| POST | /api/inbound/{id}/receive | Receiver | Wareneingang buchen (Bestand + Ledger) |
| POST | /api/inbound/{id}/cancel | Manager | Entwurf stornieren |
| GET | /api/purchase-orders | angemeldet | Einkaufsbestellungen |
| GET | /api/purchase-orders/suggestions | angemeldet | Bestellvorschläge nach Lieferant |
| GET | /api/purchase-orders/{id} | angemeldet | eine Einkaufsbestellung |
| POST | /api/purchase-orders | Manager | anlegen (Nummer `PO-JJJJMMTT-NNNNN`) |
| POST | /api/purchase-orders/{id}/lines | Manager | Zeile ergänzen (nur Entwurf) |
| DELETE | /api/purchase-orders/{id}/lines/{lineId} | Manager | Zeile entfernen (nur Entwurf) |
| POST | /api/purchase-orders/{id}/send | Manager | versenden (danach nicht mehr bearbeitbar) |
| POST | /api/purchase-orders/{id}/lines/{lineId}/receive | Receiver | empfangene Menge einer Zeile fortschreiben; **bucht keinen Bestand** (nur Statuskorrektur) |
| POST | /api/purchase-orders/{id}/create-inbound | Receiver | Wareneingang im Entwurf aus den offenen Mengen erzeugen (`targetBinId`); der Bestand entsteht erst mit `.../receive` |
| POST | /api/purchase-orders/{id}/cancel | Manager | stornieren (nicht mehr nach vollständigem Empfang) |

### Inventur und Nachschub

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/inventory | angemeldet | Inventuren |
| GET | /api/inventory/{id} | angemeldet | eine Inventur mit Zeilen |
| POST | /api/inventory/start | Receiver | Inventur starten (Snapshot der Bestandszeilen, optional nur ein Lagerplatz) |
| PUT | /api/inventory/{id}/lines/{lineId} | Receiver | Zählmenge und Grund erfassen |
| POST | /api/inventory/{id}/reconcile | Manager | abgleichen und Differenzen buchen |
| POST | /api/inventory/{id}/cancel | Manager | abbrechen |
| GET | /api/replenishment | angemeldet | alle Nachschub-Aufgaben |
| GET | /api/replenishment/open | angemeldet | offene Aufgaben |
| POST | /api/replenishment/scan | Manager | Hot-Pick-Plätze prüfen und Aufgaben anlegen (idempotent) |
| POST | /api/replenishment/{id}/complete | Picker | Umlagerung buchen (`actualQty`) |
| POST | /api/replenishment/{id}/cancel | Manager | Aufgabe abbrechen |

### Bestellungen und Kunden

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/orders | angemeldet | Bestellungen mit Bestandsampel (`hasStockNow`, `hasStockAfterFifo`) |
| GET | /api/orders/{id} | angemeldet | eine Bestellung |
| POST | /api/orders/manual | Manager | Bestellung manuell anlegen (Quelle `Manual`) |
| POST | /api/orders | Manager | **externe Bestell-API** (Quelle `Api`), wiederholbar über `externalReference` oder Header `Idempotency-Key`: erste Anlage 201, Wiederholung 200 mit Header `Idempotent-Replayed: true`. Positionen per `articleId` oder `sku`. Für Maschinen-Zugriffe einen eigenen Benutzer mit Rolle Manager anlegen. |
| POST | /api/orders/{id}/cancel | Manager | stornieren (aus New, Picking, Picked; ohne Bestandseffekt) |
| GET | /api/customers | angemeldet | Kunden (`?includeInactive=true`) |
| GET | /api/customers/{id} | angemeldet | ein Kunde mit Adressen |
| POST | /api/customers | Manager | Kunde anlegen |
| PUT | /api/customers/{id} | Manager | Kunde ändern |
| POST | /api/customers/{id}/addresses | Manager | Adresse ergänzen (Lieferung, Rechnung, beides) |
| DELETE | /api/customers/{id}/addresses/{addressId} | Manager | Adresse entfernen |
| POST | /api/customers/{id}/activate | Manager | aktivieren |
| POST | /api/customers/{id}/deactivate | Manager | deaktivieren |
| GET | /api/suppliers | angemeldet | Lieferanten (`?includeInactive=true`) |
| GET | /api/suppliers/{id} | angemeldet | ein Lieferant |
| POST | /api/suppliers | Manager | Lieferant anlegen |
| PUT | /api/suppliers/{id} | Manager | Lieferant ändern |
| POST | /api/suppliers/{id}/activate | Manager | aktivieren |
| POST | /api/suppliers/{id}/deactivate | Manager | deaktivieren |

### Kommissionierung, Packen, Wellen

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| POST | /api/picklists/generate | Manager | Pickliste für Bestellungen (Status New) erzeugen; Bestand wird allokiert, Bestellungen gehen auf Picking |
| POST | /api/picklists/generate-cart | Manager | Wagen-Pickliste: offene Bestellungen bis zur Kapazität des Pickwagens bündeln |
| GET | /api/picklists | angemeldet | Picklisten |
| GET | /api/picklists/{id} | angemeldet | eine Pickliste mit Positionen und Route |
| POST | /api/picklists/{id}/recalculate | Manager | Route einer offenen Liste neu berechnen |
| POST | /api/picklists/{id}/mark-picked | Picker | Picken abgeschlossen (Liste Picked, Bestellungen Picked); ohne Mengen |
| POST | /api/picklists/{id}/pack | Packer | Ist-Mengen bestätigen und Bestand abbuchen (genau einmal je Liste) |
| GET | /api/picklists/{id}/shipping-label.pdf | angemeldet | Lieferschein als PDF (eine Seite je Bestellung) |
| DELETE | /api/picklists | Admin | offene Picklisten löschen (Antwort `{ deleted, skippedCompleted }`); verpackte und stornierte bleiben |
| POST | /api/packing/{orderId}/plan | angemeldet | Packvorschlag (Kartonwahl) für eine Bestellung; reine Berechnung |
| GET | /api/cart-configs | angemeldet | Pickwagen-Konfigurationen |
| GET | /api/cart-configs/{id} | angemeldet | eine Konfiguration |
| POST | /api/cart-configs | Manager | anlegen |
| PUT | /api/cart-configs/{id} | Manager | ändern |
| DELETE | /api/cart-configs/{id} | Manager | löschen |
| GET | /api/pick-waves | angemeldet | Wellen |
| GET | /api/pick-waves/{id} | angemeldet | eine Welle |
| POST | /api/pick-waves | Manager | Welle anlegen (Nummer `W-JJJJMMTT-NNNN`) |
| POST | /api/pick-waves/{id}/orders | Manager | Bestellungen hinzufügen (nur offene Welle) |
| DELETE | /api/pick-waves/{id}/orders/{orderId} | Manager | Bestellung entfernen (nur offene Welle) |
| POST | /api/pick-waves/{id}/release | Manager | freigeben: erzeugt die konsolidierte Pickliste |
| POST | /api/pick-waves/{id}/cancel | Manager | abbrechen (offene Welle, oder freigegebene, solange keine Liste begonnen wurde) |

### Versand und Retouren

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/shipments/carriers | angemeldet | Carrier und ob sie verfügbar sind |
| GET | /api/shipments | angemeldet | Sendungen |
| GET | /api/shipments/{id} | angemeldet | eine Sendung |
| POST | /api/shipments | Packer | Sendung für eine **gepackte** Bestellung anlegen (Carrier, Maße und Gewicht sind Pflicht) |
| POST | /api/shipments/{id}/tracking | Packer | Tracking zuweisen (Status Labeled) |
| POST | /api/shipments/{id}/ship | Packer | an den Carrier übergeben (Shipped); die letzte offene Sendung setzt die Bestellung auf Shipped |
| POST | /api/shipments/{id}/delivered | Packer | Zustellung bestätigen |
| POST | /api/shipments/{id}/cancel | Manager | offene Sendung stornieren |
| GET | /api/returns | angemeldet | Retouren |
| GET | /api/returns/{id} | angemeldet | eine Retoure |
| POST | /api/returns | Receiver | Retoure anlegen (Nummer `RMA-JJJJMMTT-NNNNN`, optional `orderId`) |
| POST | /api/returns/{id}/lines | Receiver | Zeile ergänzen |
| PUT | /api/returns/{id}/lines/{lineId}/qc | Manager | Qualitätsprüfung: Sellable, BGrade, Defect, Destroy |
| POST | /api/returns/{id}/process | Manager | abschließen und nach QC-Ergebnis buchen |
| POST | /api/returns/{id}/cancel | Manager | abbrechen |

### Auswertungen

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/reports/dashboard | angemeldet | Kennzahlen im Zeitraum (`range` Tage) |
| GET | /api/reports/live-status | angemeldet | aktuelle Zähler (Bestellungen, Picklisten, Inventuren, Nachschub, Bestandsalarme) |
| GET | /api/reports/bin-heatmap | angemeldet | gepickte Positionen je Lagerplatz (`range`) |
| GET | /api/reports/abc-analysis | angemeldet | ABC-Klassen nach Pickmenge (`range`) |
| GET | /api/reports/dead-stock | angemeldet | Artikel mit Bestand ohne Bewegung (`days`) |
| GET | /api/reports/stock-trend/{articleId} | angemeldet | Bestandsverlauf aus dem Ledger (`days`); **nur per API** |
| GET | /api/reports/expiring | angemeldet | abgelaufene und bald ablaufende Bestandszeilen mit Menge > 0 (`days` 1 bis 3660, Standard 30), nach MHD sortiert; je Zeile `status` (`Expired`, `Critical`, `Soon`) und `daysUntilExpiry` |
| GET | /api/reports/charge/{lotNumber} | angemeldet | Charge-Rückverfolgung: Wareneingänge (mit Status), Bestand, Ledger-Bewegungen und möglicherweise betroffene Bestellungen; eine unbekannte Charge ist 404 |
| GET | /api/reports/stock-valuation | Manager | Lagerwert nach FIFO aus dem Ledger |
| GET | /api/reports/picker-performance | angemeldet | Picklisten, Positionen, Wegstrecke und Dauer je Picker (`range`) |

### CSV-Import und -Export

Alle Endpunkte verlangen die Rolle Manager (Manager und Admin). Format, Spalten, Fehlercodes und Grenzen: [features/csv-import-export.md](features/csv-import-export.md); Bedienung: [USAGE.md](USAGE.md#csv-import-und--export).

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/export/articles.csv | Manager | alle Artikel als CSV, nach SKU (dieselben Spalten wie der Import) |
| GET | /api/export/stock.csv | Manager | Bestand: eine Zeile je Artikel, Lagerplatz und Charge (nur Menge > 0) |
| GET | /api/export/orders.csv | Manager | Bestellungen: eine Zeile je Position, Kopfdaten wiederholt |
| GET | /api/export/movements.csv | Manager | das Ledger (alle Bestandsbuchungen), älteste zuerst; optional `from`, `to` |
| GET | /api/export/audit.csv | Manager | der Audit-Trail, älteste zuerst; optional `from`, `to`, `user` |
| POST | /api/import/articles | Manager | Artikel importieren (Upsert je SKU, idempotent) |
| POST | /api/import/stock | Manager | Bestand auf den Sollwert der Datei setzen (Differenz über das Ledger, Vorgangstyp `CsvImport`) |
| POST | /api/import/orders | Manager | neue Bestellungen anlegen (Zeilen mit derselben `OrderNumber` sind eine Bestellung) |

Die Exporte akzeptieren `delimiter=semicolon` (Standard, Excel-DE) oder `delimiter=comma`; sie liefern UTF-8 mit BOM, streamen die Zeilen direkt aus der Datenbank und sind nicht zwischenspeicherbar (`Cache-Control: no-store`). `from`/`to` sind UTC; ein `to` ohne Uhrzeit schließt den ganzen Tag ein. Die Importe sind `multipart/form-data` mit dem Formularfeld `file` (höchstens 5 MB und 20.000 Zeilen) und den Abfrageparametern `dryRun` (**Standard `true`**: nur prüfen, nichts schreiben), `skipErrors` (Standard `false`: bei einem Zeilenfehler wird nichts übernommen) und `delimiter`. Die Antwort (Trockenlauf und Übernahme gleich) enthält `rows`, `created`, `updated`, `unchanged`, `errorCount`, `errors` (Zeile, Schlüssel, Code, Meldung; höchstens 500), `warnings`, `summary`, `message`, `applied` und bei einer Übernahme die `importId`.

### Etiketten und Hardware

| Methode | Pfad | Rolle | Zweck |
|---|---|---|---|
| GET | /api/labels/bin/{binId}.zpl | Picker | ZPL-Etikett (Zebra) für einen Lagerplatz als Download (`text/plain`, UTF-8); `?copies=n` (1 bis 500, sonst 400) setzt `^PQ` |
| GET | /api/labels/article/{articleId}.zpl | Picker | ZPL-Etikett für einen Artikel (Code 128 der SKU), ebenfalls mit `?copies=n` |
| GET | /api/labels/order/{orderId}.zpl | Picker | ZPL-Etikett für eine Bestellung (Code 128 der Bestellnummer), ebenfalls mit `?copies=n` |
| GET | /api/hardware/scale/status | Packer | Waagen-Status (heute immer "nicht konfiguriert") |
| GET | /api/hardware/scale/read | Packer | Gewicht lesen (antwortet 204 ohne Inhalt, solange keine Waage angebunden ist) |

---

## Health-Endpunkte

Zwei anonyme Endpunkte außerhalb von `/api` für Monitoring, Reverse-Proxys und Container-Healthchecks (keine Rolle, kein Token):

| Methode | Pfad | Antwort |
|---|---|---|
| GET | /health/live | immer 200 `{"status":"Healthy"}` (der Prozess antwortet) |
| GET | /health/ready | 200 `{"status":"Healthy"}`, wenn die Datenbank erreichbar ist, sonst 503 `{"status":"Unhealthy"}` |

Die Antwort enthält nur den Status (keine Details, keine Pfade). Der Host-Header-Filter (`AllowedHosts`) gilt auch hier. Mehr: [CONFIGURATION.md](CONFIGURATION.md#health-endpunkte).

---

## Swagger und OpenAPI

Swagger UI (`/swagger`) und die OpenAPI-Beschreibung (`/swagger/v1/swagger.json`) laufen in der Umgebung `Development` oder, wenn `Swagger:Enabled=true` gesetzt ist (Umgebungsvariable `Swagger__Enabled`); `Swagger:Enabled=false` schaltet sie auch in `Development` ab. Ohne Schalter gibt es sie in `Production` nicht, auch nicht im Docker-Image. **Die Beschreibung ist ohne Anmeldung lesbar** (sie enthält keine Daten, die Endpunkte selbst bleiben geschützt): in einer erreichbaren Produktionsumgebung nur bewusst einschalten. Jede Operation hat eine Zusammenfassung, die Mindestrolle und die Erfolgs- und Fehlerantworten (Fehler als `application/problem+json`); die Kommentare der Controller und der DTOs kommen aus der XML-Dokumentation. Zum Ausprobieren: `POST /api/auth/login` ausführen, das `token` kopieren, oben auf **Authorize** klicken und den Wert eintragen (bei diesem Schema stellt Swagger `Bearer` selbst voran). Das Token der Weboberfläche gilt nicht in Swagger (andere Origin, eigener Speicher). Wer die Beschreibung offline braucht, kann das JSON aus einer Instanz mit Swagger speichern (`/swagger/v1/swagger.json`). Beispielanfragen für den Editor (Visual Studio, Rider, VS Code mit "REST Client") liegen in `src/Lager.Api/Lager.Api.http`. Was das Dokument enthält, wie es ein- und ausgeschaltet wird und wie Kommentare hineinkommen: [features/openapi.md](features/openapi.md).
