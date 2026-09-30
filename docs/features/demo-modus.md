# Demo-Modus: neutraler Demo-Datensatz mit Historie und Demo-Benutzern

Ausführliche Referenz zum Demo-Modus (Datensatz, Schalter, Grenzen). Die Anleitung zum Starten, auch mit Docker, steht in [GETTING_STARTED.md](../GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden), die Schlüssel in [CONFIGURATION.md](../CONFIGURATION.md#demo-modus-und-demo-daten), eine Anleitung für Screenshots in [screenshots/README.md](../screenshots/README.md).

Der Demo-Modus füllt eine **leere** Datenbank beim Start mit einem erfundenen, aber realistischen Betrieb: Stammdaten, rund 60 Tage
Historie und einem Ist-Stand in allen Bereichen. Damit zeigen Bestandsalarme, Lagerwert, Nachschub, Heatmap, ABC, Dead-Stock,
Picker-Performance und Chargen-Rückverfolgung sofort Daten. Er ist **standardmäßig aus** und läuft nie unbeabsichtigt in Production.

## Einschalten

| Schalter | Umgebungsvariable | Standard | Wirkung |
|---|---|---|---|
| `Demo:Enabled` | `Demo__Enabled` | `false` | Legt beim Start den Demo-Datensatz an, aber nur in eine leere Fachdatenbank. |
| (Kurzform) | `LAGER_DEMO` | nicht gesetzt | `1`, `true`, `yes` oder `on` setzt `Demo:Enabled=true`; `0`, `false`, `no` oder `off` setzt es auf `false` (schlägt die Konfigurationsdateien). Andere Werte werden ignoriert. |
| `Demo:AllowInProduction` | `Demo__AllowInProduction` | `false` | Erlaubt den Demo-Modus in der Umgebung `Production` (siehe unten). |
| `Database:Seed` | `Database__Seed` | `false` | **Veraltet.** Legt nur den kleinen Altbestand-Datensatz an (20 Artikel, 25 Bestandszeilen ohne Ledger, 8 Bestellungen) und warnt bei jedem Start im Log. Bei aktivem `Demo:Enabled` wird er ignoriert. |

Beispiele:

```bash
# lokal
LAGER_DEMO=1 dotnet run --project src/Lager.Api

# Docker ohne Compose: das Image läuft in der Umgebung Production, der Demo-Modus braucht deshalb die ausdrückliche Freigabe.
# Die Zugangsdaten stehen danach einmalig im Container-Log.
docker run -e LAGER_DEMO=1 -e Demo__AllowInProduction=true ... lager
docker logs <container> | grep -A 7 "Demo-Zugangsdaten"
```

Mit **Docker Compose** steht beides in der `.env` neben der `docker-compose.yml` (Vorlage: `.env.example`); die mitgelieferte `docker-compose.yml` reicht `Demo__Enabled` und `Demo__AllowInProduction` durch, eine Zusatzdatei ist nicht nötig:

```bash
Demo__Enabled=true
Demo__AllowInProduction=true
# danach (mit einem leeren Volume):  docker compose up --build  und  docker compose logs lager
```

Der Demo-Modus legt nur an, wenn **keine** Fachdaten da sind (keine Artikel, Lager, Lagerplätze, Bestände, Bestellungen, Kunden, Lieferanten;
Benutzer zählen nicht). Ist die Datenbank nicht leer, schreibt die Anwendung eine Info-Zeile und ändert nichts - der Seeder ergänzt oder repariert
nie bestehende Daten und legt gelöschte Demo-Daten nicht bei jedem Start neu an. Alles entsteht in **einer Transaktion**: scheitert etwas
mittendrin, bleibt die Datenbank leer und der Start bricht ab (beim nächsten Start läuft das Seeding erneut).

### Production

In der Umgebung `Production` bricht der Start mit `Demo:Enabled=true` (auch über `LAGER_DEMO=1`) mit einer klaren Meldung ab:
Demo-Daten und Demo-Benutzer gehören nicht in ein Produktivsystem. Nur mit zusätzlich `Demo:AllowInProduction=true` startet die Anwendung dort
mit Demo-Daten (etwa für eine öffentliche Vorführung). Alle anderen Umgebungen brauchen keine Freigabe. Das Docker-Image läuft standardmäßig
als `Production` (`ASPNETCORE_ENVIRONMENT`): ein Demo-Container braucht deshalb `Demo__Enabled=true` (bzw. `LAGER_DEMO=1`) **und** `Demo__AllowInProduction=true`. Die mitgelieferte
`docker-compose.yml` reicht `Demo__Enabled` und `Demo__AllowInProduction` aus der `.env` durch, nicht aber die Kurzform `LAGER_DEMO` (Anleitung: [GETTING_STARTED.md](../GETTING_STARTED.md#5-demo-modus-und-demo-daten-erkunden)).

## Demo-Benutzer und Zugangsdaten

Der Admin bleibt der Bootstrap-Admin (siehe Konfiguration). Zusätzlich legt der Seeder je Rolle einen Benutzer an:

| Benutzer | Rolle | Verwendung in der Historie |
|---|---|---|
| `picker`, `picker2`, `picker3` | Picker | die drei Picker der Picklisten (Picker-Performance zeigt drei Zeilen) |
| `packer` | Packer | packt die Picklisten, versendet |
| `receiver` | Receiver | Wareneingang, Retouren, Inventurzählung |
| `manager` | Manager | Einkauf, Inventur-Abgleich, Stornos |
| `viewer` | Viewer | nur lesen |

**Es gibt keine festen Passwörter im Repository.** Beim Seeding erzeugt der Seeder für jeden Benutzer ein zufälliges Passwort (20 Zeichen,
kryptografischer Zufall) und gibt es **genau einmal** als Warnung "Demo-Zugangsdaten" aus. Die Zeile erscheint in der **Konsole** (Docker: im
Container-Log), aber bewusst **nicht in der Logdatei** (`Logging:Directory`). Die Demo-Benutzer müssen ihr Passwort nicht ändern
(`MustChangePassword=false`, nur im Demo-Modus). Wurde die Ausgabe verpasst, hilft nur ein Zurücksetzen: als Admin unter **Benutzer** ein neues
Passwort vergeben. Existiert ein Benutzername schon (z. B. bei einem Neustart nach einem Reseed), bleibt dieser Benutzer unverändert und es wird
kein neues Passwort ausgegeben.

## Was der Datensatz enthält

Alles ist erfunden: keine echten Firmen, Marken, Personen oder Adressen; Mail-Domains nur `example.com`; GTINs aus dem Bereich der internen
Nummern (Präfix 20-29), die keinem Hersteller gehören.

| Bereich | Inhalt |
|---|---|
| Lager | `WH01` mit 2 Zonen (Kommissionierzone, Reservelager), 5 Gängen, 20 Regalen, 60 Lagerplätzen; 7 Wände mit Türen (Wegberechnung um Wände), Pickpunkte Start/Ende/beides, 2 Pickwagen-Konfigurationen |
| Lagerplätze | 8 Hot-Pick-Plätze mit Nachschub-Schwelle (Gänge A1/A2), Standardplätze, 24 Reserveplätze im Reservelager |
| Artikel | 52, davon 2 Bundles mit Komponenten; Preise, Min/Reorder/Max, Abmessungen und Gewicht, 9 mit gültiger GTIN, Alt-SKUs, 4 Saisonartikel (Sommer aktiv, Saison beendet, Winter noch nicht bestellbar), 4 Ladenhüter ohne Nachfrage |
| Partner | 3 Lieferanten, 5 Kunden mit Liefer- und Rechnungsadressen |
| Chargen | Ware mit MHD in Chargen; mindestens 3 Chargen mit Bestand laufen in den nächsten 30 Tagen ab, eine ist abgelaufen |
| Historie | rund 60 Tage: ca. 330 Bestellungen, 130 Picklisten (teils als Wellen, teils mit Pickwagen), 300 Sendungen, 30 Einkaufsbestellungen mit ihren Wareneingängen, Retouren mit QC, Nachschub-Aufgaben, Inventuren; ca. 900 Lagerbewegungen |
| Ist-Stand | Bestellungen in **allen** Status (New, Picking, Picked, Packed, Shipped, Cancelled); Picklisten in allen Status; Bestellungen im Entwurf/versendet/teilweise geliefert; Wareneingang im Entwurf; offene Retouren; offene Inventur; offene Nachschub-Aufgaben; Bestandsalarme (kritisch und Warnung) |

Die Historie liegt vollständig in der Vergangenheit (Zeitstempel relativ zum Startzeitpunkt): der Ist-Stand in den letzten fünf Stunden, der
Beginn rund 60 Tage zurück, Altbestand und Ladenhüter davor (damit "Dead-Stock" mit der Standardgrenze von 90 Tagen Treffer hat).

## Wie der Datensatz entsteht

- `DemoCatalog` (`src/Lager.Api/Seeding/DemoCatalog.cs`) hält die festen Inhalte: Artikel, Lieferanten, Kunden, Chargen, Benutzer, Lagerlayout.
- `DemoHistoryGenerator` (`DemoHistoryGenerator.cs`) simuliert den Betrieb Tag für Tag mit **festem Zufallsstartwert** (deterministisch: zwei Läufe mit
  demselben Bezugsdatum ergeben dieselben Artikel- und Bestandssummen; nur die Guids sind neu).
- Der **Bestand entsteht ausschließlich über `StockBooking`** (Wareneingang, Kommissionierung, Nachschub, Retoure, Inventur): Bestand und Ledger
  stimmen je Artikel überein, die Bewertung kommt vollständig aus dem Ledger. Bestellungen, Picklisten, Sendungen usw. laufen durch die echten
  Statusmethoden der Domain; Pickrouten und Bestandsallokation nutzen die echten Algorithmen (`StockAllocator`, Wegoptimierer).
- **Vergangenheits-Zeitstempel:** Die Domain setzt immer "jetzt". Nach jeder Fachaktion ersetzt der Generator diese Werte über den EF-Property-Zugriff
  (`Entry(e).Property("CreatedAt").CurrentValue = ...`) durch den simulierten Zeitpunkt, ohne Domain-Setter zu öffnen. Auch der Audit-Trail wird auf
  Zeit und Akteur umgeschrieben.
- Ein Lauf dauert je nach Rechner und Build etwa 5 bis 30 Sekunden (gemessen: rund 25 s in einem Debug-Build mit SQLite); die Anwendung nimmt in dieser
  Zeit noch keine Anfragen an. Der Start meldet Beginn und Ende im Log.

## Grenzen und offene Punkte

- **Reseed-Endpunkt:** `POST /api/admin/reseed` (nur `Development`) ruft weiter die alte Signatur `DemoDataSeeder.SeedAsync(db)` auf und legt den kleinen
  Altbestand-Datensatz an. Die neue Überladung `SeedAsync(db, IServiceProvider, ILogger)` ist für einen künftigen Demo-Reset-Endpunkt gedacht (siehe [TODO.md](../../TODO.md)); einen solchen Endpunkt gibt es noch nicht.
- **Kein Demo-Banner in der Oberfläche:** Die Anwendung meldet dem Frontend den Demo-Modus noch nicht.
- **`appsettings.Development.json`** setzt weiter `Database:Seed=true` (veraltet, kleiner Datensatz mit Warnung). Wer in der Entwicklung den vollen Datensatz will,
  setzt dort stattdessen `"Demo": { "Enabled": true }`.
- Die Zugangsdaten stehen nur einmal im Log (siehe oben); der Datensatz selbst enthält keine Passwörter.
- Der Demo-Modus ist für Vorführung, Entwicklung und Tests gedacht, nicht für den Produktivbetrieb.

## Health-Probes im Request-Log

Der Compose-Healthcheck ruft `/health/live` bzw. `/health/ready` alle 30 Sekunden auf und erzeugte je Aufruf eine Information-Zeile (rund 2900 Zeilen pro Tag).
Das Request-Log loggt gesunde Probes nur auf `Debug` (`LagerLogging.GetRequestLogLevel`); ein Fehler (5xx oder Ausnahme) bleibt eine
**Warnung**. Alle anderen Anfragen loggen wie bisher `Information`, bei 5xx `Error`. Wer die Probes sehen will, setzt `Serilog__MinimumLevel__Default=Debug`.
