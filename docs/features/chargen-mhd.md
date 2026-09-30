# Chargen und MHD: Warnliste, Rückverfolgung, Lot/MHD in Wareneingang und Bestand

Ausführliche Referenz zu Chargen, MHD, Warnliste und Rückverfolgung. Die Kurzfassung für die Bedienung steht in [USAGE.md](../USAGE.md#chargen-mhd-und-rückverfolgung).

Die lot-genaue Bestandslogik (eine Bestandszeile je Artikel, Lagerplatz und Charge, FEFO, `lot_expiry_mismatch`) steht im Kern des Systems.
Die Oberfläche macht sie bedienbar: Charge und MHD im Wareneingang und in der Bestandsansicht, eine Warnliste ablaufender Chargen und eine
Rückverfolgungsseite. Dazu gehört auch die Behebung des früheren Wareneingangs-Fehlers "Lieferung ohne Zeilen" (siehe unten).

## Bedienung

| Wo | Was |
|---|---|
| **Wareneingang** (`/inbound`) | Je Zeile **Lot** und **MHD** (Datumsfeld). Beim Anlegen prüft die Maske: kein MHD in der Vergangenheit (Neuware; heute ist erlaubt), Menge 1 bis 1.000.000, Charge höchstens 64 Zeichen, dieselbe Charge im selben Lagerplatz nur mit einem MHD. Bei einem Artikel, der schon **mit MHD geführt wird** (es gibt Bestand mit MHD), sind Charge und MHD **Pflicht** (Hinweis in der Zeile). Eine halb ausgefüllte Zeile wird bemängelt statt still verworfen. Ohne Lieferschein-Nummer erzeugt die Maske `WE-yyyyMMdd-HHmmss` ("Auto wenn leer"). In den Details eines **Entwurfs** zeigt eine Pill, ob ein MHD schon abgelaufen oder kritisch ist. |
| **Bestand** (`/stock`) | **Übersicht pro Artikel**: Spalte "Nächstes MHD" (frühestes MHD mit Menge) mit Status-Pill; der Pfeil vor dem Artikel klappt die **Unterzeilen je Lagerplatz/Charge/MHD** auf (FEFO-Reihenfolge). **Detail pro Platz** hat eine MHD-Spalte. Chargennummern sind Links zur Rückverfolgung. **Buchung**: nach Artikel und Lagerplatz erscheint **Charge / MHD** mit den vorhandenen Chargen dieses Platzes (Nummer, MHD, Menge), bei einem Zugang zusätzlich "Neue Charge …" (Nummer + optionales MHD); ohne Auswahl gilt "Ohne Charge" bzw. beim Abgang die Automatik (chargenlose Zeile zuerst, dann früheste Charge). |
| **Reports** (`/reports`) | Karte **Ablaufende Chargen** mit Frist **7 / 30 / 90 Tage** (Standard 30): abgelaufene und bald ablaufende Bestandszeilen mit Status, Artikel, Lagerplatz, Charge (Link), MHD, Ablauf in Tagen und Menge, nach MHD sortiert (höchstens 50 Zeilen, der Rest steht in der Zählzeile). |
| **Chargen-Trace** (`/traceability`, Sidebar-Gruppe Auswertung) | Suche nach einer Chargennummer (auch per Adresse: `/traceability?lot=LOT-A`). Zeigt Kennzahlen, die **Wareneingänge** (mit Lieferungsstatus), den **aktuellen Bestand** je Lagerplatz (mit MHD-Status), alle **Bewegungen** aus dem Ledger (Vorgang, Artikel, Lagerplatz, Menge mit Vorzeichen, Beleg; Picklisten sind verlinkt) und die **möglicherweise betroffenen Bestellungen** (Link zur Bestellung und zur Pickliste). Eine unbekannte Charge ist "nicht gefunden", kein Fehler. Jeder Angemeldete darf die Seite nutzen. |

## MHD-Status

Alles in **UTC-Kalendertagen**. Am Ablauftag selbst ist die Ware noch verwendbar (`StockItem.IsExpired`: MHD vor heute).

| Status | Tage bis zum MHD | Farbe |
|---|---|---|
| `Expired` (abgelaufen) | < 0 | rot |
| `Critical` (kritisch) | 0 bis 7 | orange |
| `Soon` (bald) | 8 bis zur gewählten Frist | blau |
| OK (nur in der Bestandsansicht) | > 30 | grün |

Die Grenzen sind fest (**keine Konfiguration**): `ReportService.ExpiryCriticalDays = 7` im Server, gespiegelt in
`frontend/lager-ui/src/features/traceability/expiry.ts` (`EXPIRY_CRITICAL_DAYS`, `EXPIRY_SOON_DAYS = 30` für die Bestandsansicht).

## API

### `GET /api/reports/expiring?days=30` (jeder Angemeldete)

Bestandszeilen mit **Menge > 0** und MHD, das abgelaufen ist oder innerhalb von `days` Tagen abläuft (`days` einschließlich: bei 30 ist
der Tag +30 dabei, +31 nicht). `days` gilt wie jeder `days`-Parameter der API: **1 bis 3660** (Standard 30), sonst 400. Ergebnis
sortiert nach MHD, dann SKU und Lagerplatz:

```json
[{ "stockItemId": "…", "articleId": "…", "articleSku": "ART-001", "articleName": "Joghurt", "binId": "…", "binCode": "A-01-01",
   "lotNumber": "LOT-A", "quantity": 4, "expiryDate": "2026-06-10T00:00:00", "daysUntilExpiry": -5, "status": "Expired" }]
```

`daysUntilExpiry` ist negativ bei abgelaufener Ware, 0 am Ablauftag. Zeilen ohne MHD und Zeilen mit Menge 0 kommen nie vor. Die Berechnung
liegt in `ReportService.ExpiringStockAsync`, die Abfrage in `ReportQueryGateway.ExpiringStockAsync`.

### `GET /api/reports/charge/{lotNumber}` (Charge-Trace, bestehend, erweitert)

Die Route bleibt (`charge/{lotNumber}`, nicht `charge-trace`). Die Chargennummer wird wie beim Bestand **getrimmt**; eine leere Nummer ist
400, eine unbekannte 404. Neu (alle Felder rückwärtskompatibel, nur ergänzt):

- `inbounds[].status` (`Draft`/`Received`/`Cancelled`) und `inbounds[].targetBinCode`. `inboundTotal` zählt **nur gebuchte** (`Received`) Eingänge;
  Entwürfe und stornierte Lieferungen stehen in der Liste, zählen aber nicht mit.
- `movements[].binCode`, `movements[].articleId`, `movements[].articleSku`.
- `orders[]`: **möglicherweise betroffene Bestellungen** (`orderId`, `orderNumber`, `customerReference`, `status`, `pickListId`, `pickListNumber`, `pickedAt`).

Herleitung der Bestellungen: Die Pick-Buchungen der Charge im Ledger (`ReferenceType = "PickList"`) verweisen auf eine Pickliste; deren
Positionen mit **gleichem Artikel und gleichem Lagerplatz** gehören zu den Bestellungen. Neueste zuerst.

### `POST /api/inbound` mit Zeilen (Wareneingangs-Bug)

**Früherer Fehler:** Die Erfassungsmaske schickte beim Anlegen `lines` mit, der Server kannte das Feld nicht und legte nur den Kopf an - das
Ergebnis war eine **leere Lieferung**, die Zeilen gingen still verloren. **Behoben** (die "saubere" Variante, ein Aufruf, atomar):
`CreateInboundShipmentRequest` hat ein optionales Feld `lines` (Liste von `AddInboundLineRequest`). `InboundService.CreateAsync` prüft **alle**
Zeilen wie `POST /api/inbound/{id}/lines` (Artikel und Lagerplatz vorhanden, Menge, Charge, MHD nach dem Jahr 2000, dieselbe Charge im selben
Lagerplatz nur mit einem MHD) und legt Kopf und Zeilen mit **einem** `SaveChanges` an. Scheitert eine Zeile, entsteht nichts; die Meldung nennt
die Zeilennummer (`Zeile 2: …`; 404 `article_not_found`/`bin_not_found`, 400 bei ungültigen Werten, 409 `lot_expiry_mismatch`). Eine Bestellzeile
(`purchaseOrderLineId`) ist beim Anlegen nicht möglich (409 `inbound_without_po`), Höchstzahl 500 Zeilen. Ohne `lines` entsteht wie bisher eine
leere Lieferung, die über `POST {id}/lines` gefüllt wird. **Keine neue Route**, die Rollen bleiben (Receiver).

Die Maske prüft zusätzlich die Antwort: kommen weniger Zeilen zurück als gesendet (ein älterer Server), meldet sie es und behält die Eingaben.

## Grenzen

- **Chargennummern mit `/`** lassen sich über `charge/{lotNumber}` nicht abfragen: der Server löst den Schrägstrich (auch als `%2F`) als
  Pfadtrenner auf. Die Rückverfolgungsseite fragt solche Nummern nicht an, sondern erklärt es. Behebung: die Route als Catch-All
  (`charge/{**lotNumber}`) führen - dann muss die Zeile in `tests/Lager.Tests/WP02/EndpointMatrix.cs` mit angepasst werden. Alle anderen
  Sonderzeichen (Leerzeichen, `#`, `+`, Umlaute) funktionieren, wenn der Client sie kodiert (`encodeURIComponent`).
- **Bestellungen im Trace sind eine Näherung.** Eine Pickposition kennt die Charge nicht (`PickItem` hat keinen Bezug zur Bestandszeile; siehe [TODO.md](../../TODO.md)): liegen im Lagerplatz mehrere Chargen, kann eine Bestellung auch aus einer anderen Charge beliefert worden sein.
  Deshalb "möglicherweise betroffen". Wurde die Pickliste inzwischen gelöscht, fehlt die Verknüpfung.
- **"MHD-Pflege" ist nur erkennbar, nicht konfigurierbar.** Es gibt kein Stammdatenfeld "MHD-pflichtig" am Artikel; die Maske behandelt einen Artikel
  als MHD-pflichtig, sobald **Bestand mit MHD** existiert. Der Server erzwingt sie nicht.
- Das **MHD einer vorhandenen Charge** lässt sich nicht ändern (eine Charge hat je Lagerplatz genau ein MHD); ein Tippfehler wird über eine
  Korrektur mit Gegenbuchung berichtigt.
- Die MHD-Warnliste **warnt nur**; abgelaufene Chargen werden beim Erzeugen von Picklisten weiterhin übersprungen (bestehendes Verhalten des `StockAllocator`, nicht konfigurierbar).
- MHD-Tage zählen in UTC; ein Bediener in einer anderen Zeitzone sieht "heute" gegebenenfalls um einen Tag verschoben (die Erfassungsmaske
  prüft "nicht in der Vergangenheit" nach der Ortszeit des Nutzers).

## Folgearbeiten

- Die Rollenmatrix (`tests/Lager.Tests/WP02/EndpointMatrix.cs`) verlangt für jede Action eine Zeile; die Zeile `Get("api/reports/expiring")`
  (Stufe Authenticated) ist dort eingetragen. Wer die Route umbenennt oder die Charge-Trace-Route auf `charge/{**lotNumber}` umstellt, muss die Matrix mitziehen.
- Optional: `RuleForEach(x => x.Lines)` mit `AddInboundLineRequestValidator` in `CreateInboundShipmentRequestValidator`
  (`src/Lager.Api/Validation/StockFlowValidators.cs`), damit die Feldfehler der Zeilen als Validierungsantwort (`errors`) kommen; der Dienst prüft
  die Zeilen bereits selbst.

## Tests

- Server (`tests/Lager.Tests/WP23`): `ExpiringReportTests` (abgelaufen/heute/Tag 7/8/30/31, Menge 0, ohne MHD, Sortierung, `days`-Grenzen, Rollen),
  `ChargeTraceTests` (zwei Chargen desselben Artikels im selben Lagerplatz, verbrauchte Charge, Entwurf/storniert, Sonderzeichen),
  `InboundCreateWithLinesTests` (Lieferung mit Zeilen -> Zeilen da -> Buchen -> Bestand mit Lot/MHD; fehlerhafte Zeile legt nichts an).
- Oberfläche (`frontend/lager-ui/src/tests/WP23`): MHD-Logik und Zeilenprüfung, Rückverfolgungsseite mit gemockter API, Bestandsseite
  (Unterzeilen, Buchung mit Lot/MHD), Wareneingangs-Maske (Validierung, Body mit Zeilen, Fehler), MHD-Karte mit Frist-Filter.
