# Tests des Backends (`tests/Lager.Tests`)

Ein xUnit-Projekt für das ganze Backend. Es startet die API bei Bedarf im Speicher (`TestServer`) gegen eine frische SQLite-Datei im
Temp-Verzeichnis, braucht also weder eine laufende Instanz noch eine vorhandene Datenbank und ändert nie die Entwickler-Datenbank.

```bash
dotnet test tests/Lager.Tests          # alle Backend-Tests
dotnet test Lager.sln                  # dasselbe über die Solution
```

Die Tests sind nach Arbeitspaketen (`WP01` ... `WP21`) geordnet; was dahinter steckt, steht jeweils im Klassenkommentar.

## Teststrategie: die Ebenen

Jedes Verhalten wird auf der **untersten Ebene** getestet, auf der es sich prüfen lässt. Höhere Ebenen sichern das Zusammenspiel ab,
nicht die Einzelregel.

| Ebene | Was wird geprüft | Wie | Beispiele |
| --- | --- | --- | --- |
| **Domain-Unit** | Regeln der Entitäten, Zustandsmaschinen, reine Algorithmen (Geometrie, Routen, Packen) | ohne Host, ohne Datenbank, `new` und Assert | `WP06/*StateTests`, `WP08/GeometryTests`, `WP08/PackingOptimizerTests`, `WP13/DomainRuleTests` |
| **Service** | Application-Services mit echter Datenbank: Buchungen, Allokation, Nummernkreise | Dienst über `factory.Services` in eigenem Scope aufrufen (jeder Aufruf wie ein Request) | `WP07/PickListFlowTests`, `WP10/*`, `WP14/*` |
| **API-Integration** | ein Endpunkt über HTTP: Statuscode, Fehlerformat, Rollen, Validierung | `LagerApiFactory` + eingeloggter `HttpClient` | `WP01/*`, `WP02/RolePolicyMatrixTests`, `WP12/*` |
| **Ende-zu-Ende** | ganze Geschäftsabläufe über HTTP mit den echten Rollen, dazu Invarianten, Parallelität, Neustart | `WorldBuilder` + `ApiCalls` (siehe unten) | `WP21/*` |
| **Betrieb** | Schema-Upgrade, Legacy-Datenbanken, Backup und Restore | eigene Factory mit fester Datenbankdatei | `WP09/*`, `WP21/LegacyDatabaseStartTests`, `WP21/BackupRestoreRoundtripTests` |
| **Frontend** | Komponenten, Stores, Hooks | Vitest, siehe [Frontend-Tests](#frontend-tests-vitest) | `frontend/lager-ui/src/tests/**` |

Regeln, die sich aus der Tabelle ergeben:

- Eine Fehlerbehebung bekommt einen Test, der **ohne die Korrektur fehlschlägt**. Reine Logik gehört in einen Unit-Test ohne Host.
- Ein Ablauf-Test prüft nach jedem Schritt den Zustand (Bestellstatus, Bestand, Ledger), nicht nur den Statuscode des letzten Aufrufs.
- Fehlerfälle prüfen den Statuscode **und** den maschinenlesbaren `code` (z. B. `order_not_cancellable`), nicht den deutschen Text.

## Ordner, Namespaces, Namen

- `tests/Lager.Tests/<WPID>/` für die Tests eines Arbeitspakets, bei themenübergreifenden Tests ein sprechender Ordnername.
  Namespace `Lager.Tests.<WPID>` (bzw. `Lager.Tests.<Thema>`), eine Testklasse pro Verhalten, Dateiname = Klassenname.
- `tests/Lager.Tests/Infrastructure/` enthält die gemeinsamen Hilfen (Factory, Anmelden, Testwelt). Sie werden erweitert, nicht umgebaut:
  Ein Test, der etwas anderes braucht, bekommt eine eigene Hilfsklasse in seinem Ordner (wie `WP21/RestartableFactory`).
- Testnamen beschreiben das Verhalten als Satz: `Repeated_generate_pack_and_receive_are_rejected_and_book_nothing_a_second_time`.

## Die gemeinsamen Hilfen

### `LagerApiFactory`

`WebApplicationFactory<Program>` mit Umgebung `Testing`, eigener SQLite-Datei je Instanz, `Seed=false` (keine Demodaten), festem
Signing-Key und dem Bootstrap-Admin `admin`. Konfiguration läuft über `UseSetting` (nur so sieht `Program.cs` die Werte schon beim Start).
Jede Instanz hat ihre eigene Datenbank, Testklassen laufen deshalb parallel, ohne sich zu stören.

- **Eine Factory pro Testklasse** (`IClassFixture`), nicht pro Test: der Start und der erste Login (BCrypt) kosten Zeit.
- Tests, die **globalen Zustand** ändern (alle Picklisten löschen, Inventur ohne Platzfilter, Nummernkreis zurücksetzen), bekommen eine eigene Klasse
  und damit eine eigene Factory.

### Anmelden: `AsAdminAsync`, `AsReadyAdminAsync`, `CreateClientWithRolesAsync`

Die API ist standardmäßig geschlossen: jeder Endpunkt außer dem Login braucht ein Token, viele eine Rolle. Der Bootstrap-Admin muss sein
Passwort beim ersten Login ändern. `AsReadyAdminAsync()` erledigt das einmalig und liefert einen einsatzbereiten Admin-Client;
`CreateClientWithRolesAsync(...)` legt einen Nutzer mit genau diesen Rollen an (`Admin`, `Manager`, `Picker`, `Packer`, `Receiver`, `Viewer`).
Rollen sind **nicht** hierarchisch (ein `Picker` darf nicht packen); nur `Manager` und `Admin` schließen die übrigen ein.

### `WorldBuilder` (`Infrastructure/WorldBuilder.cs`)

Baut die Testwelt für Ablauf-Tests. Stammdaten entstehen direkt über den `DbContext` der Factory (dafür gibt es keine Endpunkte), die
Geschäftsvorgänge laufen danach über HTTP.

```csharp
public class MeinAblaufTests : IClassFixture<WorkflowFixture>   // WP21: Factory + Standard-Lager pro Klasse
{
    private readonly WorkflowFixture _fx;
    public MeinAblaufTests(WorkflowFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Ein_Ablauf()
    {
        var w = _fx.W;
        var manager = await w.ClientAsync("Manager");           // eingeloggter Client je Rollenkombination (wiederverwendet)
        var packer = await w.ClientAsync("Packer");
        var article = await w.AddArticleAsync(priceCents: 120); // Artikel mit eindeutiger SKU
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, _fx.Warehouse.PickA, 40, "LOT-1", WorldBuilder.InDays(45)));

        var (order, list) = await manager.PackedOrderAsync(article.Id, 12);   // Bestellung -> Pickliste -> gepackt (HTTP)
        Assert.Equal(28, await manager.TotalStockAsync(article.Id));
        Assert.Empty(await w.LedgerViolationsAsync(article.Id));               // Bestand und Ledger stimmen überein
    }
}
```

| Baustein | Zweck |
| --- | --- |
| `BuildAsync()` | Standard-Lager: Lager, Zone, Gang, Regal, dazu `PickA`, `PickB` (Standard), `HotPick` (Schwelle 10) und `Reserve` |
| `AddSiteAsync()`, `AddBinAsync(...)`, `AddArticleAsync(...)`, `AddSupplierAsync()` | einzelne Stammdaten, alle mit eindeutigen Namen |
| `ClientAsync(rollen)`, `AdminAsync()`, `Anonymous()` | eingeloggte Clients je Rolle bzw. ohne Token |
| `AddLegacyStockAsync(...)` | Bestand **ohne** Movement ("Altbestand"); danach gilt die Ledger-Invariante für diesen Artikel nicht mehr |
| `MovementsAsync`, `StockRowsAsync`, `QuantityAsync` | Zustand direkt aus der Datenbank lesen |
| `LedgerViolationsAsync(articleId?)` | prüft die Bestandsinvarianten und liefert die Verstöße als Text (leer = konsistent) |

Der Builder akzeptiert jede `WebApplicationFactory<Program>` mit dem Bootstrap-Admin aus `LagerApiFactory` - auch eine eigene mit fester
Datenbankdatei.

### Die Ledger-Invariante

Jede Bestandsänderung schreibt genau eine Movement (bei Sperr- und Ausschussbuchungen der Retoure ein Paar mit Summe 0). Daraus folgt für
jeden Artikel: **Summe(Movement-Deltas) = Summe(Bestandsmengen)** - sogar je (Artikel, Lagerplatz, Charge). Dazu kommt: kein negativer
Bestand, keine Bestandszeile doppelt je (Artikel, Lagerplatz, Charge). `WorldBuilder.LedgerViolationsAsync` prüft genau das; jeder Test, der
Bestand bucht, sollte es am Ende aufrufen.

### WP21-Hilfen (`WP21/`)

- `ApiCalls`: typisierte HTTP-Aufrufe (`PlaceOrderAsync`, `GenerateAsync`, `PackAsync`, `ReceiveAsync`, `ShipAsync` ...). Die Varianten ohne Suffix
  erwarten Erfolg und zeigen bei Abweichung den Body der Antwort; `...RawAsync` liefert die Antwort für Fehlerfälle.
- `WorkflowFixture`: Factory + Standard-Lager pro Testklasse.
- `RestartableFactory`: API auf einer Datenbankdatei in vorgegebenem Verzeichnis, nacheinander mehrfach startbar (Neustart nach Restore,
  Start auf einer Legacy-Datenbank).

## Konventionen für neue Tests

- **Eindeutige Namen** (`WorldBuilder.Unique`, SKU, Bestellnummer, Lagerplatz): mehrere Tests teilen sich eine Factory.
- **Keine festen Kalenderdaten** in Erwartungen: MHD relativ zu heute (`WorldBuilder.InDays`), Nummern nur per Muster prüfen.
- **Deterministisch**: keine Wartezeiten und kein `Thread.Sleep`; Zufall nur mit festem Seed (`LedgerInvariantTests`).
- **Parallelität** testen mit Startschuss (`TaskCompletionSource`) und mehreren Runden mit frischen Belegen, nicht mit einem einzelnen Versuch:
  ein zufällig serialisierter Ablauf würde den Fehler verstecken (siehe `ConcurrencyScenarioTests`).
- Ein Test räumt nichts auf, was die Factory nicht selbst entsorgt; eigene Dateien (Temp-Verzeichnisse) löscht er in `finally`
  (nach `SqliteConnection.ClearAllPools()`).
- Die Produktion wird für Tests nicht umgebaut. Weicht das Ist-Verhalten vom Plan ab, wird der Test an das dokumentierte Ist-Verhalten
  angepasst und die Abweichung gemeldet (siehe unten).

## Ausführen

```bash
dotnet test tests/Lager.Tests --filter "FullyQualifiedName~WP21"        # nur ein Paket
dotnet test tests/Lager.Tests --filter "FullyQualifiedName~PickList"    # nach Namensteil
dotnet test tests/Lager.Tests --logger "console;verbosity=normal"        # Namen und Dauer je Test
dotnet test Lager.sln -c Release                                          # im Release-Modus
dotnet build Lager.sln -p:LagerStrict=true                                # Warnungen als Fehler
```

### Mit Coverage

`coverlet.collector` ist eingebunden:

```bash
dotnet test tests/Lager.Tests --collect:"XPlat Code Coverage"
```

Die Ergebnisse liegen als `coverage.cobertura.xml` unter `tests/Lager.Tests/TestResults/<guid>/`. Für einen lesbaren Bericht:

```bash
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator -reports:"tests/Lager.Tests/TestResults/**/coverage.cobertura.xml" -targetdir:coverage -reporttypes:Html
```

## Frontend-Tests (Vitest)

Das Frontend hat ein eigenes Test-Setup (Vitest, jsdom, Testing Library); die Tests liegen unter `frontend/lager-ui/src/tests/`.

```bash
cd frontend/lager-ui
npm test              # einmal ausführen (vitest run)
npm run test:watch    # beim Entwickeln
npm run typecheck     # tsc -b
npm run lint          # eslint
```

Ein Blick in `frontend/lager-ui/package.json` zeigt die Skripte. Vor einem Pull Request gehören Backend-Tests, `npm test`,
`npm run typecheck` und `npm run lint` zusammen.

## Die Szenarien von WP21

Regressionsnetz über die kritischen Abläufe, jeweils über HTTP und mit den echten Rollen.

| Szenario | Test |
| --- | --- |
| 1 Wareneingang mit Charge/MHD, Bestand je Charge | `GoodsReceiptScenarioTests` |
| 2 Bestellung -> Pickliste -> Picken -> Packen -> Sendung -> Versendet | `OrderToShipmentScenarioTests` |
| 3 Storno-Pfade (Bestellung in jedem Status, Sendung, Wareneingang, Einkaufsbestellung) | `CancellationScenarioTests` |
| 4 Inventur mit Differenz und Bewegung während der Zählung | `StockProcessScenarioTests` |
| 5 Retoure A-/B-Ware und Ausschuss | `StockProcessScenarioTests` |
| 6 Nachschub-Scan und -Abschluss | `StockProcessScenarioTests` |
| 7 Einkaufsbestellung -> Wareneingang -> Buchen (Teillieferung) | `GoodsReceiptScenarioTests` |
| 8 Doppeltes Packen/Erzeugen/Buchen, Zurücksetzen der Picklisten | `IdempotenceScenarioTests`, `PickListResetScenarioTests` |
| 9 Rollen-Stichprobe an den Grenzen des Ablaufs | `RoleAndErrorScenarioTests` |
| 10 Fehlerformat (`application/problem+json`, `code`, `correlationId`) | `RoleAndErrorScenarioTests` |
| 11 Backup -> Restore -> Neustart | `BackupRestoreRoundtripTests` |
| Ledger-Invarianten nach 200 zufälligen Buchungen | `LedgerInvariantTests` |
| Parallelität (Packen, Erzeugen, Wareneingang/Retoure/Nachschub, Nummernkreise) | `ConcurrencyScenarioTests` |
| Start auf einer Legacy-Datenbank (per SQL erzeugte alte Struktur) | `LegacyDatabaseStartTests` |

## Mutationsprobe (manuell)

Ein Test ist nur etwas wert, wenn er einen Fehler bemerkt. Für WP21 wurde das von Hand geprüft: absichtlich einen Fehler in die Produktion
einbauen, die WP21-Tests ausführen, den Fehler wieder zurücknehmen. Stand der Probe (WP21, alle Läufe mit `dotnet test tests/Lager.Tests --filter "FullyQualifiedName~WP21"`):

| Eingebauter Fehler | Ergebnis |
| --- | --- |
| **Packen ohne Status-Guard**: Prüfung auf Completed/Cancelled in `PickListService.PackAsync`, in `PickList.MarkPacked` und die Bestätigungsprüfung in `PickItem.ConfirmPacked` entfernt | rot: `IdempotenceScenarioTests` (zweites Packen liefert 200), `CancellationScenarioTests` (Packen einer stornierten Liste), `RoleAndErrorScenarioTests` (Konflikt beim Packen), `ConcurrencyScenarioTests` (Packen zweimal gleichzeitig: 200/200) |
| **Kein Pick-Movement beim Packen** (`_movements.AddAsync` in `PackAsync` entfernt) | rot: 15 von 28 Tests, u. a. `LedgerInvariantTests`, `OrderToShipmentScenarioTests`, `IdempotenceScenarioTests`, `BackupRestoreRoundtripTests` |
| **Nachschub verliert die Charge** (Ziel-Buchung in `ReplenishmentService.CompleteAsync` ohne Charge/MHD) | rot: `StockProcessScenarioTests` (Nachschub behält Charge und MHD) |
| **Concurrency-Token wird nicht mehr rotiert** (`Entity.Touch` ohne neues Token) | rot: `ConcurrencyScenarioTests` (Erzeugen, Packen, Wareneingang/Retoure/Nachschub gleichzeitig: beide Aufrufer erfolgreich) |

Nur den Guard im Service zu entfernen genügt nicht: die Domain prüft denselben Zustand noch einmal (Verteidigung in der Tiefe), das Packen
bleibt dann abgelehnt. Wer die Probe wiederholt, muss deshalb alle Ebenen der jeweiligen Prüfung entfernen.

## Bekanntes Ist-Verhalten (Abweichungen vom Plan)

- **Fehlerformat**: `PickListsController`, `OrdersController` (inklusive Storno), `ShipmentsController`, `PickWavesController` und
  `CustomersController` fangen fachliche Fehler noch lokal und antworten mit `{ error }` bzw. `{ code, error }` als `application/json`
  (ohne `correlationId` im Body; der Header `X-Correlation-Id` ist gesetzt). Alle übrigen Controller nutzen die zentrale Abbildung und
  antworten mit `application/problem+json`. `RoleAndErrorScenarioTests` prüft für die zentralen Endpunkte das volle Format und für die lokalen nur,
  was in beiden Formaten gilt (Status, lesbarer Text in `error`, Korrelations-Header, kein Stacktrace); sobald ein lokaler Endpunkt
  `application/problem+json` liefert (Arbeitspaket "Controller-Aufräumen"), prüft derselbe Test dort auch `code` und `correlationId`.
