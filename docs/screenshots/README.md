# Screenshots für README und Doku

In diesem Ordner liegen die Bilder, die [README.md](../../README.md) und [README.en.md](../../README.en.md) zeigen. **Es liegen noch keine Bilder hier:** sie entstehen einmalig durch den Eigentümer des Repositorys mit dem **Demo-Modus** und gehören erst danach ins Repository. Diese Seite beschreibt, wie.

Regeln:

- **Nur Demo-Daten.** Der Demo-Modus legt einen erfundenen Betrieb an (keine echten Firmen, Personen, Adressen oder Marken; Mail-Domains nur `example.com`). Nie Screenshots einer echten Datenbank, eines echten Kunden oder eines Backups einchecken.
- **Keine Zugangsdaten im Bild.** Die Zugangsdaten stehen nur in der Konsole; in den Bildern dürfen weder Passwörter noch Tokens, Hostnamen des eigenen Rechners oder der Adresszeile mit privaten Daten auftauchen (am besten nur die Seite ohne Browserrahmen aufnehmen).
- **Nichts erfinden.** Es gehören nur echte Bildschirmfotos der laufenden Anwendung hierher, keine Mockups und keine nachbearbeiteten Zahlen.

## 1. Demo-Modus starten

Der Demo-Modus füllt nur eine **leere** Datenbank (er ergänzt nie eine vorhandene). Dauer je nach Rechner rund 5 bis 30 Sekunden, in dieser Zeit nimmt die Anwendung noch keine Anfragen an. Hintergrund und Umfang des Datensatzes: [features/demo-modus.md](../features/demo-modus.md).

**Lokal (Entwicklung):** eine frische Datenbankdatei verwenden, damit nichts Altes im Weg ist (`*.db` ist in `.gitignore`):

```powershell
$env:LAGER_DEMO = "1"
$env:Database__ConnectionString = "Data Source=demo.db"
dotnet run --project src/Lager.Api
# zweites Terminal:
cd frontend/lager-ui
npm run dev
# → http://localhost:5173
```

Ein Mal gestartet und mit Daten gefüllt, bleibt die Datei `src/Lager.Api/demo.db` bestehen. Für einen sauberen Neustart die Anwendung stoppen, `demo.db`, `demo.db-wal` und `demo.db-shm` löschen und erneut starten.

**Docker:** das Image läuft als `Production`, der Demo-Modus braucht deshalb beide Schalter; die mitgelieferte `docker-compose.yml` reicht `Demo__Enabled` und `Demo__AllowInProduction` aus der `.env` durch. Beide Zeilen in die (nicht eingecheckte) `.env` neben der `docker-compose.yml` eintragen:

```bash
Demo__Enabled=true
Demo__AllowInProduction=true
```

```bash
docker compose up --build
```

Das Volume sollte neu sein (`docker compose down -v`, **löscht alle Daten**), sonst bleibt der Demo-Modus wirkungslos.

**Zugangsdaten** stehen einmalig in der Konsole bzw. im Container-Log, nicht in der Logdatei:

- Admin: Benutzer `admin` mit dem Einmalpasswort des ersten Starts (beim ersten Login ist ein neues Passwort fällig);
- Demo-Benutzer `manager`, `picker`, `picker2`, `picker3`, `packer`, `receiver`, `viewer` mit zufälligen Passwörtern: `docker compose logs lager` (mit `grep -A 7 "Demo-Zugangsdaten"` eingrenzen) bzw. die Konsole von `dotnet run`. Sie müssen ihr Passwort nicht ändern.

## 2. Vorbereitung

