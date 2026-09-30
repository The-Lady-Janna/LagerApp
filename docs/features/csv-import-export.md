# CSV-Import und -Export: Artikel, Bestand, Bestellungen, Bewegungen und Audit

Ausführliche Referenz zu CSV-Import und -Export (Formate, Fehlercodes, Grenzen). Die Kurzfassung für die Bedienung steht in [USAGE.md](../USAGE.md#csv-import-und--export).

## Was es gibt

| Was | Wo | Für wen |
|---|---|---|
| **Export** von Artikeln, Bestand, Bestellungen, Bewegungen (Ledger) und Audit als CSV | Seite **Import & Export** (`/import-export`, Navigation "Stammdaten"), `GET /api/export/*.csv` | Auswertung in Excel, Buchhaltung, Sicherung von Stammdaten |
| **Import** von Artikeln, Bestand und Bestellungen aus CSV mit **Trockenlauf** und Zeilenfehlern | dieselbe Seite, `POST /api/import/{articles\|stock\|orders}` | Datenübernahme beim Start, Preislisten pflegen, Inventurergebnis einspielen |
| **Beispieldateien** | `docs/samples/articles.csv`, `stock.csv`, `orders.csv` | zum Ausprobieren (siehe unten) |

Rolle: alles verlangt **Manager** (Manager und Admin, Policy `Manager`). Die Navigation zeigt den Eintrag deshalb nur diesen Rollen; der Server
erzwingt die Rolle unabhängig davon (Viewer, Picker, Packer, Receiver bekommen 403).

## Bedienung

**Export:** Auf der Seite oben das **Trennzeichen** wählen (Semikolon = Excel-Standard mit Dezimalkomma, Komma = mit Dezimalpunkt), dann bei der
gewünschten Datei **Herunterladen**. Bei *Bewegungen* und *Audit* lässt sich ein **Zeitraum** (von / bis, "bis" schließt den ganzen Tag ein) wählen,
beim Audit zusätzlich ein **Benutzer**. Der Dateiname trägt die UTC-Zeit: `articles-20260930T101500Z.csv`.

**Import:**

1. Bei Artikel, Bestand oder Bestellungen **Datei importieren…** drücken, die CSV-Datei wählen und das Trennzeichen prüfen.
2. **Prüfen (Trockenlauf)** liest die Datei und prüft jede Zeile gegen die Regeln des Systems und den vorhandenen Datenstand - **ohne etwas zu schreiben**.
   Das Ergebnis: die Zusammenfassung **"x neu, y aktualisiert, z Fehler"**, die Zahl der unveränderten Datensätze, Hinweise (z. B. eine unbekannte Spalte,
   meist ein Tippfehler im Spaltennamen) und eine **Tabelle der Zeilenfehler** (Zeile der Datei, Schlüssel, Meldung).
3. **Übernehmen** schreibt. Gibt es Zeilenfehler, ist der Knopf gesperrt, bis die Datei korrigiert ist oder das Häkchen **"Fehlerhafte Zeilen auslassen"** gesetzt
   wird; dann gilt nur der fehlerfreie Rest. Wer Datei oder Trennzeichen nach der Prüfung ändert, muss neu prüfen: übernommen wird immer genau das, was geprüft wurde.
4. Die Übernahme läuft **in einer Transaktion**: scheitert irgendetwas, bleibt nichts zurück.

Eine sinnvolle Reihenfolge beim Start: **Artikel, dann Bestand, dann Bestellungen** (Bestand und Bestellungen nennen Artikel per SKU, der Bestand nennt Lagerplätze,
die vorher in der Lagerstruktur existieren müssen).

## Dateiformat

- **Kodierung:** Export UTF-8 **mit BOM** (daran erkennt Excel die Kodierung). Der Import liest UTF-8 mit und ohne BOM, UTF-16 mit BOM und - wenn die Bytes kein gültiges
  UTF-8 sind - **Windows-1252** (so speichert Excel-DE "CSV (Trennzeichen-getrennt)"). Umlaute bleiben erhalten.
- **Trennzeichen:** Semikolon (Standard) oder Komma, Zeilenende CRLF (der Import nimmt CRLF, LF und CR). Felder mit Trennzeichen, Anführungszeichen oder Zeilenumbruch
  stehen in Anführungszeichen (RFC 4180, `""` = ein Anführungszeichen); **Zeilenumbrüche in Feldern** sind erlaubt. Ein nicht geschlossenes Anführungszeichen oder Text
  hinter dem schließenden ist ein Fehler der ganzen Datei (mit Zeilennummer), keine still falsch gelesene Zeile. Eine Excel-Zeile `sep=;` vor der Kopfzeile wird übersprungen;
  leere Zeilen (auch `;;;`) zählen nicht.
- **Spalten** werden am Namen erkannt, nicht an der Position (Groß-/Kleinschreibung, Leerzeichen, Unterstriche und Bindestriche egal: `Purchase Price` = `purchase_price`).
  Unbekannte Spalten werden ignoriert und als Hinweis gemeldet. Eine Zeile mit **mehr Zellen als die Kopfzeile Spalten hat** wird abgelehnt (meist steckt ein Trennzeichen in einem
  nicht zitierten Text, alle folgenden Zellen wären verschoben).
- **Zahlen:** beim Schreiben Dezimalkomma (Semikolon-Datei) bzw. Dezimalpunkt (Komma-Datei); beim Lesen werden beide Schreibweisen angenommen: `12,50`, `12.50`, `1.234,56`,
  `1,234.56`, `1 234,5`, `12,50 €`. `1.234` (genau drei Ziffern hinter einem Punkt) heißt 1234. Preise mit mehr als zwei Nachkommastellen sind ein Fehler (kein stilles Runden).
- **Datum:** `2026-09-30` oder `30.09.2026` (auch mit Uhrzeit, `2026-09-30T14:30:00Z`); immer UTC. Wahrheitswerte: `true/false`, `ja/nein`, `1/0`.
- **Formel-Injection:** Ein Text, den Excel als Formel auswerten würde (beginnt mit `=`, `+`, `-`, `@`, Tab oder CR), bekommt im **Export** ein **Apostroph** vorangestellt
  (`=HYPERLINK(...)` wird `'=HYPERLINK(...)`: Excel zeigt Text statt zu rechnen). Zahlen, Daten und Bewegungsmengen (`-5`) bleiben unverändert. Der **Import** nimmt genau dieses Apostroph
  wieder weg, damit Export und Import verlustfrei zueinander passen.

## Export

| Endpunkt | Inhalt | Spalten |
|---|---|---|
| `GET /api/export/articles.csv` | alle Artikel, nach SKU | `Sku, Name, Description, Gtin, LengthMm, WidthMm, HeightMm, WeightGrams, IsStackable, StackingAxis, StackingIncrementMm, MaxStackCount, MinStock, ReorderPoint, MaxStock, PurchasePrice, SupplierCode, AlternativeSkus, ValidFrom, ValidUntil` (Alternativ-SKUs mit `\|` getrennt) |
| `GET /api/export/stock.csv` | Bestand: eine Zeile je Artikel, Lagerplatz und Charge (nur Menge > 0) | `Sku, ArticleName, Location, Quantity, LotNumber, ExpiryDate` |
| `GET /api/export/orders.csv` | Bestellungen: eine Zeile je Position, Kopfdaten wiederholt | `OrderNumber, Status, Source, CreatedAt, CustomerReference, Priority, DueDate, ExternalReference, Sku, Quantity` |
| `GET /api/export/movements.csv?from=&to=` | das Ledger (alle Bestandsbuchungen), älteste zuerst | `At, Sku, Location, QuantityDelta, Reason, ReferenceType, ReferenceId, LotNumber, ExpiryDate, UnitCost` |
| `GET /api/export/audit.csv?from=&to=&user=` | der Audit-Trail, älteste zuerst | `At, User, EntityType, EntityId, Operation, Changes` (JSON) |

Alle akzeptieren `delimiter=semicolon` (Standard) oder `delimiter=comma`. `from`/`to`: Datum oder Zeitpunkt in UTC; **"to" ohne Uhrzeit schließt den ganzen Tag ein**, mit Uhrzeit
ist es die exklusive Obergrenze; `from` ist inklusive. Ein verkehrter oder unlesbarer Zeitraum ist ein 400, bevor das erste Byte der Datei geschrieben ist. `user` vergleicht ohne Beachtung
der Schreibweise.

**Streaming:** die Zeilen werden aus der Datenbank gelesen und sofort geschrieben (`AsAsyncEnumerable`, eine Schreiboperation je Zeile); keine Tabelle liegt vollständig im Speicher,
auch nicht das Ledger oder der Audit-Trail eines langen Betriebs. Die Antwort kommt ohne Content-Length, `Cache-Control: no-store`.

Artikel-, Bestands- und Bestelldatei haben **dieselben Spalten wie der Import**: jede exportierte Datei lässt sich wieder importieren. Der **Kreislauf** Export -> Import derselben Artikel
meldet "0 neu, 0 aktualisiert, 0 Fehler" (alles unverändert); in ein leeres System importiert reproduziert er die Artikel (SKU, Maße, Schwellen, Preis, GTIN, Alternativen, Saison).
Anzeigespalten des Exports, die der Import nicht braucht (`ArticleName`, `Status`, `Source`, `CreatedAt`), ignoriert er ohne Hinweis. Lieferanten stehen als **Code** in der Datei: im Zielsystem
muss es den Lieferanten mit diesem Code geben, sonst ist die Zeile ein Fehler (`unknown_supplier`).

## Import

`POST /api/import/articles|stock|orders?dryRun=true|false&skipErrors=true|false&delimiter=semicolon|comma` als **Multipart** mit dem Formularfeld `file`.

- `dryRun` ist **standardmäßig `true`**: ein vergessener Parameter prüft nur und schreibt nichts. Mit `dryRun=false` wird übernommen.
- `skipErrors=true` übernimmt die fehlerfreien Zeilen auch bei Zeilenfehlern (ohne: bei einem Fehler wird **nichts** übernommen).
- Antwort (beide Modi):

```json
{
  "kind": "articles", "dryRun": true, "applied": false, "delimiter": "semicolon",
  "rows": 5, "created": 2, "updated": 1, "unchanged": 1, "errorCount": 1,
  "errors": [ { "line": 4, "key": "DEMO-1003", "code": "invalid_gtin", "message": "Prüfziffer der GTIN stimmt nicht (erwartet 4)." } ],
  "errorsTruncated": false, "warnings": [], "summary": "2 neu, 1 aktualisiert, 1 Fehler",
  "message": "Trockenlauf: 2 neu, 1 aktualisiert, 1 Fehler (1 unverändert). Es wurde nichts geschrieben.", "importId": null
}
```

`line` ist die Zeile der Datei (Kopfzeile = 1). Die Antwort nennt höchstens **500 Einzelfehler**, `errorCount` zählt immer alle. `applied` heißt: es wurde geschrieben; `importId` ist die Kennung der
Übernahme (Verweis im Ledger und im Audit).

**Der Trockenlauf sagt voraus, was die Übernahme tut:** die Übernahme führt dieselbe Prüfung aus (innerhalb der Transaktion) und schreibt nur, was sie als gültig eingestuft hat.
Geschrieben wird **ausschließlich über die vorhandenen Dienste** (ArticleService, StockBooking, OrderService): der Import umgeht keine Regel und kein Ledger.

### Artikel

Upsert je **SKU** (Groß-/Kleinschreibung egal), **idempotent**: derselbe Import zweimal ändert nichts - auch GTIN, Alternativ-SKUs und Saison-Fenster nicht (kein Speichern, kein Audit-Eintrag,
"unverändert").

| Spalte | Pflicht | Bedeutung |
|---|---|---|
| `Sku` | immer | Kennung, höchstens 64 Zeichen, in der Datei eindeutig |
| `Name` | neu | höchstens 256 Zeichen |
| `Description` | | höchstens 2000 Zeichen |
| `Gtin` | | Ziffern, 8/12/13/14 Stellen, Prüfziffer nach GS1, eindeutig gegen die Datenbank und die übrigen Zeilen der Datei |
| `LengthMm, WidthMm, HeightMm` | neu | 1 bis 1.000.000 |
| `WeightGrams` | | 0 bis 100.000.000 |
| `IsStackable, StackingAxis (X/Y/Z), StackingIncrementMm, MaxStackCount` | | Stapelregeln |
| `MinStock, ReorderPoint, MaxStock` | | Mindest- <= Melde- <= Höchstbestand (0 = ungesetzt) |
| `PurchasePrice` | | Euro, höchstens zwei Nachkommastellen |
| `SupplierCode` | | Code eines vorhandenen Lieferanten |
| `AlternativeSkus` | | getrennt mit `\|`, `,` oder `;` |
| `ValidFrom, ValidUntil` | | Saison-Fenster (das Ende gilt als Kalendertag inklusive) |

**Fehlt eine Spalte in der Datei, bleibt das Feld unverändert** (eine Preisliste mit nur `Sku;PurchasePrice` löscht keine Beschreibungen). **Eine vorhandene, aber leere Zelle setzt das Feld zurück**
(GTIN entfernen, Alternativen und Saison löschen). Bundle-Komponenten stehen nicht in der Datei: neue Artikel sind keine Bundles, vorhandene behalten ihre Komponenten.

### Bestand

Setzt den Bestand je **Artikel, Lagerplatz und Charge auf den Wert der Datei** (Sollbestand). Gebucht wird die **Differenz** zum aktuellen Bestand, **ausschließlich über StockBooking** (Grund *Adjust*,
Vorgangstyp `CsvImport`, Vorgangs-Id = Kennung des Imports): jede Änderung steht im Ledger, die Invariante "Summe der Buchungen = Bestand" bleibt erhalten. Bestandszeilen werden **nie direkt
eingefügt oder überschrieben**. Ist der Bestand schon gleich, passiert nichts. Zeilen der Datenbank, die nicht in der Datei stehen, bleiben unberührt (kein "Rest auf 0").

Spalten: `Sku`, `Location` (Lagerplatz-Code; auch `LocationCode` oder `Bin`), `Quantity` (ganze Zahl >= 0) - optional `LotNumber`, `ExpiryDate` (MHD). Charge und MHD gehören zur Bestandsidentität: dieselbe Charge
mit anderem MHD im selben Lagerplatz ist ein Fehler (`lot_expiry_mismatch`), auch innerhalb der Datei. Bundles haben keinen eigenen Bestand.

### Bestellungen

Legt **neue Bestellungen** an, **eine Zeile je Position**; Zeilen mit derselben `OrderNumber` gehören zu einer Bestellung. Angelegt wird über `OrderService.CreateAsync` mit denselben Regeln wie über die API. Eine Bestellung
entsteht ganz oder gar nicht: hat eine ihrer Zeilen einen Fehler, entsteht sie nicht.

Spalten: `OrderNumber`, `Sku`, `Quantity` (1 bis 100.000) - optional `CustomerReference`, `Priority` (0 bis 3), `DueDate`, `ExternalReference`. Die Kopfangaben dürfen in einer Zeile oder in allen (dann gleich) stehen. Eine **vorhandene
Bestellnummer ist ein Fehler** (`duplicate_order_number`) - außer die Zeile nennt dieselbe `ExternalReference` wie die vorhandene Bestellung: dann ist es eine Wiederholung und zählt als "unverändert". Bestehende Bestellungen werden
nie geändert. Kunde und Lieferadresse kennt die Datei nicht.

### Fehlercodes

Fehler einzelner Zeilen stehen im Ergebnis (`errors[].code`), Fehler der **ganzen Datei** sind ein **400** (`code` im Problem-Body).

| Art | Codes |
|---|---|
| Datei (400) | `validation_failed` (keine Datei, leer, größer als 5 MB, ungültiges Trennzeichen), `import_file_too_large`, `import_empty`, `import_no_rows`, `import_missing_column` (mit den gefundenen Spalten und dem Hinweis auf das Trennzeichen), `import_duplicate_column`, `import_too_many_rows`, `csv_invalid`, `invalid_delimiter`, `invalid_range` |
| Zeile, allgemein | `too_many_columns`, `invalid_number`, `invalid_boolean`, `invalid_date`, `invalid_price` |
| Zeile, Artikel | `sku_missing`, `sku_too_long`, `duplicate_sku_in_file`, `name_missing`, `name_too_long`, `description_too_long`, `invalid_dimension`, `invalid_weight`, `invalid_stacking`, `invalid_axis`, `invalid_stock_level`, `invalid_season`, `invalid_alternative_skus`, `invalid_gtin`, `duplicate_gtin`, `duplicate_gtin_in_file`, `unknown_supplier` |
| Zeile, Bestand | `sku_missing`, `unknown_sku`, `article_is_bundle`, `location_missing`, `unknown_location`, `quantity_missing`, `negative_quantity`, `quantity_out_of_range`, `lot_too_long`, `duplicate_row`, `lot_expiry_mismatch` |
| Zeile, Bestellung | `order_number_missing`, `order_number_too_long`, `unknown_sku`, `article_not_orderable` (außerhalb des Saison-Fensters), `invalid_quantity`, `invalid_priority`, `customer_reference_too_long`, `external_reference_too_long`, `order_header_mismatch`, `too_many_lines`, `duplicate_order_number` |

## Audit-Trail: ein Sammel-Eintrag

Ohne Gegenmaßnahme schriebe der `AuditingInterceptor` je Entität eine Audit-Zeile: ein Import von 1000 Artikeln ergäbe 1000 Zeilen, die den Trail fluten. Während eine Übernahme schreibt, ist deshalb der
**Sammelmodus** aktiv (`IAuditBatch`, scoped je Request): der Interceptor schreibt keine Einzelzeilen, sondern zählt die Änderungen und hängt mit dem letzten SaveChanges **einen** Eintrag an:

- `EntityType` = `CsvImport`, `Operation` = `Import`, `EntityId` = Kennung des Imports, `User` = der Benutzer.
- `Changes` = JSON mit `summary` (`"CSV-Import: 1200 Artikel, Nutzer admin"`; bei Bestand "Bestandszeilen", bei Bestellungen "Bestellungen"), Art, Dateiname, Zahlen (neu, aktualisiert, unverändert,
  ausgelassene Fehler) und den Zählungen je Entitätstyp (`"entities": {"Article": {"Added": 1200}}`).

Ein Trockenlauf und ein Import ohne Änderungen schreiben **keinen** Eintrag. Ohne aktiven Sammelmodus (jede normale Änderung) verhält sich das Auditing unverändert.

## Konfiguration und Grenzen

Es gibt **keine Konfigurationsschlüssel**; die Grenzen stehen im Code (`ImportLimits`):

| Grenze | Wert | Verhalten |
|---|---|---|
| Dateigröße | **5 MB** | 400 "Die Datei ist größer als 5 MB." (die Datei wird nie vollständig gelesen); die Oberfläche warnt schon vor dem Upload |
| Datenzeilen | **20.000** (ohne Kopfzeile) | 400 `import_too_many_rows` mit der Zahl; größere Datenmengen in mehrere Dateien aufteilen |
| Einzelfehler in der Antwort | **500** | `errorsTruncated = true`, `errorCount` bleibt vollständig |
| Anfragegröße | 5 MB + 256 KB | größere Anfragen lehnt der Server schon beim Lesen des Formulars mit 400 `validation_failed` ab (technische, englische Meldung des Frameworks "Request body too large", gemessen mit echtem Kestrel; kein 413). Dateien zwischen 5 MB und 5 MB + 256 KB bekommen die klare deutsche Meldung der Zeile oben |

Bekannte Einschränkungen:

- Der Import liest **CSV**, keine `.xlsx`-Dateien (in Excel "Speichern unter" -> "CSV (Trennzeichen-getrennt)" bzw. "CSV UTF-8").
- Bundle-Komponenten, Kunden, Lieferadressen, Lieferanten und Lagerstruktur lassen sich nicht importieren (Lagerplätze und Lieferanten müssen vorher existieren).
- Ein Bestandsimport kennt nur den **Sollbestand** je Zeile; Zu-/Abgänge (Delta-Buchungen) und das Löschen von Bestandszeilen sind nicht vorgesehen.
- Die Übernahme bucht Zeile für Zeile über die Dienste (Regeln und Ledger bleiben erhalten) und ist deshalb langsamer als ein Massen-INSERT: 1000 Artikel brauchen wenige Sekunden; 20.000 neue Artikel brauchten im Rauchtest (Debug-Build, SQLite) rund 95 Sekunden,
  ein Wiederholungsimport derselben Datei (alles unverändert) und der Trockenlauf unter einer Sekunde. Die Transaktion hält die Datenbank während dieser Zeit für Schreiber gesperrt (SQLite: andere Schreibzugriffe warten bis zu 5 Sekunden, danach scheitern sie).
- Zeitpunkte im Export sind ISO-8601 mit `Z` (`2026-09-30T14:30:00Z`); Excel zeigt sie als Text. Reine Daten (`2026-09-30`) erkennt Excel als Datum.

## Beispieldateien

`docs/samples/` enthält drei Dateien im Format des Exports (UTF-8 mit BOM, Semikolon, Dezimalkomma): `articles.csv` (5 Artikel, darunter einer mit GTIN, Alternativen und Saison-Fenster), `stock.csv` (Bestand mit Chargen und MHD)
und `orders.csv` (drei Bestellungen, eine mit zwei Positionen). **Reihenfolge:** Artikel, Bestand, Bestellungen. Die Bestandsdatei nennt die Lagerplätze `A-01-1`, `A-01-2`, `A-01-3`, `B-02-1`, `B-02-2`, `B-02-3`: entweder solche Lagerplätze
anlegen oder die Spalte `Location` durch vorhandene Codes ersetzen. Jede Datei zuerst mit **Prüfen (Trockenlauf)** ansehen. Ein Test (`SampleFilesTests`) spielt die Dateien in dieser Reihenfolge ein, damit sie nicht veralten.

## Tests

- Backend (`tests/Lager.Tests/WP24/`): CSV-Leser und -Schreiber (Anführungszeichen, Semikolon, Zeilenumbrüche, BOM, Formel-Injection, Dezimalkomma, Kodierungen), Zeitraum-Filter, Sammelmodus des Audits (Unit),
  Export-Endpunkte (Format, Dateiname, Filter, Rollen), Artikelimport (Upsert, Idempotenz, Trockenlauf schreibt nichts, Zeilenfehler, `skipErrors`, Kreislauf Export -> Import in ein leeres System, Excel-DE-Datei),
  Bestandsimport (Ledger, Invariante, Differenz), Bestellimport, **genau ein Audit-Eintrag bei 1000 Artikeln**, normales Auditing unverändert, Atomarität (Commit-Fehler rollt alles zurück), Grenzen (5 MB, 20.000 Zeilen),
  Viewer-403, Beispieldateien.
- Frontend (`frontend/lager-ui/src/tests/WP24/`): Dialog-Fluss mit gemocktem API (Prüfen, Fehlertabelle, Auslassen, Übernehmen, Sperren während der Übernahme), Export-Knöpfe und Filter, Route-Registry.
