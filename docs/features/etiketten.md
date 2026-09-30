# Etiketten: Druck im Browser (Code 128), ZPL-Download und Barcode im Lieferschein

Ausführliche Referenz zu den Etiketten (Druck im Browser, ZPL, Code 128, Lieferschein). Die Kurzfassung für die Bedienung steht in [USAGE.md](../USAGE.md#etiketten).

## Was es gibt

| Was | Wo | Für wen |
|---|---|---|
| **Etiketten im Browser drucken** (Lagerplatz, Artikel, Bestellung) mit Code 128 als Inline-SVG | Seite **Etiketten** (`/labels`, Navigation "Stammdaten") | Bürodrucker, Einzeletikett 50 × 30 mm oder A4-Etikettenbogen 3 × 8 |
| **ZPL herunterladen** für dieselbe Auswahl | Seite **Etiketten**, Knopf "ZPL herunterladen" | Zebra-Drucker (Datei an den Drucker senden oder in Zebra Designer öffnen) |
| **Echter Code-128-Barcode im Lieferschein-PDF** (Bestellnummer, Vektor-Rechtecke) | `GET /api/picklists/{id}/shipping-label.pdf` (Knopf im Packen-Dialog) | Scan der Bestellnummer beim Packen/Versenden |

Rolle: die Seite und die ZPL-Endpunkte verlangen **Picker** (Picker, Manager, Admin) wie bisher (Policy `Picker`).

## Bedienung

1. **Etikettenart** wählen: Lagerplatz, Artikel oder Bestellung.
2. **Auswahl** treffen: einzeln per Ankreuzfeld oder mit "Alle Treffer auswählen".
   - *Lagerplätze* haben Filter für **Lager, Gang, Regal** und eine Suche über die Codes. Für den **Sammeldruck** ein Regal
     (oder einen Gang, ein Lager) wählen und "Alle Treffer auswählen" drücken: alle Etiketten kommen in einem Druckjob,
     **sortiert nach Lagerplatz-Code** (natürliche Sortierung: `A-01-2` vor `A-01-10`).
   - *Artikel*: Suche über SKU, Name und GTIN; der Barcode kodiert die **SKU**.
   - *Bestellungen*: Suche über Nummer und Kunde; der Barcode kodiert die **Bestellnummer**, die Kundenreferenz steht darunter.
   Die Auswahl bleibt beim Filtern und beim Wechsel der Etikettenart erhalten.
3. **Format**: "Einzeletikett 50 × 30 mm" (jedes Etikett eine Seite, für Rollendrucker mit Windows-/CUPS-Treiber) oder
   "A4-Bogen 3 × 8" (24 Etiketten à 70 × 36 mm ohne Zwischenraum, oben und unten je 4,5 mm Rand).
4. **Kopien je Etikett** (1 bis 100; die Kopien eines Etiketts liegen nebeneinander) und bei einem A4-Bogen die **Startposition**
   (1 bis 24): so lässt sich ein angebrochener Bogen weiterverwenden.
5. Die **Vorschau** darunter ist die Druckvorlage. **Drucken** öffnet den Druckdialog des Browsers; gedruckt wird nur die Vorschau.
   Im Druckdialog **Skalierung 100 % ("Tatsächliche Größe")** und **Ränder "Keine"** einstellen, sonst passt das Raster nicht auf den Bogen.
6. **ZPL herunterladen** ruft je Etikett `GET /api/labels/{bin|article|order}/{id}.zpl?copies=n` auf (mit Token) und speichert
   **eine** Datei mit allen Etiketten (jedes ein eigenes `^XA…^XZ`, Kopien per `^PQ`). Dateiname: ein Etikett wie beim Server
   (`bin-A-01-01.zpl`), mehrere `etiketten-lagerplaetze-12.zpl`.

Hinweise der Seite: ein Code, den Code 128 nicht darstellen kann (z. B. mit Umlaut), erscheint nur als Text und wird gemeldet; ein
Code, der auf dem gewählten Format zu dicht würde (Balken schmaler als 0,19 mm), wird als "zu lang für dieses Format" gemeldet.
Ab 200 Etiketten (Druck) bzw. 100 Etiketten (ZPL) fragt die Seite nach; mehr als **1000 Etiketten samt Kopien** druckt der Browser
nicht (keine Vorschau, ZPL geht weiter, die Kopien vervielfacht dann der Drucker).

**Verweis von anderen Seiten:** `/labels?type=bin|article|order&ids=<id>,<id>` öffnet die Seite mit dieser Auswahl. Ein Knopf
"Etikett drucken" in Artikelliste, Bestellung und Layout-Editor kann darauf verlinken (die Seiten gehören anderen Paketen).

## Code 128

Die Encoder gibt es zweimal, mit **denselben Regeln und denselben Testvektoren**:

- Backend: `src/Lager.Api/Labels/Code128Encoder.cs` (Lieferschein-PDF), Tests `tests/Lager.Tests/WP26/Code128EncoderTests.cs`
- Frontend: `frontend/lager-ui/src/features/labels/code128.ts` (Etiketten), Tests `frontend/lager-ui/src/tests/WP26/code128.test.ts`

Regeln: **Set B** (ASCII 32 bis 126) und **Set C** (zwei Ziffern je Symbol); Start B/C, Prüfsumme (Start + Summe Wert × Position) mod 103,
Stopp. Die Wahl ergibt die **kürzeste Symbolfolge**, bei Gleichstand gilt Start in B vor Start in C und im Set bleiben vor Umschalten
(`ORD-DEMO-01` bleibt in B; `ART-123456` wechselt vor den sechs Ziffern nach C; `1234567890` startet in C; eine ungerade Ziffernfolge
kodiert die erste Ziffer in B). Set A, FNC-Zeichen und alles außerhalb von ASCII 32 bis 126 sind nicht vorgesehen: ein Zeichen, das
sich nicht kodieren lässt, ist ein Fehler (`ArgumentException` / `Code128Error`), nie eine stille Änderung. Ausgabe: die Symbolwerte,
die Prüfsumme, die Balken-/Lückenbreiten und die Modulfolge (`"1101001…"`); die Ruhezone (10 Module) fügt der Zeichner hinzu.

Die Symboltabelle (ISO/IEC 15417) ist durch Tests abgesichert: 106 verschiedene Muster zu je 11 Modulen mit drei Balken und gerader Zahl
schwarzer Module, dazu die bekannten Normmuster (Start B `11010010000`, Start C `11010011100`, Stopp `1100011101011`, Leerzeichen,
"A", "0") und eine Rückwärtsprüfung der Symbolfolge auf 500 Zufallstexte.

Beispiel `ORD-DEMO-01`: Symbole `104, 47, 50, 36, 13, 36, 37, 45, 47, 13, 16, 17, 11, 106` (Start B, elf Zeichen, Prüfsumme 11, Stopp),
156 Module.

## Lieferschein-PDF

`ShippingLabelRenderer` zeichnet den Barcode der Bestellnummer je Seite als Reihe von **QuestPDF-Rechtecken** (ein gefülltes Rechteck je
Balken, Modul 1,5 pt, Höhe 50 pt, Ruhezone 10 Module, mittig; bei sehr langen Nummern proportional schmaler) und die Nummer im Klartext
darunter. Kann die Nummer nicht kodiert werden (Zeichen außerhalb von ASCII 32 bis 126), bleibt der bisherige Text-Kasten — das PDF
scheitert nie an der Bestellnummer. Der Test liest die Rechtecke aus dem Seiteninhalt des PDFs und vergleicht Lage und Breite mit der
Modulfolge des Encoders.

## ZPL-Endpunkte

`GET /api/labels/bin/{binId}.zpl`, `/article/{articleId}.zpl`, `/order/{orderId}.zpl` (Rolle Picker), unverändert im Pfad; jetzt einheitlich:

- Content-Type `text/plain; charset=utf-8` (UTF-8 ohne BOM), Dateiname `bin-<Code>.zpl` / `article-<SKU>.zpl` / `order-<Nummer>.zpl`;
  Zeichen außer Buchstaben, Ziffern, `.`, `-`, `_` im Dateinamen werden zu `_` (kein Pfad aus einem Code).
- Neu: **`?copies=n`** (1 bis 500, sonst 400 `validation_failed`) setzt `^PQn` vor `^XZ`.
- Neu: `^CI28` (UTF-8) im Etikett, damit Umlaute im Artikelnamen richtig gedruckt werden.
- Zeilenumbrüche in Nutzerdaten werden zu Leerzeichen, `^` und `~` entfallen wie bisher (kein Einschleusen von ZPL-Befehlen).
- Der Barcode ist weiter `^BC` (Code 128, der Drucker kodiert selbst).

Es gibt bewusst **keine neuen Endpunkte**: den Sammeldruck (alle Plätze eines Regals, Gangs, Lagers) stellt die Oberfläche aus dem
Lager-Layout zusammen und hängt die einzelnen ZPL-Antworten aneinander.

## Konfiguration und Anpassung

Es gibt keine Einstellungen in `appsettings.json`. Das Raster des A4-Bogens (Spalten, Zeilen, Etikettengröße, Ränder, Zwischenräume) steht
in `LABEL_FORMATS` (`frontend/lager-ui/src/features/labels/labelLayout.ts`); die Vorschau und `@page` richten sich danach. Das Papier
ist immer weiß mit schwarzem Druck (auch im Dark-Mode), die Formatierung steht seitenlokal in `features/labels/labels.css`.

## Grenzen

- **Kein EAN-13/GTIN-Barcode**: der Artikel-Barcode ist die SKU (Code 128). Ein GTIN-Etikett bräuchte einen zweiten Encoder (EAN-13).
- Der Barcode des **Mobile-Pickers** verlangt Bin-Codes; ein Bin-Code mit Zeichen außerhalb von ASCII 32 bis 126 lässt sich nicht als
  Code 128 drucken (die Seite meldet es).
- **Druckskalierung und Ränder** unterscheiden sich je Browser und Drucker; ohne 100 % und "Keine Ränder" verschiebt sich das Raster.
  Der Bogen sollte einmal auf Papier gegen die Etiketten gehalten werden.
- Das Etikettenraster kennt nur zwei Formate (Einzeletikett 50 × 30 mm, A4 3 × 8 zu 70 × 36 mm); andere Bögen erfordern einen Eintrag in `LABEL_FORMATS`.
- Netzwerkdruck direkt an einen Zebra-Drucker (TCP) gibt es nicht; die ZPL-Datei wird gespeichert und an den Drucker geschickt.
- Höchstens 1000 Etiketten (samt Kopien) je Druckauftrag im Browser; ZPL-Kopien bis 500 je Etikett.
- Der **ZPL-Download** ruft je Etikett einen Endpunkt auf (sechs gleichzeitig). Ist das Rate-Limit des Servers aktiv, begrenzt
  `Security:GlobalRateLimitPerMinute` (Standard 600 Anfragen je Minute und Client-IP) eine Auswahl auf gut 500 Etiketten je Download:
  darüber antwortet der Server mit 429, der Download bricht mit einer Meldung ab und speichert keine Teildatei. Dann die Auswahl aufteilen
  (z. B. je Gang) oder das Limit anheben. Der Druck im Browser ist davon nicht betroffen (er ruft den Server nicht je Etikett auf).

## Manuelle Prüfung (Scan mit dem Handy)

Die Akzeptanz "ein gedruckter oder als PDF gespeicherter Bin-Etikettenbogen liefert beim Scannen mit einem Handy exakt den Bin-Code"
lässt sich nicht automatisch prüfen und ist **noch nicht durchgeführt** (die automatischen Tests sichern Encoder, Modulfolge und Raster ab,
nicht die Lesbarkeit auf Papier). Vorgehen:

1. Seite **Etiketten**, Art "Lagerplatz", ein Regal wählen, "Alle Treffer auswählen", Format "A4-Bogen 3 × 8".
2. **Drucken**, im Druckdialog "Als PDF speichern" (oder auf Papier) mit Skalierung 100 % und Rändern "Keine".
3. Mit einer Barcode-Scanner-App des Handys (oder der Kamera-Funktion des Mobile-Pickers, `/picker/:id`) mindestens drei Etiketten
   vom Bildschirm bzw. Papier scannen, darunter ein Code mit Ziffernfolge (Set C, z. B. `A-01-10`) und einen mit Kleinbuchstaben.
4. Ergebnis eintragen: gescannter Text = Lagerplatz-Code, Datum, Gerät/App, Druckerauflösung.

Automatische Vorprüfung ohne Handy (Vorprüfung im Review): Die Vorschau samt `index.css` und Seitengerüst wurde mit Edge (headless) in ein PDF
gedruckt (24 Etiketten = 1 Seite, 25 = 2 Seiten, Einzeletikett 50 × 30 mm = je Etikett eine Seite). Die Balken-Rechtecke aus dem
PDF ließen sich mit einem unabhängigen Decoder (Symboltabelle nach ISO/IEC 15417, Prüfsumme geprüft) wieder zu genau den Lagerplatz-Codes
lesen, darunter Set-C-Ziffernfolgen (`A-01-1234567`) und Kleinbuchstaben. Das ersetzt den Scan mit einem echten Handy und einem echten
Ausdruck nicht.

Ergebnis der manuellen Prüfung: _offen_.