- **Fenstergröße:** Desktop-Aufnahmen mit einem Viewport von **1440 × 900 Pixeln**, Mobilaufnahmen (Mobile-Picker) mit **390 × 844 Pixeln** (Geräte-Werkzeugleiste des Browsers). Zoom 100 %, Gerätepixelverhältnis 1 (oder durchgehend 2 und dann alle Bilder so).
- **Thema:** hell als Standard. Wer zusätzlich dunkle Varianten möchte, hängt `-dark` an den Dateinamen (Umschalter im Seitenleisten-Fuß).
- **Sprache, Zeit:** die Screenshots zeigen die **deutsche** Oberfläche (Standardsprache; im Umschalter **DE / EN** der Seitenleiste bzw. der Anmeldeseite **DE** wählen, sonst folgt die Sprache dem Browser); Zeitstempel zeigen die Ortszeit des Browsers. Das ist gewollt, nichts anpassen.
- Browser-Erweiterungen, Lesezeichenleisten und Entwicklerwerkzeuge ausblenden; nur den Seiteninhalt aufnehmen.

## 3. Welche Bilder

Dateinamen in Kleinbuchstaben mit Bindestrichen, Format **PNG**, Breite höchstens 1440 Pixel. Jedes Bild einzeln auf **unter etwa 500 KB** halten (nötigenfalls mit einem PNG-Optimierer verkleinern): das Repository soll schlank bleiben.

| Datei | Seite (Route) | Anmeldung | Was im Bild zu sehen sein soll |
|---|---|---|---|
| `reports-dashboard.png` | Reports (`/reports`) | `manager` | Lagerwert, Live-Status, Kennzahlen, ABC-Analyse und Heatmap mit Daten |
| `warehouse-layout.png` | Lager-Layout (`/layout`) | `manager` | Draufsicht mit Regalen, Wänden und Pickpunkten, Heatmap eingeschaltet |
| `pick-route.png` | Picklisten (`/picklists`), eine offene Liste öffnen | `manager` | Pickroute um die Wände herum mit nummerierten Stopps |
| `orders.png` | Bestellungen (`/orders`) | `manager` | Liste mit Status und Bestandsampel |
| `stock-expiry.png` | Bestand (`/stock`) mit aufgeklappten Chargen | `manager` | Spalte "Nächstes MHD", Unterzeilen je Charge, Status-Pills |
| `traceability.png` | Chargen-Trace (`/traceability?lot=<Charge>`), eine vorhandene Charge der Demo-Daten eintragen | `manager` | Wareneingänge, Bestand, Bewegungen, möglicherweise betroffene Bestellungen |
| `warehouse-structure.png` | Lagerstruktur (`/warehouses`) | `manager` | Baum Lager → Zone → Gang → Regal mit Bin-Typen |
| `import-export.png` | Import & Export (`/import-export`), bei einem Import **Prüfen (Trockenlauf)** gedrückt | `manager` | Ergebnis des Trockenlaufs (Beispieldatei aus `docs/samples/`) |
| `labels.png` | Etiketten (`/labels`), Art "Lagerplatz", ein Regal ausgewählt | `manager` | Vorschau des A4-Bogens mit Code-128-Barcodes |
| `backup.png` | Backup & Restore (`/system`) | `admin` | Einstellungen und Liste (vorher ein Backup anlegen) |
| `mobile-picker.png` | Mobile-Picker (`/picker/<Pickliste>`) | `picker` | Ein Stopp mit Lagerplatz, Artikel und Zählung (Mobilgröße) |

Der Mobile-Picker braucht eine offene Pickliste: in den Demo-Daten gibt es Picklisten in allen Status; sonst unter **Bestellungen** eine neue Bestellung wählen und **Pickliste generieren**.

## 4. Einbinden

Nach dem Ablegen die Bilder im README im Abschnitt **Screenshots** verlinken (Platzhaltertext ersetzen), jeweils mit Alternativtext, z. B.:

```markdown
![Reports mit Kennzahlen, ABC-Analyse und Heatmap](docs/screenshots/reports-dashboard.png)
```

Dasselbe in `README.en.md` (gleiche Bilder, englischer Alternativtext). Danach prüfen:

```powershell
dotnet test tests/Lager.Tests --filter "FullyQualifiedName~WP18"
```

Die Doku-Tests (`tests/Lager.Tests/WP18`) prüfen, dass alle relativen Verweise auf vorhandene Dateien zeigen. `*.png` ist in `.gitattributes` als Binärdatei geführt (keine Zeilenenden-Umwandlung).
