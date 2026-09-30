# Benutzer-Handbuch

Dieses Dokument erklärt, **wie** die App im Alltag bedient wird, pro Workflow ein Abschnitt. Es geht nicht um die Installation (siehe [GETTING_STARTED.md](GETTING_STARTED.md)) und nicht um die Code-Architektur (siehe [ARCHITECTURE.md](ARCHITECTURE.md)). Wo die Oberfläche etwas (noch) nicht kann, steht es dabei, gesammelt im Abschnitt [Bekannte Einschränkungen](#bekannte-einschränkungen). Je Bereich gibt es eine ausführliche Referenz unter [features/](features/): die Abschnitte hier fassen sie zusammen und verweisen darauf.

---

## Inhaltsverzeichnis

- [Konzepte](#konzepte) — Lagerhierarchie, Pickpunkte, Wände, Bin-Typen, Bundles, GTIN, Chargen
- [Login & Benutzer](#login--benutzer) — Anmelden, Passwort, Sprache, Benutzer und Rollen
- [Stammdaten](#stammdaten) — Artikel, Lieferanten, Kunden, Pickwagen, Lager-Layout, Lagerstruktur
- [Wareneingang](#wareneingang)
- [Beschaffung](#beschaffung) — Bestellvorschläge und Einkaufsbestellungen
- [Bestand](#bestand)
- [Chargen, MHD und Rückverfolgung](#chargen-mhd-und-rückverfolgung)
- [Bestellungen](#bestellungen)
- [Picken](#picken) — Pickliste, Wagen-Pickliste, Welle, Mobile-Picker
- [Packen & Versand](#packen--versand)
- [Retouren](#retouren)
- [Inventur](#inventur)
- [Replenishment](#replenishment)
- [Reporting & Analytics](#reporting--analytics)
- [CSV-Import und -Export](#csv-import-und--export)
- [Etiketten](#etiketten)
- [System & Admin](#system--admin) — Audit, Backup und Restore, Demo-Modus, API-Beschreibung (Swagger), Wartung
- [Tastenkürzel](#tastenkürzel)
- [Bekannte Einschränkungen](#bekannte-einschränkungen)

---

## Konzepte

### Lagerhierarchie

```
Warehouse                  z.B. "Hauptlager" (WH01)
 └─ Zone                    z.B. "Zone A"
     └─ Aisle               z.B. "Gang A1"
         └─ Shelf           z.B. "Regal A1-01"
             └─ StorageLocation (= Bin)   z.B. "A1-01-01"
```

Jeder Knoten hat Koordinaten in Millimetern. Der Layout-Editor pflegt die Positionen, der Pickrouten-Optimierer nutzt sie. **Lager, Zonen, Gänge, Regale und Lagerplätze** legt die Seite **Lagerstruktur** an (Manager, siehe [Lagerstruktur](#lagerstruktur)); ohne Demo-Daten beginnt man dort mit dem ersten Lager. Regale und Lagerplätze lassen sich zusätzlich im Layout-Editor anlegen.

### Wände & Pickpunkte

- **Wände** sind Polylinien mit Dicke. Der Pickrouten-Optimierer behandelt sie als Hindernisse und routet darum herum (Sichtbarkeitsgraph und Dijkstra). **Regale gelten nicht als Hindernisse**, nur Wände.
- **Pickpunkte** sind die Start- und Endpunkte des Picker-Wegs (z. B. "Wareneingang" als Start, "Versand" als Ende), Typ Start, Ende oder Beides. Beim Erzeugen einer **Einzelbestellungs-Pickliste** und beim **Neuberechnen** der Route lassen sich Start und Ende wählen; die Wagen- und Wellen-Pickliste nehmen in der Oberfläche automatisch den ersten Start-Pickpunkt (die API erlaubt dort ebenfalls eine Angabe).

### Bin-Typen

Jeder Lagerplatz hat einen Typ:
- **Standard** — normaler Lagerplatz
- **HotPick** — schnell erreichbarer Platz mit kleinen Mengen; bekommt Nachschub
- **Reserve** — Großmengen-Platz, der die HotPick-Plätze beliefert

Plus eine **Nachschub-Schwelle** je HotPick-Platz: sinkt der Bestand darunter, legt der Replenishment-Scan eine Auffüll-Aufgabe an. **Typ und Schwelle stellt man auf der Seite Lagerstruktur ein** (Knopf **Bin-Typ** am Lagerplatz, Manager; per API `PUT /api/warehouse/storage-locations/{id}/bin-type`). Die Kommissionierung bevorzugt HotPick vor Standard vor Reserve.

### Bundles / Kits

Ein Artikel kann ein **Bundle** sein: eine Zusammenstellung aus mehreren Komponenten. Bestellt jemand ein Bundle, löst der Pickliste-Generator es in die Komponenten auf (auch verschachtelt, bis fünf Ebenen; zyklische Bundles werden abgelehnt). Das Bundle selbst hat keinen physischen Bestand; Bestandsampel und Packvorschlag rechnen mit den Komponenten. Retouren von Bundles werden abgelehnt, retourniert werden die Komponenten. Die Komponenten pflegt der **Artikel-Editor** (Abschnitt **Bundle / Kit**: Komponente per Artikelsuche über SKU, Name oder GTIN wählen, Menge je Bundle eintragen); der Editor sperrt Selbstreferenz, doppelte Komponenten und Zyklen, der Server prüft dasselbe (Codes `duplicate_bundle_component`, `unknown_bundle_component`, `bundle_cycle`, `bundle_too_deep`).

### Saison-Fenster und Alternativ-SKUs

Artikel können ein Saison-Fenster haben (**Gültig ab** / **Gültig bis (letzter Tag)**; das letzte Datum gilt noch vollständig, leer = ganzjährig). Beim **Anlegen einer Bestellung** lehnt der Server Artikel außerhalb ihres Fensters ab (Code `article_not_orderable`). **Alternativ-SKUs** pflegt der Editor als Chips. Sie wirken heute bei der **Artikelsuche** und bei der **Scan-Auflösung** (siehe unten): die Suche findet einen Artikel auch über seine Alternativ-SKU, und der Scan über `by-code` nutzt sie als Rückfall, wenn weder SKU noch GTIN passen. Einen **Ersatzartikel-Vorschlag** bei fehlendem Bestand gibt es nicht.

### GTIN und Scan-Auflösung

Jeder Artikel kann eine **GTIN/EAN** tragen (nur Ziffern, 8, 12, 13 oder 14 Stellen, Prüfziffer nach GS1; eindeutig über alle Artikel). Ein UPC-A und der EAN-13 mit führender Null zählen als dieselbe GTIN. `GET /api/articles/by-code/{code}` löst einen gescannten Code auf: zuerst SKU und GTIN, nur wenn dort nichts passt die Alternativ-SKU; unbekannt gibt 404, mehrdeutig 409 `ambiguous_code` mit den Kandidaten. Die Oberfläche nutzt diese Auflösung noch nicht (Mobile-Picker und Globalsuche suchen weiter nach SKU). Einzelheiten: [features/artikel-gtin.md](features/artikel-gtin.md).

### Chargen und MHD

Bestandszeilen und Wareneingangszeilen tragen optional eine **Charge** (`LotNumber`) und ein **MHD** (`ExpiryDate`):

- Bestand ist je **Artikel, Lagerplatz und Charge** eindeutig geführt: zwei Chargen im selben Lagerplatz sind zwei Bestandszeilen. Dieselbe Charge im selben Lagerplatz hat genau **ein** MHD; ein anderes MHD wird abgelehnt (`lot_expiry_mismatch`).
- **FEFO** beim Kommissionieren: frühestes MHD zuerst, Ware ohne MHD zuletzt. **Abgelaufene Chargen** (MHD vor heute, UTC) werden nicht gepickt.
- Charge und MHD erfasst die Oberfläche im **Wareneingang** und bei der **Bestandsbuchung**; die Retoure erfasst die Charge. Ablaufende Chargen zeigt die Karte **Ablaufende Chargen** in den Reports, die **Rückverfolgung** einer Charge die Seite **Chargen-Trace** (siehe [Chargen, MHD und Rückverfolgung](#chargen-mhd-und-rückverfolgung)).

---

## Login & Benutzer

### Anmelden

Login-Seite (Vollbild): Benutzername + Passwort. Nach erfolgreichem Login bekommt der Browser ein Token (8 Stunden gültig), gespeichert im `localStorage`. Läuft die Sitzung ab oder wird sie widerrufen (Konto deaktiviert, Passwort oder Rollen geändert), erscheint wieder die Anmeldeseite mit einem Hinweis.

Beim allerersten Start gibt es nur den Benutzer `admin`; sein **Einmalpasswort steht in der Konsole des Servers** (Docker: im Container-Log, siehe [GETTING_STARTED.md](GETTING_STARTED.md#3-erster-start)). Im **Demo-Modus** gibt es zusätzlich Benutzer je Rolle, deren Passwörter einmalig in der Konsole stehen (siehe [Demo-Modus](#demo-modus)). Nach fünf falschen Versuchen ist ein Konto 15 Minuten gesperrt. Die Fehlermeldung ist immer dieselbe ("Anmeldung fehlgeschlagen"), egal ob Benutzer, Passwort oder Sperre die Ursache sind.

### Passwort ändern

Im **Seitenleisten-Fuß** → **🔒 Passwort ändern**: aktuelles Passwort, neues Passwort (mindestens 10 Zeichen, höchstens 72 Bytes, nicht gleich dem Benutzernamen oder dem bisherigen Passwort) und Wiederholung. Bei ausstehendem Pflichtwechsel (erster Login, Admin-Reset) erscheint ein Vollbild-Dialog, der sich nicht schließen lässt (nur Passwort ändern oder Abmelden); solange bleibt jede andere Funktion gesperrt.

### Sprache

Die Oberfläche gibt es auf **Deutsch** und **Englisch**. Der Umschalter **DE / EN** sitzt im **Seitenleisten-Fuß** (unter der Theme-Wahl) und auf der **Anmeldeseite**; der Wechsel gilt sofort, ohne Neuladen. Die Wahl wird im Browser gespeichert (pro Browser und Gerät, nicht im Benutzerkonto); ohne Wahl folgt die Oberfläche der Browsersprache (Englisch, sonst Deutsch). Datum, Zahlen und Beträge folgen der Sprache. **Die Meldungen des Servers (Fehlertexte der API) und die Dokumentation bleiben deutsch**: in der englischen Oberfläche erscheinen häufige Fehlercodes übersetzt, andere Meldungen deutsch, und die farbigen Statusfelder zeigen teils den englischen Serverwert. Nutzung, eine neue Sprache hinzufügen und Grenzen: [features/i18n.md](features/i18n.md).

### Benutzer verwalten (nur Admin)

Seitenleiste **System → Benutzer**: Liste aller Benutzer. Pro Zeile:
- **Rollen-Checkboxen** (Admin, Manager, Receiver, Picker, Packer, Viewer), direkt umschaltbar; **Speichern** erscheint, sobald sich etwas ändert
- **PW reset** — der Admin setzt ein neues Passwort; der Benutzer muss es beim nächsten Login ändern. Das hebt auch eine Kontosperre auf.
- **Aktivieren / Deaktivieren** — Benutzer werden nur deaktiviert, nie gelöscht (das Konto und sein Name bleiben in alten Einträgen erhalten). Ein deaktiviertes Konto kann sich nicht mehr anmelden und verliert sofort die Sitzung.

**+ Neuer Benutzer**: Benutzername (mindestens 3 Zeichen) + Initial-Passwort (mindestens 10 Zeichen) + Rollen.

Schutzregeln: Es bleibt immer mindestens ein aktiver Admin; niemand kann sich selbst deaktivieren oder die eigene Admin-Rolle entziehen. Änderungen an Benutzern stehen im Audit-Protokoll (Rollen, Aktiv-Kennzeichen; Passwörter nur maskiert) und im Sicherheits-Log des Servers.

### Rollen

Ein Benutzer kann mehrere Rollen tragen. **Manager** darf alles, was Receiver, Picker und Packer dürfen; **Admin** darf zusätzlich die Admin-Bereiche. Die Rechte werden **serverseitig** geprüft (403 ohne Berechtigung), die Oberfläche blendet Menüpunkte nur passend aus.

| Rolle | Darf |
|---|---|
| **Viewer** | Alles **lesen** (Listen, Details, Reports). Nicht: Audit, Bestandsbewertung, Etiketten, Waage, Benutzerliste. Keine Änderungen. |
| **Receiver** | Wareneingang anlegen, Positionen erfassen und buchen; Wareneingang aus einer Einkaufsbestellung anlegen; Inventur starten und zählen; Retouren anlegen |
| **Picker** | Picklisten als gepickt abschließen; Nachschub-Aufgaben buchen; Etiketten drucken und ZPL laden |
| **Packer** | Picklisten verpacken (bucht den Bestand); Sendungen anlegen, Tracking zuweisen, versenden und als geliefert markieren; Waage |
| **Manager** | Stammdaten (Artikel, Lieferanten, Kunden, Pickwagen, Lager-Layout, Lagerstruktur); Bestellungen anlegen und stornieren; Picklisten, Wagen-Picklisten und Wellen erzeugen; Bestandskorrektur; Inventur abgleichen und abbrechen; Retouren prüfen und verarbeiten; Einkauf; Sendungen stornieren; CSV-Import und -Export; Audit-Protokoll; Bestandsbewertung |
| **Admin** | Alles, dazu Benutzerverwaltung, Backup und Restore und die Wartungswerkzeuge (Picklisten zurücksetzen, Reseed) |

Die vollständige Zuordnung je Endpunkt steht in [API.md](API.md#rollen).

---

## Stammdaten

### Artikel

Seitenleiste **Stammdaten → Artikel** → **+ Neuer Artikel** (Rolle Manager). Pflichtfelder: **SKU** (eindeutig, ohne Beachtung der Schreibweise, danach nicht mehr änderbar) und **Name**. Dazu:
- **Maße + Gewicht** (mm, g) — Grundlage für den Packvorschlag und die Einlagerungsvorschläge
- **Stapelbarkeit** — Stapelachse (X/Y/Z), Versatz je Stapel (`stackingIncrementMm`, oft kleiner als die volle Höhe wegen Verzahnung) und maximale Stapelzahl
- **Bestandsschwellen** — `MinStock` (Alarm "kritisch"), `ReorderPoint` (Bestellvorschlag, Alarm "Warnung"), `MaxStock` (Zielbestand für Bestellvorschläge); 0 = nicht gesetzt
- **Beschaffung** — Standard-Lieferant und Einkaufspreis (in Cent, mit Euro-Anzeige)
- **Kennungen** — **GTIN / EAN** (der Hinweis erscheint schon beim Tippen, leer = keine GTIN) und **Alternativ-SKUs**
- **Saison** — Gültig ab und Gültig bis (letzter Tag)
- **Bundle / Kit** — Komponenten mit Menge

Die Bedeutung steht unter [Konzepte](#bundles--kits) und [GTIN und Scan-Auflösung](#gtin-und-scan-auflösung). Wer mit ungespeicherten Änderungen den Editor verlässt, wird gewarnt. Die **Artikelliste** hat eine GTIN-Spalte, eine Suche über Name, SKU, Alternativ-SKU und GTIN, einen Statusfilter (Alle, Aktuell bestellbar, Außerhalb der Saison) und kennzeichnet Bundles und Saison-Artikel. Viele Artikel auf einmal lassen sich per [CSV-Import](#csv-import-und--export) anlegen oder aktualisieren.

### Lieferanten

**Stammdaten → Lieferanten**: Liste mit Formular (Code, Name, E-Mail, Telefon, Lieferzeit in Tagen, Mindestbestellwert, Währung). Lieferanten werden **deaktiviert statt gelöscht** (Bestellungen behalten ihren Bezug); für einen deaktivierten Lieferanten lässt sich keine neue Bestellung anlegen.

### Kunden

**Stammdaten → Kunden**: Kunde anlegen (Code, Name), aufklappen für Name, E-Mail, Telefon, Währung, Standard-Rabatt und Notizen sowie **Adressen** (Lieferadresse, Rechnungsadresse oder beides). Eine Bestellung kann mit einem Kunden und einer seiner Lieferadressen verknüpft werden (siehe [Bestellungen](#bestellungen)).

### Pickwagen

**Stammdaten → Pickwagen** (Rolle Manager): Konfiguration eines Kommissionierwagens: Name, Anzahl Ebenen, Ebenen-Maße (Breite, Tiefe, Höhe in mm) und maximales Gesamtgewicht. Aus Ebenenzahl und -maßen ergibt sich das Gesamtvolumen; die Wagen-Pickliste nutzt Volumen und Gewicht als Kapazität.

### Lager-Layout (CAD-Editor)

**Stammdaten → Lager-Layout**: Draufsicht.
- **Mausrad**: Zoom, **Ziehen auf leerer Fläche**: Verschieben
- **Regal ziehen**: verschiebt es (Raster 100 mm); **+ Neues Regal** legt ein Regal in einem bestehenden Gang an (optional gleich mit Fächern). Gibt es genau einen Gang, ist er vorgewählt; gibt es noch keinen Gang, aber ein Lager, legt der Dialog die Zone `Z1` und den Gang `A1` gleich mit an; gibt es kein Lager, verweist er auf die Lagerstruktur.
- **Regal anklicken**: Bins in diesem Regal, **+ Bin hinzufügen** (schlägt einen im ganzen System freien Code vor), Bins löschen (scheitert mit 409, solange Bestand oder Belege auf dem Lagerplatz liegen)
- **Wand zeichnen**: Modus "Wand zeichnen", Punkte anklicken, **Doppelklick beendet**; Wandpunkte lassen sich ziehen, Wände löschen; Endpunkte rasten auf vorhandene Wandpunkte ein
- **Pickpunkt platzieren**: Modus "Pickpunkt" mit Typ Start, Ende oder Beides; Pickpunkte verschieben, Typ ändern und löschen
- **Heatmap**: Umschalter, färbt Regale nach Pickfrequenz im gewählten Zeitraum

Ein Lager-Selector im Seitenkopf erscheint erst ab zwei Lagern und wirkt heute nur im Layout-Editor (siehe [Bekannte Einschränkungen](#bekannte-einschränkungen)).

### Lagerstruktur

**Stammdaten → Lagerstruktur** (Route `/warehouses`, Manager und Admin): die Struktur `Lager → Zone → Gang → Regal → Lagerplatz` vollständig anlegen, ändern und löschen, ohne Demo-Daten und ohne Swagger. Ohne Lager zeigt die Seite "Noch kein Lager – jetzt anlegen"; dorthin führen auch die Seitenleiste (Lager-Auswahl bei leerer Liste) und der Layout-Editor. Der Baum zeigt Lager, Zonen und Gänge aufgeklappt, Regale zugeklappt (ein Klick zeigt die Lagerplätze mit Bin-Typ, Schwelle, Maßen und Höchstlast).

| Aktion | Wo | Hinweis |
|---|---|---|
| Lager anlegen / umbenennen | **+ Neues Lager**, **Bearbeiten** am Lager | Code (eindeutig im System) und Name |
| Zone anlegen / ändern | **+ Zone**, **Bearbeiten** | Code und Name, Code eindeutig im Lager (Vorschlag `Z1`, `Z2` ...) |
| Gang anlegen / ändern | **+ Gang**, **Bearbeiten** | Code eindeutig in der Zone (Vorschlag `A1` ...), Ausrichtung entlang X oder Y |
| Regal anlegen / ändern | **+ Regal** am Gang, **Bearbeiten** am Regal | Code und Abmessungen, beim Anlegen mit Anzahl und Maßen der ersten Lagerplätze; die Position ändert der Layout-Editor |
| Lagerplatz anlegen / ändern | **+ Lagerplatz**, **Bearbeiten** | Code (Vorschlag `<Regal>-NN`), Maße, Höchstlast in kg |
| **Bin-Typ** und **Nachschub-Schwelle** | **Bin-Typ** am Lagerplatz | Standard, Hot-Pick oder Reserve; die Schwelle gilt nur für Hot-Pick (Bestand darunter = Nachschub-Aufgabe, siehe [Replenishment](#replenishment)) |
| Löschen | **Löschen** an jedem Knoten | die Bestätigung nennt, was mitgelöscht wird (z. B. "2 Zonen, 3 Gänge, 8 Regale, 40 Lagerplätze"); eine Ablehnung des Servers steht im Dialog |

Regeln: **Codes** sind ohne Beachtung der Schreibweise eindeutig (Lager im ganzen System, Zone im Lager, Gang in der Zone, Regal im Gang, **Lagerplatz im ganzen System**, weil er die Kennung für Bestand und Etiketten ist); ein Konflikt ist 409 `duplicate_code`. **Löschen** geht nur, wenn darunter kein Lagerplatz mit Bestand liegt und keine offene Aufgabe (Nachschub, laufende Inventur) auf einen Lagerplatz zeigt (409 `warehouse_not_empty`); dann wird die leere Unterstruktur samt Wänden und Pickpunkten des Lagers mitgelöscht. Ein Lagerplatz, auf den noch Belege verweisen (Wareneingang, Retoure, Pickliste, erledigte Nachschub-Aufgaben, abgeschlossene Inventuren), bleibt bestehen (409 `in_use`). Was es nicht gibt: Umlagerung von Bin zu Bin, eine Auslastungsansicht und das Verschieben eines Regals in einen anderen Gang. Alle Regeln, Grenzen und die zugehörigen API-Routen: [features/lager-stammdaten.md](features/lager-stammdaten.md).

---

## Wareneingang

Seitenleiste **Wareneingang → Wareneingang**: Liste aller Lieferungen mit Status **Draft** (Entwurf), **Received** (gebucht) oder **Cancelled**.

### Ablauf

1. **+ Neuer Wareneingang**: Lieferschein-Nummer (leer lassen = die Maske erzeugt `WE-JJJJMMTT-HHMMSS`) und optional die Lieferanten-Referenz, dann die **Positionen**: Artikel, **Ziel-Lagerplatz**, Menge, optional **Lot** (Charge) und **MHD**. Die Zeile blendet **Putaway-Vorschläge** ein (bis zu drei passende Lagerplätze mit Typ; ein Klick übernimmt den Lagerplatz). Kopf und Positionen werden **in einem Aufruf atomar** angelegt: scheitert eine Zeile, entsteht nichts (die Meldung nennt die Zeilennummer). Nach dem Anlegen steht die Lieferung als Entwurf in der Liste.
2. **📥 Empfangen** bucht die ganze Lieferung (nach einer Rückfrage): Der Bestand wird atomar in die Ziel-Lagerplätze gebucht (je Zeile eine Bestandszeile pro Charge, dazu ein Ledger-Eintrag `Inbound` mit Kosten-Snapshot). Status wird `Received`. Ein zweiter Klick (Doppelklick, Wiederholung) bucht nichts mehr.

Die Maske prüft die Zeilen vor dem Senden: kein MHD in der Vergangenheit (Neuware; heute ist erlaubt), Menge 1 bis 1.000.000, Charge höchstens 64 Zeichen, dieselbe Charge im selben Lagerplatz nur mit einem MHD. Bei einem Artikel, der schon **mit MHD geführt wird** (es gibt Bestand mit MHD), sind Charge und MHD **Pflicht**; eine halb ausgefüllte Zeile wird bemängelt statt still verworfen. In den Details eines Entwurfs zeigt eine Pill, ob ein MHD schon abgelaufen oder kritisch ist.

Regeln: mehrere Zeilen mit **gleicher Charge und gleichem MHD** im selben Lagerplatz ergeben **eine** Bestandszeile; dieselbe Charge mit **anderem MHD** wird abgelehnt. Eine Lieferung ohne Positionen lässt sich nicht buchen. Stornieren (nur Entwürfe, Rolle Manager) sowie nachträgliches Ergänzen oder Entfernen von Positionen eines Entwurfs gehen nur per API (`POST /api/inbound/{id}/lines`, `DELETE .../lines/{lineId}`, `POST .../cancel`).

### Wareneingang zu einer Einkaufsbestellung

Der reguläre Weg für bestellte Ware: auf der Seite **Beschaffung** bei einer versendeten oder teilweise gelieferten Bestellung **📥 Wareneingang anlegen** (Rolle Receiver). Der Dialog nennt die offenen Mengen und verlangt einen **Ziel-Lagerplatz** (alle Zeilen gehen dorthin). Er erzeugt aus den **offenen Mengen** einen Wareneingang im **Entwurf** (eine Zeile je Bestellzeile, Preis aus der Bestellung; die Nummer lautet `WE-<Bestellnummer>`, bei Teillieferungen mit Zähler) und **bucht noch nichts**: den Bestand bucht erst **📥 Empfangen** in der Wareneingangs-Liste. Erst dann wird die empfangene Menge in die Bestellzeilen fortgeschrieben (Status Teilweise geliefert bzw. Geliefert); mehr als die offene Menge wird abgelehnt (`po_over_receipt`). Pro Bestellung gibt es höchstens einen offenen Entwurf. Per API: `POST /api/purchase-orders/{id}/create-inbound` (Body `{ "targetBinId": "..." }`). Charge, MHD oder abweichende Lagerplätze der erzeugten Zeilen lassen sich vor dem Buchen nur per API ergänzen (siehe oben).

Wareneingänge lassen sich auch per API erfassen, z. B. aus einem Skript:

```bash
# 1. Kopf und Positionen in einem Aufruf anlegen (shipmentNumber ist Pflicht, lines optional)
curl -X POST http://localhost:5099/api/inbound -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"shipmentNumber":"LS-2026-001","supplierReference":"Lieferant X","lines":[{"articleId":"<artikel-id>","targetBinId":"<lagerplatz-id>","quantity":50,"lotNumber":"L-2026-09","expiryDate":"2027-06-30T00:00:00Z"}]}'
# 2. Buchen
curl -X POST http://localhost:5099/api/inbound/<id>/receive -H "Authorization: Bearer $TOKEN"
```

Ohne `lines` entsteht ein leerer Kopf, der über `POST /api/inbound/{id}/lines` gefüllt wird.

---

## Beschaffung

Seitenleiste **Wareneingang → Beschaffung**: Einkaufsbestellungen (PO) an Lieferanten.

- **Bestellvorschläge:** Karten oben zeigen Artikel, deren Bestand **plus bereits bestellte, noch nicht gelieferte Menge** unter dem `ReorderPoint` liegt, gruppiert nach Standard-Lieferant. Vorgeschlagen wird die Menge bis zum `MaxStock` (ohne MaxStock bis zum Doppelten des ReorderPoint). Damit erscheint derselbe Bedarf nach dem Versenden nicht erneut. **→ PO erstellen** übernimmt die Zeilen.
- **+ Neue PO:** Lieferant und Zeilen (Preis vorbelegt aus dem Artikel). Nummer `PO-JJJJMMTT-NNNNN`. Zeilen lassen sich nur im Entwurf ändern.
- **Versenden:** setzt die PO auf Versendet und sperrt sie für weitere Änderungen. (Es wird nichts an den Lieferanten übermittelt; das System führt nur den Status.)
- **📥 Wareneingang anlegen** bei versendeten und teilweise gelieferten Bestellungen: legt einen **Wareneingang im Entwurf** an (siehe [Wareneingang](#wareneingang)). **Die Bestellung bucht selbst keinen Bestand;** das tut erst der Wareneingang ("Empfangen"). Der Endpunkt `POST /api/purchase-orders/{id}/lines/{lineId}/receive` schreibt die empfangene Menge einer Zeile nur fort (Statuskorrektur ohne Bestandsbuchung, per API); wer beides tut, stößt an die Übermengen-Prüfung.

Stornieren (Rolle Manager, bis zum vollständigen Empfang) geht nur per API.

---

## Bestand

Seitenleiste **Bestand → Bestand**:
- **Bestands-Alarme:** Karte mit Artikeln unter `MinStock` (kritisch) bzw. `ReorderPoint` (Warnung).
- **Übersicht pro Artikel** mit der Spalte **Nächstes MHD** (frühestes MHD mit Menge, mit Status-Pill); der Pfeil vor dem Artikel klappt die **Unterzeilen je Lagerplatz, Charge und MHD** auf (FEFO-Reihenfolge).
- **Detail pro Platz** (mit Charge und MHD-Spalte). Chargennummern sind Links zur [Rückverfolgung](#chargen-mhd-und-rückverfolgung).
- **Buchung** (Rolle Manager): manuelle Korrektur um ein **Delta** (+/−) für Artikel und Lagerplatz; bucht ins Ledger (Grund `Adjust`). Ein Abgang über den Bestand hinaus wird abgelehnt. Nach Artikel und Lagerplatz erscheint **Charge / MHD** mit den vorhandenen Chargen dieses Platzes (Nummer, MHD, Menge); bei einem Zugang lässt sich zusätzlich **"Neue Charge …"** (Nummer plus optionales MHD) anlegen. Ohne Auswahl gilt "Ohne Charge" bzw. beim Abgang die Automatik: zuerst die chargenlose Zeile, dann FEFO über die Chargen.

Den Bestand vieler Zeilen auf einmal (z. B. nach einer Zählung in Excel) setzt der [CSV-Import](#csv-import-und--export) über das Ledger.

---

## Chargen, MHD und Rückverfolgung

Die lot-genaue Bestandsführung (eine Bestandszeile je Artikel, Lagerplatz und Charge, FEFO, ein MHD je Charge) ist in der Oberfläche bedienbar. Ausführlich: [features/chargen-mhd.md](features/chargen-mhd.md).

**Status des MHD** (alles in UTC-Kalendertagen; am Ablauftag selbst ist die Ware noch verwendbar):

| Status | Tage bis zum MHD | Farbe |
|---|---|---|
| **Abgelaufen** (`Expired`) | unter 0 | rot |
| **Kritisch** (`Critical`) | 0 bis 7 | orange |
| **Bald** (`Soon`) | 8 bis zur gewählten Frist | blau |
| OK (nur in der Bestandsansicht) | über 30 | grün |

Die Grenzen sind fest und nicht konfigurierbar.

- **Ablaufende Chargen** (Karte in den Reports, Seite `/reports`): Frist **7 / 30 / 90 Tage** (Standard 30); abgelaufene und bald ablaufende Bestandszeilen mit Status, Artikel, Lagerplatz, Charge (Link), MHD, Ablauf in Tagen und Menge, nach MHD sortiert (höchstens 50 Zeilen, der Rest steht in der Zählzeile). API: `GET /api/reports/expiring?days=30`.
- **Chargen-Trace** (Seitenleiste **Auswertung → Chargen-Trace**, Route `/traceability`, jeder Angemeldete): Suche nach einer Chargennummer (auch per Adresse: `/traceability?lot=LOT-A`). Die Seite zeigt Kennzahlen, die **Wareneingänge** der Charge, den **aktuellen Bestand** je Lagerplatz, alle **Bewegungen** aus dem Ledger und die **möglicherweise betroffenen Bestellungen** (mit Link zur Bestellung und zur Pickliste). Eine unbekannte Charge ist "nicht gefunden", kein Fehler. API: `GET /api/reports/charge/{lotNumber}`.

Grenzen: **Bestellungen im Trace sind eine Näherung** (eine Pickposition kennt die Charge nicht; liegen im Lagerplatz mehrere Chargen, kann eine Bestellung auch aus einer anderen beliefert worden sein, daher "möglicherweise betroffen"). Chargennummern mit `/` lassen sich über die Trace-Route nicht abfragen. Das MHD einer vorhandenen Charge lässt sich nicht ändern (Tippfehler: Korrektur mit Gegenbuchung). Die Warnliste **warnt nur**; abgelaufene Chargen werden beim Erzeugen von Picklisten ohnehin übersprungen. "MHD-pflichtig" ist kein Artikelfeld: die Wareneingangs-Maske behandelt einen Artikel als MHD-pflichtig, sobald Bestand mit MHD existiert, der Server erzwingt es nicht.

---

## Bestellungen

Seitenleiste **Auslieferung → Bestellungen**. Oben eine **Übersicht** (Gesamt, Status New, mit Stock, ohne Stock, "Fällt aus durch FIFO", "Nicht pickbar gesamt"), darunter die Liste. Die Liste sortiert **neue Bestellungen mit Bestand zuerst** (Priorität absteigend, Fälligkeit aufsteigend, Eingang), dann neue ohne Bestand, dann die übrigen Status. Ein Status-Filter grenzt ein.

**Status-Fluss:** `New` → `Picking` (Pickliste erzeugt) → `Picked` (Picken abgeschlossen) → `Packed` (verpackt, Bestand gebucht) → `Shipped` (versendet). `Cancelled` (storniert). Die Spalte **Stock** zeigt für neue Bestellungen: OK, "kein Stock" oder **"FIFO-Konflikt"** (die Bestellung hätte allein genug Bestand, aber dringendere oder ältere Bestellungen verbrauchen ihn vorher).

### Manuell anlegen

Karte **Manuelle Bestellung anlegen** (aufklappen, Rolle Manager):
- Auftragsnummer (Pflicht, eindeutig ohne Beachtung der Schreibweise) und optionale Kundenreferenz (Freitext)
- optional **Kunde** und dessen **Lieferadresse** (aus dem Kundenstamm; die Adresse muss eine Liefer- oder Doppeladresse des Kunden sein)
- **Priorität** (Normal, Erhöht, Hoch, Dringend) und **Fällig am** (Kalendertag)
- Positionen (Artikel + Menge)

Bei Bundle-Artikeln wird die Bestellung beim Pickliste-Generieren in Komponenten aufgelöst. Artikel außerhalb ihres Saison-Fensters werden abgelehnt. Viele Bestellungen auf einmal legt der [CSV-Import](#csv-import-und--export) an.

### Stornieren

**Stornieren** (Liste oder Detailseite, Rolle Manager) ist erlaubt aus **New, Picking und Picked** und hat **keinen Bestandseffekt**: gebucht wird erst beim Verpacken. Die Positionen der Bestellung verlassen ihre Picklisten; bleibt eine Liste leer, wird sie storniert. Ab **Packed** ist Stornieren nicht mehr möglich (409 `order_not_cancellable`); dann bleibt die Retoure.

### Externe Bestell-API

`POST /api/orders` (Rolle Manager) ist die Schnittstelle für Shops und Drittsysteme; die Bestellung bekommt die Quelle `Api`. Für Maschinenzugriffe einen eigenen Benutzer mit der Rolle Manager anlegen. Sie ist **wiederholbar**: `externalReference` im Body oder der Header `Idempotency-Key` (höchstens 128 Zeichen) identifiziert die Bestellung im Quellsystem. Dieselbe Kennung liefert die bereits angelegte Bestellung zurück (200 mit Header `Idempotent-Replayed: true`, das erste Mal 201). Eine doppelte Bestellnummer **ohne** passende Kennung ist ein Konflikt (409 `duplicate_order_number`). Positionen nennen den Artikel per `articleId` oder `sku`. Einzelheiten: [API.md](API.md#bestellungen-und-kunden).

Aus den Schnittstellen zu ERP/Shop wird **nichts automatisch abgeholt** (siehe [ARCHITECTURE.md](ARCHITECTURE.md#erweiterungspunkte--was-heute-wirklich-funktioniert)).

---

## Picken

### Standard-Pickliste

**Bestellung öffnen → Pickliste generieren** (Bestellung im Status New; Start- und Endpunkt wählbar, sonst der erste Start-Pickpunkt).

Der Server:
1. wählt den Bestand: nur nicht abgelaufene Chargen, **FEFO** (frühestes MHD zuerst), HotPick vor Standard vor Reserve; bei zu wenig Bestand bricht das Erzeugen mit einer Fehlmenge ab (alles oder nichts, nichts wird überzuteilt);
2. berechnet die Route um die Wände herum (Reihenfolge bei bis zu 12 Stopps exakt, darüber eine Heuristik);
3. setzt die Bestellungen auf **Picking** und vergibt eine Nummer `PL-JJJJMMTT-NNNNN`.

Es wird **nichts reserviert**: der Bestand bleibt unverändert, bis die Liste verpackt wird. Die Pickliste-Seite zeigt Reihenfolge, Lagerplätze, Mengen und die Route auf dem Lager-Canvas. **Route neu berechnen** (Karte, nur für offene Listen) rechnet mit dem aktuellen Layout und anderen Start-/Endpunkten neu.

### Pickwagen-Pickliste

Karte **Wagen-Pickliste generieren** auf der Bestellungen-Seite (Rolle Manager; vorher unter **Pickwagen** eine Konfiguration anlegen). **Wagen vollmachen** bündelt offene Bestellungen in **eine** Pickliste, bis der Wagen voll ist:
- Reihenfolge: Priorität, Fälligkeit, Eingang.
- Die Kapazität ist die **Summe** aus Volumen und Gewicht der Artikel gegen Gesamtvolumen und Höchstgewicht des Wagens. **Es gibt keine 3D-Packoptimierung und keine Verteilung auf Wagenebenen**; die Ebenenzahl fließt nur ins Gesamtvolumen ein.
- Optional **"So wenig Lagerplätze wie möglich anfahren"**: wählt bevorzugt Bestellungen, deren Artikel Lagerplätze der schon gewählten teilen (Greedy).
- Bestellungen ohne ausreichenden Bestand (oder mit nicht auflösbarem Bundle) werden übersprungen, nicht als Fehler behandelt. Passt gar nichts, meldet der Server, wie viele wegen Kapazität und wegen Bestand übersprungen wurden.

### Wellen (Wave-Picking)

Seitenleiste **Auslieferung → Wellen**. Mehrere offene Bestellungen zu einer Welle bündeln (z. B. "Cutoff 12:00").

**Neue Welle:** Beschreibung, optionale Cutoff-Zeit, Bestellungen (Mehrfachauswahl aus offenen Bestellungen). Nummer `W-JJJJMMTT-NNNN`. **▶ Release** erzeugt **eine** konsolidierte Pickliste mit allen Positionen (die Bestellungen gehen auf Picking); **Abbrechen** verwirft eine offene Welle.

**Status:** `Open` → `Released` → `Completed` (sobald **alle** Picklisten der Welle verpackt sind). `Cancelled` aus Open; eine freigegebene Welle lässt sich nur per API abbrechen (`POST /api/pick-waves/{id}/cancel`), und nur, solange ihre Pickliste noch unbegonnen ist (nicht als gepickt markiert); dabei wird die Liste storniert und die Bestellungen kehren auf New zurück. Eine Aufteilung nach Zonen gibt es nicht.

### Mobile-Picker

Auf der Pickliste-Seite **📱 Mobile-Picker öffnen** → Vollbild-Route `/picker/:id` ohne Seitenleiste.

- Schritt für Schritt Lagerplatz für Lagerplatz, dunkles Theme, große Touch-Flächen
- **📷 Bin scannen** — Kamera erkennt den Lagerplatz-Code; ein Treffer bestätigt den Platz (manuelle Bestätigung als Ausweg). Die Lagerplatz-Etiketten dafür druckt die Seite [Etiketten](#etiketten).
- **📷 Artikel scannen (+1)** — der Scan einer SKU erhöht die Menge des passenden Postens um 1. Gezählt wird **ab 0**: wer scannt, zählt nach und übernimmt nicht blind das Soll. Ein Scan lässt sich per **Rückgängig** zurücknehmen. (Eine GTIN erkennt der Scan noch nicht.)
- Pro Posten Plus/Minus und Zahleneingabe; die Menge bleibt zwischen 0 und Soll. Eine unberührte Position gilt als wie geplant gepickt.
- Letzter Stopp → **✓ Picking abschließen**: markiert die Pickliste als **Picked** (Bestellungen ebenso) und springt zur Pack-Seite. Der Server kennt beim Picken **keine Mengen**; die am Regal gezählten Mengen werden **an die Pack-Seite übergeben** und dort als Ist-Menge vorbelegt (Abweichungen sind sichtbar). **Gebucht wird erst, wenn der Packer dort bestätigt.**

Kamera-Scan braucht einen **sicheren Kontext (HTTPS oder `localhost`)** und die `BarcodeDetector`-API des Browsers (Chrome/Edge, v. a. Android); sonst bleibt das Textfeld zur manuellen Eingabe. Über `http://<IP>:5173` aus dem LAN sperrt der Browser die Kamera; mit dem Docker-Profil `https` (Caddy) steht ein HTTPS-Zugang bereit (siehe [GETTING_STARTED.md](GETTING_STARTED.md#https-und-kamera-scan-am-handy-caddy)).

---

## Packen & Versand

### Packen

Seitenleiste **Auslieferung → Packen**: Übersicht mit **Bereit zum Packen** (Picklisten im Status Picked), **Noch im Picken** und **Verpackt**. Ein Klick auf **📦 Packen** öffnet die Pack-Seite.

Jede **Bestellung wird ein Paket** (bei Wagen- und Wellen-Picklisten mehrere Pakete in einer Liste). Pro Position:
- Die Soll-Menge (bzw. die im Mobile-Picker gezählte Menge) ist als **Ist-Menge** vorbelegt; sie lässt sich von **0 bis Soll** ändern, die Differenz wird rot/grün markiert. **Alle auf Plan-Menge** setzt zurück.
- **Packen abschließen** bestätigt die Mengen und **bucht den Bestand ab**: je Lagerplatz FEFO über die Chargen, je Bestandszeile ein Ledger-Eintrag `Pick` mit Charge und MHD. Die Pickliste wird `Completed`, die Bestellungen `Packed`, und sind damit alle Listen einer Welle verpackt, ist auch die Welle abgeschlossen.
- **Kurz-Pick:** Eine Ist-Menge unter Soll bucht nur die Ist-Menge; die Fehlmenge wird **nicht** nachgeliefert oder neu eingeplant, die Bestellung gilt trotzdem als gepackt. Vor dem Absenden fragt die Seite bei Abweichungen nach.
- **Genau einmal:** Eine bereits verpackte oder stornierte Liste lässt sich nicht erneut packen (Fehler, es wird nichts gebucht). Reicht der Bestand am Lagerplatz nicht (mehr), scheitert der ganze Vorgang, ohne etwas zu ändern.

**📄 Lieferschein als PDF** lädt pro Bestellung eine Seite mit Positionen und Bestellnummer (QuestPDF). Die Bestellnummer erscheint als **Code-128-Barcode** (Vektor-Rechtecke) mit der Nummer im Klartext darunter; lässt sich die Nummer nicht kodieren (Zeichen außerhalb von ASCII 32 bis 126), bleibt der Text-Kasten.

**Packvorschlag:** Auf der Bestellungs-Detailseite berechnet der Server, in welche Kartons (S, M, L, XL) die Positionen passen, mit Füllgrad und Gewicht. Das ist eine **Heuristik mit geometrischer Prüfung**, kein Optimum und keine Vorgabe; die Kartongrößen sind fest.

### Versand

Seitenleiste **Auslieferung → Versand**: Sendungen mit Status **Ready → Labeled → Shipped → Delivered** (oder Cancelled).

**Neues Paket** (Rolle Packer): Es lassen sich nur **gepackte Bestellungen** (Status Packed) wählen. Dazu Carrier, **Länge, Breite, Höhe und Gewicht (alle Pflicht)**; aus dem Packvorschlag lassen sich die Werte eines Kartons übernehmen. Der Empfänger ist die Lieferadresse der Bestellung (Kunde plus verknüpfte Adresse), sofern eine verknüpft ist.

**Carrier:** Verfügbar ist **Manuell** (Tracking-Nr per Hand). **DHL und UPS sind nicht angebunden**: sie erscheinen als "nicht verfügbar" und lassen sich nicht wählen; es entstehen keine Fake-Tracking-Nummern. Ein später angebundener Carrier erzeugt Nummer und Label beim Zuweisen des Trackings selbst.

Ablauf:
1. **Tracking-Nr** zuweisen (bei manuellem Carrier Pflicht, optional URL und Kosten) → `Labeled`.
2. **📦 Versandt**, wenn das Paket abgeholt wurde → `Shipped`. Hat die Bestellung damit keine offene Sendung mehr (Ready oder Labeled), wird sie **Shipped**.
3. **✓ Geliefert** → `Delivered` (manuelle Bestätigung).

Offene Sendungen (Ready/Labeled) lassen sich stornieren (Rolle Manager, nur per API); die Bestellung bleibt dabei `Packed` und bekommt bei Bedarf eine neue Sendung.

---

## Retouren

Seitenleiste **Auslieferung → Retouren**: Liste der Rücksendungen (RMA, Nummer `RMA-JJJJMMTT-NNNNN`).

### Neue Retoure

Kundenreferenz, Notizen und Zeilen (Artikel, Menge, optional Charge). Die Retoure **zu einer Bestellung** (Feld `orderId`, nur per API) prüft zusätzlich: die Bestellung muss ausgeliefert sein (**Packed** oder **Shipped**), nur Artikel der Bestellung (Bundles als Komponenten), höchstens die gelieferte Menge abzüglich früherer Retouren. Bundle-Artikel selbst werden abgelehnt.

### Qualitätsprüfung

Pro Zeile die Buttons (Rolle Manager):
- **Sellable** — verkaufsfähig; ein **Ziel-Lagerplatz** ist Pflicht, die Ware kommt beim Verarbeiten zurück in den Bestand (Ledger `Return`)
- **BGrade** — B-Ware: kein Verkaufsbestand (Ledger `ReturnB`, netto 0)
- **Defect** — defekt: kein Verkaufsbestand (Ledger `ReturnScrap`, netto 0)
- **Destroy** — vernichten (Ledger `ReturnScrap`)

Sobald **alle** Zeilen bewertet sind, erscheint **✓ Verarbeiten**. Es bucht je nach Ergebnis (Charge und MHD bleiben erhalten); ein zweiter Klick bucht nichts mehr. Stornieren geht nur per API.

---

## Inventur

Seitenleiste **Bestand → Inventur**. Liste aller Zählungen mit Status **Open**, **Reconciled** oder **Cancelled**.

### Neue Inventur starten

Name (Pflicht) und optional **ein einzelner Lagerplatz** (Teilzählung) → **Starten**. Der Server nimmt einen **Snapshot** des aktuellen Bestands: eine Zeile je vorhandener Bestandszeile (Lagerplatz, Artikel, Charge/MHD) mit dem Soll-Wert. Ohne Bestand lässt sich keine Inventur starten; **Bestand in Lagerplätzen ohne Bestandszeile** taucht nicht auf und ist per Bestandskorrektur zu buchen.

### Erfassen

Klick auf die Inventur öffnet die Detailseite mit der Zählliste:

| Spalte | Inhalt |
|---|---|
| Bin / SKU | Position |
| Soll | Snapshot-Wert |
| Ist | Eingabefeld, speichert beim Verlassen des Feldes (0 = leer gezählt) |
| Diff | Live grün/rot (+/−) |
| Grund | Freitext für Abweichungen |

### Abgleichen

**✓ Reconcile (Diffs anwenden)** ist erst möglich, wenn **jede** Position gezählt ist (der Server verlangt es), und fragt nach. Der Abgleich bucht den **gezählten Wert gegen den aktuellen Bestand** der Zeile (nicht die Differenz zum Snapshot): Bewegungen zwischen Start und Abgleich (Picken, Wareneingang) werden nicht doppelt verrechnet. Weicht der aktuelle Bestand vom Snapshot ab, steht das im Grund der Zeile. Zeilen ohne Differenz erzeugen keine Buchung. Gebucht wird mit Ledger-Grund `Inventory`, der Status wird `Reconciled` (endgültig). Abbrechen einer offenen Inventur geht nur per API.

---

## Replenishment

Seitenleiste **Wareneingang → Replenishment**: Nachschub-Aufgaben, die HotPick-Plätze aus Reserve-Plätzen auffüllen. Voraussetzung sind Lagerplätze mit Typ **HotPick** (samt Schwelle) und **Reserve**, die man auf der Seite [Lagerstruktur](#lagerstruktur) einstellt. Die Demo-Daten bringen solche Plätze mit; ohne sie entstehen keine Aufgaben.

### Aufgaben scannen

**🔍 Hot-Picks scannen** prüft alle HotPick-Plätze mit Schwelle: Liegt der Bestand eines Artikels dort **unter der Schwelle** (auch bei komplett leergepicktem Platz, dessen Bestandszeile mit Menge 0 stehen bleibt), legt der Scan eine Aufgabe an. Die Quelle ist der Reserve-Platz mit der FEFO-ersten Ware (abgelaufene zählt nicht). Vorschlagsmenge: bis zum Doppelten der Schwelle, begrenzt durch den Bestand der Quelle. **Idempotent:** Gibt es für Artikel und Ziel-Platz schon eine offene Aufgabe, entsteht keine zweite. Die Symbolleiste zeigt "✓ N neue Tasks erzeugt". Der Scan ist ein **manueller Knopf**; automatisch läuft nichts.

### Buchen

Pro Aufgabe **📦 Buchen** → Dialog mit der Ist-Menge. Bestätigen:
- der Bestand des Quell-Platzes wird abgebucht (FEFO über die Chargen) und mit **denselben Chargen und MHD** im Ziel-Platz zugebucht (Ledger `ReplenishmentOut` und `ReplenishmentIn`);
- die Aufgabe wird `Completed`. Reicht die Quelle nicht, scheitert der Vorgang, ohne etwas zu ändern.

Filter "Nur offene" blendet erledigte Aufgaben aus. Abbrechen (Rolle Manager) geht nur per API.

---

## Reporting & Analytics

Seitenleiste **Auswertung → Reports**. Die Karten:

### Lagerwert und Live-Status (oben)
- **Aktueller Lagerwert** (nur für Manager und Admin sichtbar): **FIFO** über die Zugänge im Ledger, bewertet mit dem Kosten-Snapshot jeder Buchung (Stammpreis zum Buchungszeitpunkt, nicht der tatsächliche Rechnungspreis, solange der Wareneingang keinen Preis erfasst). Umlagerungen sind bewertungsneutral; Bestand **ohne Ledger-Historie** (etwa der kleine Altbestand-Datensatz) wird zum aktuellen Stammpreis bewertet. Währung immer EUR.
- **Live-Status** (Abfrage alle 10 Sekunden): Orders offen (New), Im Picking (Picking), Ready Pack (Picklisten im Status Picked), Heute fertig (heute verpackte Picklisten), Inventur offen, Replen offen, Bestand kritisch / Warnung.

### Kennzahlen (Zeitraum 24 h / 7 / 30 / 90 Tage; alle Zeiten UTC)
- Bestellungen und Picklisten im Zeitraum (nach Anlagedatum), davon fertige Picklisten
- **Picks pro Stunde** = gepickte Positionen geteilt durch die Zeitspanne zwischen erstem und letztem Pick im Zeitraum (mindestens 1 Stunde)
- **Ø Pickdistanz** über abgeschlossene Picklisten (in m)

Datenbasis der Auswertungen sind **tatsächlich gepickte Positionen**: Picklisten im Status Picked oder Completed, mit der bestätigten Menge (bzw. der Sollmenge, solange noch nicht verpackt); offene und stornierte Listen und Positionen mit Ist-Menge 0 zählen nicht.

### Weitere Karten
- **Ablaufende Chargen:** siehe [Chargen, MHD und Rückverfolgung](#chargen-mhd-und-rückverfolgung)
- **Picklisten pro Tag** / **Bestellungen pro Tag** (einfache SVG-Verläufe), **Bestellungen nach Status**
- **Top 10 Artikel** (Pickfrequenz) und **Top 10 Lagerplätze**
- **ABC-Analyse:** Pareto über die Ist-Pickmenge: **A** = die Artikel, die zusammen die ersten 80 % der Menge tragen, **B** bis 95 %, **C** der Rest (ein Artikel, der die Grenze überschreitet, zählt noch zur oberen Klasse; ein Einzelartikel ist A)
- **Pick-Heatmap:** gepickte Positionen je Lagerplatz, farbig
- **Picker-Performance:** je Picker Picklisten, Positionen, Ø Weg und Ø Dauer (der Picker wird beim Abschluss des Pickens festgehalten)
- **Slotting-Vorschläge:** Tausch- oder Verschiebe-Empfehlungen mit **Netto-Ersparnis** über beide Artikel. Grundlage ist die Pickfrequenz mal die **Luftlinie** zum ersten Start-Pickpunkt; Wände zählen dabei nicht. Es werden nur Empfehlungen berechnet, nie Bestand umgelagert.
- **Dead-Stock:** Artikel mit Bestand ohne Bewegung seit mindestens 90 Tagen. Interne Umlagerungen (Nachschub) zählen nicht als Bewegung; Artikel ohne jeden Ledger-Eintrag gelten als "nie bewegt" (∞).

### Nur per API
- **Bestandsverlauf:** `GET /api/reports/stock-trend/{articleId}?days=90`: echte Mengen-Deltas aus dem Ledger.

---

## CSV-Import und -Export

Seitenleiste **Stammdaten → Import & Export** (Route `/import-export`, **Manager** und Admin). Sie liest und schreibt CSV-Dateien, die Excel, Buchhaltung oder andere Systeme lesen. Alle Formate, Fehlercodes und Grenzen: [features/csv-import-export.md](features/csv-import-export.md).

**Export:** oben das **Trennzeichen** wählen (Semikolon = Excel-Standard mit Dezimalkomma, Komma = Dezimalpunkt), dann bei der gewünschten Datei **Herunterladen**: **Artikel**, **Bestand** (je Artikel, Lagerplatz und Charge), **Bestellungen** (eine Zeile je Position), **Bewegungen** (das Ledger) und **Audit**. Bewegungen und Audit lassen sich auf einen **Zeitraum** (von/bis, "bis" schließt den ganzen Tag ein) eingrenzen, das Audit zusätzlich auf einen **Benutzer**. Der Dateiname trägt die UTC-Zeit (`articles-20260930T101500Z.csv`). Die Dateien sind UTF-8 mit BOM (Excel erkennt die Umlaute). Ein Text, den Excel als Formel auswerten würde (beginnt mit `=`, `+`, `-`, `@`), bekommt im Export ein Apostroph vorangestellt; der Import nimmt es wieder weg.

**Import** von Artikeln, Bestand und Bestellungen, immer mit **Trockenlauf**:

1. Bei Artikel, Bestand oder Bestellungen **Datei importieren…**, die CSV-Datei wählen und das Trennzeichen prüfen.
2. **Prüfen (Trockenlauf)** prüft jede Zeile gegen die Regeln des Systems und den vorhandenen Datenstand, **ohne etwas zu schreiben**. Das Ergebnis: "x neu, y aktualisiert, z Fehler", Hinweise (z. B. eine unbekannte Spalte) und eine **Tabelle der Zeilenfehler** (Zeile der Datei, Schlüssel, Meldung).
3. **Übernehmen** schreibt. Gibt es Zeilenfehler, ist der Knopf gesperrt, bis die Datei korrigiert ist oder das Häkchen **"Fehlerhafte Zeilen auslassen"** gesetzt wird. Wer Datei oder Trennzeichen nach der Prüfung ändert, muss neu prüfen. Die Übernahme läuft **in einer Transaktion**: scheitert etwas, bleibt nichts zurück.

| Datei | Wirkung beim Import |
|---|---|
| **Artikel** | Upsert je SKU, **idempotent**: derselbe Import zweimal ändert nichts. Fehlt eine Spalte, bleibt das Feld unverändert; eine vorhandene, aber leere Zelle setzt es zurück. Bundle-Komponenten stehen nicht in der Datei. |
| **Bestand** | setzt den Bestand je Artikel, Lagerplatz und Charge auf den **Wert der Datei** (Sollbestand); gebucht wird die Differenz **über das Ledger** (Grund `Adjust`, Vorgangstyp `CsvImport`). Die Lagerplätze müssen vorher existieren. |
| **Bestellungen** | legt **neue** Bestellungen an (Zeilen mit derselben `OrderNumber` sind eine Bestellung); eine vorhandene Nummer ist ein Fehler (außer mit derselben `ExternalReference`: dann "unverändert"), bestehende Bestellungen werden nie geändert. |

Sinnvolle Reihenfolge beim Start: **Artikel, dann Bestand, dann Bestellungen**. Zum Ausprobieren liegen Beispieldateien unter `docs/samples/` (`articles.csv`, `stock.csv`, `orders.csv`; die Bestandsdatei nennt die Lagerplätze `A-01-1` bis `B-02-3`: entweder solche Lagerplätze in der Lagerstruktur anlegen oder die Spalte `Location` durch vorhandene Codes ersetzen).

Grenzen: **5 MB** und **20.000 Datenzeilen** je Datei, höchstens 500 Einzelfehler in der Antwort; der Import liest CSV, keine `.xlsx` (in Excel "Speichern unter" → "CSV"); Lieferanten, Kunden, Lagerstruktur und Bundle-Komponenten lassen sich nicht importieren. Im Audit-Trail erscheint ein Import als **ein** Sammel-Eintrag (`CsvImport`), nicht als eine Zeile je Datensatz; ein Trockenlauf schreibt keinen. API: [API.md](API.md#csv-import-und--export).

---

## Etiketten

Seitenleiste **Stammdaten → Etiketten** (Route `/labels`, Rolle **Picker**, Manager und Admin). Sie druckt Etiketten für **Lagerplätze**, **Artikel** und **Bestellungen** mit Code 128 im Browser und lädt dieselbe Auswahl als ZPL für Zebra-Drucker. Ausführlich: [features/etiketten.md](features/etiketten.md).

1. **Etikettenart** wählen und die **Auswahl** treffen, einzeln per Ankreuzfeld oder mit "Alle Treffer auswählen". Lagerplätze haben Filter für **Lager, Gang, Regal** und eine Suche (Sammeldruck eines Regals oder Gangs: alle Etiketten in einem Druckjob, sortiert nach Code); Artikel: Suche über SKU, Name und GTIN, der Barcode kodiert die **SKU**; Bestellungen: Suche über Nummer und Kunde, der Barcode kodiert die **Bestellnummer**.
2. **Format:** "Einzeletikett 50 × 30 mm" (jedes Etikett eine Seite, für Rollendrucker) oder "A4-Bogen 3 × 8" (24 Etiketten à 70 × 36 mm). Dazu **Kopien je Etikett** (1 bis 100) und bei einem A4-Bogen die **Startposition** (1 bis 24), um einen angebrochenen Bogen weiterzuverwenden.
3. Die **Vorschau** ist die Druckvorlage. **Drucken** öffnet den Druckdialog des Browsers; dort **Skalierung 100 % ("Tatsächliche Größe")** und **Ränder "Keine"** einstellen, sonst passt das Raster nicht auf den Bogen.
4. **ZPL herunterladen** speichert **eine** Datei mit allen Etiketten (jedes ein eigenes `^XA…^XZ`, Kopien per `^PQ`), die man an einen Zebra-Drucker schickt oder in Zebra Designer öffnet. Die Seite ruft je Etikett `GET /api/labels/{bin|article|order}/{id}.zpl?copies=n` auf.

Ein Code, den Code 128 nicht darstellen kann (z. B. mit Umlaut), erscheint nur als Text und wird gemeldet. Ab 200 Etiketten (Druck) bzw. 100 Etiketten (ZPL) fragt die Seite nach; mehr als 1000 Etiketten samt Kopien druckt der Browser nicht. Es gibt **keinen** EAN-13/GTIN-Barcode (der Artikel-Barcode ist die SKU) und keinen Netzwerkdruck direkt an den Zebra-Drucker. Ob ein gedruckter Bogen mit einem Handy lesbar ist, ist noch nicht praktisch geprüft (Vorgehen in der Referenz).

---

## System & Admin

### Audit-Protokoll

Seitenleiste **Auswertung → Audit** (Rolle Manager), filterbar nach Entität und ID. Protokolliert werden Änderungen an **Artikeln, Bestandszeilen, Bestellungen, Picklisten, Wänden, Lagerplätzen, Lagern, Pickpunkten, Regalen, Pickwagen-Konfigurationen und Benutzern** (mit Diff und Benutzer; Passwörter nur maskiert) sowie ein Sammel-Eintrag je **CSV-Import**. **Nicht** protokolliert werden Kunden, Lieferanten, Einkaufsbestellungen, Wareneingänge, Inventuren, Retouren, Sendungen, Wellen und Nachschub-Aufgaben. Bestandsänderungen sind zusätzlich über das Ledger nachvollziehbar (ohne Benutzer). Es ist also **kein lückenloser Audit-Trail**. Den Audit-Trail lädt der [CSV-Export](#csv-import-und--export) als Datei.

### Backup und Restore

Seitenleiste **System → Backup & Restore** (Route `/system`, nur **Admin**, nur **SQLite**; bei MySQL zeigt die Seite stattdessen den `mysqldump`-Aufruf). Ausführlich: [features/backup-restore.md](features/backup-restore.md).

| Bereich | Was man dort tut |
|---|---|
| Einstellungen (nur lesen) | Zeitplan (`täglich 02:00 UTC` oder `aus`), nächster Lauf (Ortszeit), Aufbewahrung, ob der Restore freigegeben ist. Die Werte kommen aus der Server-Konfiguration (`Backup__Schedule`, `Backup__RetentionCount`, `Backup__AllowRestore`, [CONFIGURATION.md](CONFIGURATION.md#backup-und-restore)), die Seite ändert sie nicht. |
| **Backup jetzt erstellen** | legt sofort ein Backup an (konsistenter Snapshot); danach greift die Aufbewahrung |
| Liste | Name, Art (**Backup** oder **Vor Restore**), Größe, Zeit; je Zeile **Herunterladen**, **Wiederherstellen**, **Löschen** |
| **Backup-Datei einspielen…** | Restore aus einer hochgeladenen Datei (z. B. ein Backup von einem anderen Rechner; bis 1 GB) |

**Restore:** in der Zeile **Wiederherstellen** (oder **Backup-Datei einspielen…**) wählen, der Dialog warnt, dass die laufende Datenbank ersetzt wird und alle Änderungen seit der Sicherung verloren gehen. Zur Bestätigung **`RESTORE`** eintippen. Der Server prüft die Datei, sichert den aktuellen Stand als `lager-before-restore-<UTC-Zeit>.db` und tauscht die Datenbank aus; danach zeigt die Seite **Neustart erforderlich**: den Server neu starten (Docker: `docker compose restart`) und neu anmelden. **In Produktion ist der Restore gesperrt**, solange `Backup__AllowRestore` nicht `true` ist (nur für die Dauer des Restores setzen); in `Development` ist er immer erlaubt. Wer sich vertan hat, spielt die Sicherheitskopie ein.

Die Backups enthalten **alle Daten samt Passwort-Hashes** und sind **unverschlüsselt**; ein Backup auf demselben Laufwerk schützt nicht vor einem Plattenausfall: das Backup-Verzeichnis gehört zusätzlich auf ein anderes Medium. Der Zeitplan holt einen verpassten Lauf nicht nach. Anleitung für den Betrieb: [GETTING_STARTED.md](GETTING_STARTED.md#backup-und-restore).

### Demo-Modus

Der Demo-Modus (`Demo__Enabled=true` bzw. `LAGER_DEMO=1`, standardmäßig aus) füllt eine **leere** Datenbank beim Start mit einem erfundenen Betrieb samt Historie und Demo-Benutzern (`manager`, `picker`, `packer`, `receiver`, `viewer` ...); die Passwörter stehen einmalig in der Konsole bzw. im Container-Log. Die Oberfläche zeigt keinen Hinweis, dass die Anwendung im Demo-Modus läuft. Einschalten, Docker und Datensatz: [GETTING_STARTED.md](GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden) und [features/demo-modus.md](features/demo-modus.md).

### API-Beschreibung (Swagger)

Die API beschreibt sich selbst: **Swagger UI** unter `/swagger` zum Ausprobieren der Endpunkte im Browser und die OpenAPI-Beschreibung unter `/swagger/v1/swagger.json` für Werkzeuge. Swagger läuft in der Entwicklung (Umgebung `Development`) oder mit `Swagger:Enabled=true`, im Produktionsbetrieb ist es standardmäßig aus; die Beschreibung ist ohne Anmeldung lesbar, die Endpunkte bleiben geschützt. Einschalten, Anmelden in Swagger und Inhalt: [features/openapi.md](features/openapi.md); Überblick über alle Endpunkte: [API.md](API.md).

### Picklisten zurücksetzen und Reseed

- **Picklisten zurücksetzen** (Admin, Seite **Picklisten**): löscht alle **nicht verpackten** Picklisten (Pending, Picked); die Bestellungen kehren auf New zurück. **Verpackte** (Completed) und stornierte Listen bleiben; die Antwort nennt beide Zahlen (`deleted`, `skippedCompleted`).
- **Reseed** (`POST /api/admin/reseed`, nur `Development`): löscht **alle Fachdaten** (Artikel, Lager, Bestand, Bestellungen, Belege, Kunden, Lieferanten ...) und legt den **kleinen Altbestand-Datensatz** neu an (nicht den Demo-Datensatz). **Benutzer und Audit-Trail bleiben.** Außerhalb von `Development` verweigert der Server mit 403; die Knöpfe dafür erscheinen nur im Entwicklungsbuild (`npm run dev`).
- **Bulk-Seed** (`POST /api/admin/seed-bulk?count=1000`, nur `Development`): zusätzliche Zufallsdaten für Leistungstests (ca. 5 % Artikel, 10 % Bestand, 85 % Bestellungen).

---

## Tastenkürzel

| Shortcut | Was |
|---|---|
| **Cmd + K** / **Ctrl + K** | Globale Suche öffnen (Artikel, Lagerplätze, Bestellungen, Kunden; im Browser über die geladenen Daten) |
| **↑** / **↓** in der Suche | Treffer wählen |
| **Enter** | Treffer öffnen |
| **Esc** | Suche/Dialog schließen |

Die Seitenleiste ist nach Arbeitsbereichen gruppiert (Stammdaten, Wareneingang, Bestand, Auslieferung, Auswertung, System); jede Gruppe lässt sich auf- und zuklappen. **Theme** im Seitenleisten-Fuß: ☀ hell, 🌙 dunkel, 🖥 auto (folgt dem System); darunter der Umschalter **DE / EN** für die Sprache. Die Auswahl wird jeweils im Browser gespeichert. Als **PWA** lässt sich die App über HTTPS installieren; der Service-Worker läuft nur im Produktions-Build.

---

## Bekannte Einschränkungen

Was heute **nicht** oder nur eingeschränkt geht (Ausbau siehe [TODO.md](../TODO.md)):

- **Lagerstruktur:** keine Umlagerung (Bin zu Bin), keine Auslastungsansicht, kein Verschieben eines Regals in einen anderen Gang. Mehrere Lager sind nur im Layout-Editor vorbereitet: Bestand, Bestellungen und Wareneingang sind nie nach Lager gefiltert.
- **Wareneingang:** Positionen eines bestehenden Entwurfs lassen sich nur per API ergänzen oder entfernen; beim Wareneingang aus einer Bestellung gehen alle Zeilen in einen Ziel-Lagerplatz, Charge und MHD nur per API. Ein MHD-Pflichtfeld am Artikel gibt es nicht.
- **Artikel:** Alternativ-SKUs schlagen bei fehlendem Bestand keinen Ersatzartikel vor; Mobile-Picker, Globalsuche und Etiketten kennen die GTIN nicht als Barcode (nur die SKU wird gescannt und gedruckt).
- **Nur per API, ohne Oberfläche:** Stornieren von Wareneingang, Inventur, Einkaufsbestellung, Retoure, Nachschub-Aufgabe und Sendung; Abbrechen einer freigegebenen Welle; Retoure zu einer Bestellung; Bestandsverlauf; Waage.
- **Keine Bestandsreservierung:** Bestand wird beim Erzeugen der Pickliste nur gerechnet und erst beim Packen gebucht. Eine Fehlmenge beim Packen wird nicht als Rückstand geführt.
- **Carrier:** nur manuelle Tracking-Nummern; DHL/UPS nicht angebunden; keine Anbindung an ERP/Shop-Systeme (die Schnittstelle existiert, ein Import nicht). Der CSV-Import ersetzt sie für den Start, nicht für den Dauerbetrieb.
- **Backup:** nur für SQLite in der Anwendung; Backups sind unverschlüsselt; der Restore braucht danach einen Neustart; MySQL sichert man mit `mysqldump`.
- **Etiketten:** kein Netzwerkdruck zum Zebra, kein EAN-13-Barcode; die Lesbarkeit eines Ausdrucks ist noch nicht mit einem Handy geprüft.
- **Audit:** nicht für alle Objekte (siehe oben); Ledger-Einträge tragen keinen Benutzer.
- **Listen:** keine Seiten-Aufteilung (Paging), alles wird geladen; die Suche in der Artikelliste und die Globalsuche filtern im Browser bzw. über `?search=`.
- **Mehrsprachigkeit:** die Oberfläche ist zweisprachig (Deutsch, Englisch); Meldungen des Servers und die Doku bleiben deutsch, und die Statusfelder zeigen teils den englischen Serverwert (siehe [features/i18n.md](features/i18n.md#grenzen)).
- **Demo-Modus:** kein Demo-Hinweis in der Oberfläche und kein Demo-Reset-Endpunkt.

---

## Tipps

- **Demo-Vorführung mit vollem Datensatz:** Demo-Modus mit einer frischen Datenbank starten (siehe [GETTING_STARTED.md](GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden)). Der Reseed (nur `Development`) setzt dagegen nur den kleinen Altbestand-Datensatz neu auf.
- **Bulk-Seed für Leistungstests:** `POST /api/admin/seed-bulk?count=10000` (nur `Development`), dann Heatmap, Slotting und Pickrouten beobachten.
- **PWA installieren:** über HTTPS erscheint im Browser ein Install-Symbol; die App läuft dann als eigenes Fenster.
- **Mobile-Picker auf einem echten Gerät:** Der Entwicklungsserver muss im Netz lauschen (`npm run dev -- --host`), Adresse `http://<PC-IP>:5173/picker/<picklist-id>`. Die Kamera funktioniert dort **nicht** (kein HTTPS); für Scan-Tests einen HTTPS-Zugang (Docker-Profil `https`, Reverse-Proxy oder Tunnel) verwenden oder die Codes von Hand eintippen.
