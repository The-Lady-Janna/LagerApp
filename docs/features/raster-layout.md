# Raster-Layout: das Lager wie eine Tabelle zeichnen

Ausführliche Referenz zum einfachen Layout-Editor (Regeln, Codes, Grenzen, Schnittstellen). Die Kurzfassung für die Bedienung steht in [USAGE.md](../USAGE.md#lager-layout-raster-einfach).

Der erweiterte Editor arbeitet frei in Millimetern: Regale ziehen, Wände als Polylinien klicken, Pickpunkte setzen. Für die meisten Lager
reicht weniger: ein Raster aus gleich großen Feldern, und jedes Feld ist entweder Laufweg, Wand, Regal oder ein besonderer Bereich. Genau
das ist das Raster-Layout. Der Server erzeugt daraus die gewohnten Objekte (Zonen, Gänge, Regale, Lagerplätze, Wände, Pickpunkte), deshalb
arbeiten Pickrouten, Einlagerung, Bestand, Etiketten und Inventur unverändert weiter.

## Feldarten

| Feldart | Wert in der API | Entsteht beim Speichern | Begehbar |
|---|---|---|---|
| Laufweg | `Walkway` (leeres Feld, wird nicht gespeichert) | nichts | ja |
| Wand | `Wall` | Wand für die Wegberechnung (Dicke = Feldgröße), Beschriftung „Wand“ | nein |
| Tür / Tor | `Door` | nichts (Lücke in der Wand) | ja |
| Hindernis / Säule | `Obstacle` | Wand, Beschriftung „Hindernis“ | nein |
| Regal | `Shelf` | Regal mit X Lagerplätzen vom Typ `Standard` | Zugriff von den Nachbarfeldern |
| Packpuffer | `PackingStorage` | Regal mit X Lagerplätzen vom Typ `Packing` | dito |
| Warenausgang | `OutgoingStorage` | Regal mit X Lagerplätzen vom Typ `Outgoing` | dito |
| Retouren | `Returns` | Regal mit X Lagerplätzen vom Typ `Returns` | dito |
| Start | `Start` | Pickpunkt Typ `Start` | ja |
| Ende | `End` | Pickpunkt Typ `End` | ja |
| Verpackung | `Packing` | Pickpunkt Typ `Both` (als Start oder Ende wählbar) | ja |
| Wareneingang | `GoodsReceipt` | nichts, markierte Fläche | ja |

Zusammenhängende Felder derselben Pickpunkt-Art (waagerecht/senkrecht benachbart) ergeben **einen** Pickpunkt auf dem Feld nahe der
Mitte des Bereichs; mehrere getrennte Bereiche heißen „Start 1“, „Start 2“ …. Wand- und Hindernisfelder werden zu möglichst langen geraden
Wänden zusammengefasst (zuerst waagerechte Folgen, die übrigen Felder senkrecht); das Rechteck jeder Wand deckt die Felder genau ab.

Wie im erweiterten Editor sind Regale **keine** Hindernisse der Wegberechnung: die Route führt zur Mitte des Regalfelds.

## Behälter und Positionen

- Die Feldgröße gilt für das ganze Raster (Standard 1 m, erlaubt 0,25 bis 5 m), dazu 1 bis 100 Spalten und Zeilen.
- Jedes Lagerfeld hat 1 bis 50 Behälter. Die Behältermaße (Breite, Tiefe, Höhe, Höchstlast) gelten für alle Lagerfelder; Breite und Tiefe
  höchstens eine Feldgröße.
- Das Regal füllt das Feld (linke obere Ecke = Spalte × Feldgröße, Zeile × Feldgröße), die Behälter stehen übereinander in der Feldmitte
  (Höhe 0, 1 × Behälterhöhe, 2 × …). Ändert sich die Feldgröße oder ein Behältermaß, werden Regale und Behälter beim Speichern angepasst.
- Mehr Behälter: neue kommen oben dazu. Weniger Behälter: die obersten fallen weg.

## Codes

Gangbasiert, je Lagerfeld-Art in einer eigenen Zone (`LAGER`, `PACKPUFFER`, `AUSGANG`, `RETOUREN`):

1. Jede **Reihe** gleichartiger Lagerfelder ist ein Gang. Ein Feld gehört zur waagerechten Reihe, wenn seine waagerechte Folge mindestens so
   lang ist wie die senkrechte; ein Doppelregal aus zwei Zeilen sind also zwei Gänge.
2. Gänge werden von oben nach unten, dann von links nach rechts nummeriert: `A01`, `A02` … (Packpuffer `PK01`, Warenausgang `WA01`,
   Retouren `RT01`).
3. Regal = Gang + Position in der Reihe (links nach rechts bzw. oben nach unten): `A01-03`. Behälter = Regal + Ebene von unten: `A01-03-2`.

**Einmal vergebene Codes bleiben.** Kommt später ein Feld vor eine bestehende Reihe, behalten die alten Regale ihre Nummern, das neue bekommt
die nächste freie (`A01-04`). Lagerplatz-Codes sind im ganzen System eindeutig: Sind die Codes schon in einem anderen Lager vergeben,
bekommen alle neuen Regale dieses Lagers den Lager-Code als Präfix (`WH02-A01-03-2`).

## Was beim Speichern passiert

`PUT /api/warehouse/{id}/grid` ersetzt alle Felder und gleicht das Lager in **einer** Transaktion ab:

- Lagerfelder an gleicher Stelle mit gleicher Art behalten ihr Regal (Id, Code, Bestand); geänderte Art = altes Regal weg, neues Regal.
- Entfernt werden nur Behälter **ohne** Bestand, offene Aufgaben (Nachschub, Inventur) und Belege (Wareneingang, Retoure, Pickliste).
  Trifft eine Änderung einen solchen Behälter, lehnt der Server das ganze Speichern ab (409 `grid_cells_in_use`) und nennt die Felder;
  der Editor markiert sie rot. Nichts wird teilweise geändert.
- Wände und Pickpunkte des Rasters werden neu berechnet; Pickpunkte behalten dabei ihre Id, solange es den Bereich noch gibt.
- Leere Raster-Gänge und -Zonen verschwinden.
- **Objekte aus dem erweiterten Editor bleiben unangetastet.** Der Editor blendet sie gestrichelt über dem Raster ein. Umgekehrt setzt das
  nächste Speichern des Rasters Regale, Wände und Pickpunkte, die aus dem Raster stammen, wieder an ihren Platz, auch wenn sie im
  erweiterten Editor verschoben wurden.

Zwei Manager, die gleichzeitig dasselbe Raster bearbeiten: das zweite Speichern scheitert mit 409 `grid_conflict` (die mitgeschickte
`expectedVersion` passt nicht mehr). Der Editor bietet „Erneut versuchen“, das lädt den neuen Stand.

## Pufferplätze

Die Bin-Typen `Packing`, `Outgoing` und `Returns` markieren Plätze für Ware, die nicht frei verfügbar ist (gepickt und wartet aufs
Verpacken, verpackt und wartet auf den Versand, zurückgekommen und noch nicht geprüft):

- Picklisten und Wellen ziehen keinen Bestand aus Pufferplätzen.
- Einlagerungsvorschläge und Slotting nennen sie nicht als Ziel (und Slotting schlägt keine Umlagerung aus ihnen heraus vor).
- Bestand, Bestandskorrektur, Inventur und Etiketten funktionieren wie bei jedem Lagerplatz.

Den Typ setzt das Raster beim Speichern; im erweiterten Editor bzw. in der Lagerstruktur lässt er sich auch von Hand setzen.

## Oberfläche

Seite **Lager-Layout**, Umschalter **Raster (einfach)** / **Erweitert (frei)** (in der URL `?mode=grid` bzw. `?mode=advanced`). Ohne Wahl
öffnet das Raster, wenn das Lager schon eins hat oder noch nichts gezeichnet ist; ein frei gezeichnetes Lager ohne Raster öffnet im
erweiterten Editor.

| Aktion | Bedienung |
|---|---|
| Rechteck malen | Feldart in der Palette wählen, im Raster ziehen |
| Felder markieren | Werkzeug **Auswählen**, klicken oder ziehen; Umschalt+Klick bzw. Umschalt+Pfeil erweitert |
| Feldart im Raster wählen | Rechtsklick, Doppelklick oder Enter (auch Umschalt+F10) auf der Markierung |
| Laufweg | Entf oder Rücktaste |
| Rückgängig / Wiederholen | Strg+Z / Strg+Y (bzw. Strg+Umschalt+Z), bis zu 100 Schritte |
| Behälter | Zahl „Behälter je Lagerfeld“: gilt beim Malen und ändert markierte Lagerfelder |
| Größe | **Größe und Behälter …**: Spalten, Zeilen, Feldgröße, Behältermaße; Verkleinern nennt vorher die wegfallenden Felder |
| Speichern / Verwerfen | Leiste über dem Raster; ungespeicherte Änderungen fragen vor dem Wechsel in den erweiterten Editor und vor dem Schließen der Seite |

Ohne Rolle Manager ist das Raster nur zu sehen. Das Panel neben dem Raster zeigt zur Markierung Art, Behälter, Regal-Code, Behälter-Codes und
belegte Behälter; ein Punkt oben rechts im Feld bedeutet „Behälter mit Bestand oder offener Aufgabe“.

## API

| Methode und Route | Rolle | Zweck |
|---|---|---|
| `GET /api/warehouse/{id}/grid` | angemeldet | Raster des Lagers (`exists: false` = noch keins, die Werte sind die Vorgaben), je Feld Art, Behälter, Regal-Id und -Code, belegte Behälter; dazu die Ids der vom Raster erzeugten Regale, Wände und Pickpunkte |
| `PUT /api/warehouse/{id}/grid` | Manager | Raster speichern und anwenden; Antwort: neues Raster und Zusammenfassung (Regale/Behälter neu und entfernt, Wände, Pickpunkte) |

Anfrage `PUT`:

```json
{
  "cellSizeMm": 1000, "columns": 30, "rows": 20,
  "containerWidthMm": 600, "containerDepthMm": 600, "containerHeightMm": 400, "containerMaxWeightGrams": 50000,
  "cells": [
    { "col": 1, "row": 1, "type": "Shelf", "containers": 4 },
    { "col": 0, "row": 0, "type": "Start" }
  ],
  "expectedVersion": "…"
}
```

Fehler: 400 `validation_failed` (unbekannte Feldart, Feld außerhalb des Rasters, doppeltes Feld, Behälterzahl, Maße), 404 (Lager unbekannt),
409 `grid_conflict`, 409 `grid_cells_in_use` mit `cells: [{ col, row, ref, containerCodes }]`.

## Datenmodell

Tabelle `GridLayouts`, eine Zeile je Lager (eindeutiger Index auf `WarehouseId`, Fremdschlüssel mit Löschweitergabe): Rastergröße,
Behältermaße, `CellsJson` (die belegten Felder samt Verknüpfung zum erzeugten Regal) und `ManagedJson` (erzeugte Zonen, Gänge, Wände,
Pickpunkte). Bestandsdatenbanken bekommen die Tabelle über den Schema-Schritt `3500_AddGridLayout`. Ein Lager mit Raster lässt sich wie
bisher löschen; das Raster geht mit.

## Grenzen

- Ein Feld ist entweder ganz Regal oder ganz Laufweg: Regale, die schmaler als ein Feld sind oder schräg stehen, zeichnet der erweiterte Editor.
- Ein vorhandenes, frei gezeichnetes Lager wird nicht automatisch ins Raster übernommen; seine Objekte lassen sich im Raster nachzeichnen
  (sie bleiben dabei bestehen).
- Der Wareneingang ist eine markierte Fläche ohne eigene Funktion; Einlagerungsrouten gibt es noch nicht.
