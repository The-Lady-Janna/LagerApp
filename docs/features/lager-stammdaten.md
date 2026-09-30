# Lager-Stammdaten: Lager, Zonen, Gänge, Regale, Lagerplätze pflegen

Ausführliche Referenz zur Lagerstruktur (Regeln, Grenzen, Schnittstellen). Die Kurzfassung für die Bedienung steht in [USAGE.md](../USAGE.md#lagerstruktur).

Die Lagerstruktur `Lager → Zone → Gang → Regal → Lagerplatz` lässt sich jetzt vollständig über API und Oberfläche pflegen. Vorher
gab es Lager, Zonen und Gänge nur im Demo-Seeder (`WH01`, `Z-A`, `A1`–`A3`): mit `Database:Seed=false` (Produktivbetrieb) konnte man kein
Lager aufbauen, und Bin-Typ und Nachschub-Schwelle ließen sich nur per Swagger einstellen.

## Bedienung (Oberfläche)

Seite **Lagerstruktur** (Sidebar, Gruppe *Stammdaten*, Route `/warehouses`, nur Rolle **Manager** oder Admin):

| Aktion | Wo | Hinweis |
|---|---|---|
| Lager anlegen / umbenennen | **+ Neues Lager**, **Bearbeiten** am Lager | Code (eindeutig im System) und Name |
| Zone anlegen / ändern | **+ Zone**, **Bearbeiten** an Lager bzw. Zone | Code und Name, Code eindeutig im Lager (Vorschlag `Z1`, `Z2` ...) |
| Gang anlegen / ändern | **+ Gang**, **Bearbeiten** | Code eindeutig in der Zone (Vorschlag `A1` ...), Ausrichtung entlang X oder Y |
| Regal anlegen | **+ Regal** am Gang | derselbe Dialog wie im Layout-Editor, der Gang steht schon fest; mit Anzahl und Maßen der ersten Lagerplätze |
| Regal ändern | **Bearbeiten** am Regal | Code und Abmessungen; die Position ändert der Layout-Editor |
| Lagerplatz anlegen / ändern | **+ Lagerplatz**, **Bearbeiten** | Code (Vorschlag `<Regal>-NN`), Maße, Höchstlast in kg |
| **Bin-Typ** und **Nachschub-Schwelle** | **Bin-Typ** am Lagerplatz | Standard, Hot-Pick oder Reserve; die Schwelle gilt nur für Hot-Pick (Bestand darunter = Nachschub-Aufgabe, siehe Replenishment) |
| Löschen | **Löschen** an jedem Knoten | Bestätigungsdialog nennt, was mitgelöscht wird (z. B. "2 Zonen, 3 Gänge, 8 Regale, 40 Lagerplätze"); Ablehnung durch den Server steht im Dialog |

Ohne Lager zeigt die Seite den Leerzustand **"Noch kein Lager – jetzt anlegen"**. Dasselbe erreichen Nutzer über die Seitenleiste
(`WarehouseSelector` verlinkt bei leerer Liste dorthin) und den Layout-Editor (Leerzustand mit Link). Der Baum zeigt Lager, Zonen und
Gänge aufgeklappt, Regale zugeklappt (ein Klick zeigt die Lagerplätze mit Bin-Typ, Schwelle, Maßen und Höchstlast).

Im **Layout-Editor** ist **+ Neues Regal** ohne Vorwissen über Gang-Ids bedienbar: gibt es genau einen Gang, ist er vorgewählt; gibt es
noch keinen Gang, aber ein Lager, legt der Dialog beim Anlegen die Zone `Z1` und den Gang `A1` mit an; gibt es kein Lager, verweist er
auf die Lagerstruktur. Der Knopf **+ Bin hinzufügen** im Regal-Panel schlägt einen Code vor, der im ganzen System noch frei ist.

## API

Alle Endpunkte unter `/api/warehouse`, Ändern nur **Manager** (Admin darf ebenfalls), Lesen jeder Angemeldete. Die bestehenden Endpunkte
(`layout`, `{id}`, `storage-locations` ..., `shelves` ..., `bin-type`, `walls`, `pick-points`) bleiben unverändert.

| Methode und Route | Zweck | Antwort |
|---|---|---|
| `POST /api/warehouse` | Lager anlegen `{code, name}` | 201 `WarehouseDto` |
| `PUT /api/warehouse/{id}` | Lager umbenennen `{code, name}` | 200 / 404 |
| `DELETE /api/warehouse/{id}` | Lager samt Unterstruktur, Wänden und Pickpunkten löschen | 204 / 404 / 409 |
| `POST /api/warehouse/zones` | Zone anlegen `{warehouseId, code, name, origin?}` | 201 `ZoneDto` (404 bei unbekanntem Lager) |
| `PUT /api/warehouse/zones/{id}` | Zone ändern `{code, name, origin?}` | 200 `ZoneDto` (mit Baum) / 404 |
| `DELETE /api/warehouse/zones/{id}` | Zone samt Unterstruktur löschen | 204 / 404 / 409 |
| `POST /api/warehouse/aisles` | Gang anlegen `{zoneId, code, startPosition?, endPosition?, orientation?}` | 201 `AisleDto` (Vorgabe: `AlongX`, Start im Ursprung, Ende 10 m weiter) |
| `PUT /api/warehouse/aisles/{id}` | Gang ändern (fehlende Lage/Ausrichtung = unverändert) | 200 `AisleDto` / 404 |
| `DELETE /api/warehouse/aisles/{id}` | Gang samt Regalen löschen | 204 / 404 / 409 |
| `PUT /api/warehouse/shelves/{id}` | Regal ändern `{code, widthMm, depthMm, heightMm}` | 200 `ShelfDto` / 404 |
| `DELETE /api/warehouse/shelves/{id}` | Regal samt Lagerplätzen löschen | 204 / 404 / 409 |
| `PUT /api/warehouse/storage-locations/{id}` | Lagerplatz ändern `{code, widthMm, depthMm, heightMm, maxWeightGrams}` | 200 `StorageLocationDto` / 404 |
| `PUT /api/warehouse/storage-locations/{id}/bin-type` | Bin-Typ und Schwelle (besteht schon) | `{"binType":"HotPick","replenishmentThreshold":10}` |

Die Rollen-Matrix aus WP02 (`tests/Lager.Tests/WP02/EndpointMatrix.cs`) muss jede dieser Routen als `Tier.Manager`-Zeile führen (der Test
`Endpoint_table_matches_the_real_routes` zählt alle Controller-Actions per Reflection); dieselben Rechte prüfen die Tests unter `tests/Lager.Tests/WP22`.

## Regeln

**Eindeutige Codes** (ohne Groß-/Kleinschreibung, Leerraum am Rand zählt nicht): Lager im ganzen System, Zone im Lager, Gang in der Zone,
Regal im Gang, **Lagerplatz im ganzen System** (er ist die Kennung für Bestand und Etiketten). Ein Konflikt ist **409** mit dem Code
`duplicate_code` und ändert nichts; das gilt auch für neue Regale, deren automatisch erzeugte Fach-Codes (`<Regal>-01` ...) schon vergeben
sind (das Regal entsteht dann gar nicht). Umbenennen auf den eigenen Code in anderer Schreibweise ist erlaubt. Wegen der globalen
Lagerplatz-Codes brauchen Regale mit Fächern in verschiedenen Gängen verschiedene Regalcodes.

**Löschen**: Lager, Zone, Gang und Regal sind nur löschbar, wenn darunter

- kein Lagerplatz mit **Bestand** (Menge über 0) liegt und
- keine **offene Aufgabe** auf einen Lagerplatz zeigt (offene Nachschub-Aufgabe als Quelle oder Ziel, laufende Inventur),

sonst **409** mit Code `warehouse_not_empty` und der Meldung, welche Lagerplätze es sind; es wird nichts (auch nichts teilweise) gelöscht.
Sonst wird die **leere Unterstruktur mitgelöscht** (Kaskade in der Anwendung, nicht über Datenbank-Fremdschlüssel): Zonen, Gänge, Regale,
Lagerplätze, leere Bestandszeilen (Menge 0) sowie beim Lager die Wände und Pickpunkte. Ein einzelner Lagerplatz (`DELETE storage-locations/{id}`)
antwortet bei Bestand oder offenen Aufgaben wie bisher mit **409 `in_use`**.

Verweisen noch **Belege** auf einen Lagerplatz (Wareneingang, Retoure, Pickliste, erledigte oder stornierte Nachschub-Aufgaben, abgeschlossene
Inventuren), lehnt der Server ebenfalls mit **409 `in_use`** ab - die Prüfung läuft vorab in der Anwendung, weil ältere Datenbanken nicht alle
Fremdschlüssel haben und dort sonst ein Verweis ins Leere bliebe. Ein Lagerplatz, der einmal benutzt wurde, bleibt deshalb bestehen.

**Eingaben** (Validatoren in `src/Lager.Api/Validation/WarehouseAdminValidators.cs`): Codes bis 64, Namen bis 256 Zeichen, Abmessungen von
Regal und Lagerplatz 1 bis 100.000 mm, Höchstgewicht 0 bis 100.000.000 g, Koordinaten wie überall ±1.000.000 mm; Verstöße sind 400 mit
Feldfehlern. Die Grenzen der Regal-Anlage (höchstens 500 Fächer je neuem Regal, Code-Längen) bleiben unverändert; zusätzlich fasst ein
Regal höchstens 500 Lagerplätze (Code `shelf_bin_limit`).

## Konfiguration

Keine neuen Schlüssel. Demodaten sind nicht nötig: ohne Demo-Modus (Standard) startet die Anwendung mit leerer Lagerstruktur, und
der Leerzustand führt durch das Anlegen. Mit dem Demo-Modus (`Demo:Enabled`, nur bei leerer Datenbank) entsteht das Demo-Lager `WH01` samt Zonen, Gängen,
Regalen und Lagerplätzen; es lässt sich wie jedes andere Lager ändern und löschen (bei einem Neustart mit leerer Datenbank würde es neu angelegt).

## Grenzen

- **Keine Umlagerung** (Bin-zu-Bin) und **keine Auslastungsansicht**: bewusst nicht Teil dieses Pakets (Roadmap).
- Verschieben eines Regals in einen anderen Gang, einer Zone in ein anderes Lager oder eines Lagerplatzes in ein anderes Regal gibt es nicht
  (neu anlegen und den Bestand umbuchen); die Position auf der Fläche ändert der Layout-Editor.
- Groß-/Kleinschreibung der **Lagerplatz-Codes** vergleicht die Datenbank (`lower()`): bei SQLite nur für ASCII-Zeichen (`ä` und `Ä` gelten dort
  als verschieden); identische Codes fängt der Unique-Index in jedem Fall ab (409 `duplicate`).
- Das Löschen prüft Bestand und Referenzen vorab, sperrt aber nicht gegen gleichzeitige Buchungen: bucht jemand im selben Augenblick Bestand
  auf einen Lagerplatz, verhindert bei neuen Datenbanken der Fremdschlüssel das Löschen (409 `in_use`).
- Ein Lager mit Wänden und Pickpunkten verliert sie beim Löschen ohne weitere Rückfrage (die Bestätigung nennt es).
