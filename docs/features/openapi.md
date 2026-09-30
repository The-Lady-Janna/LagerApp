# Swagger und OpenAPI

Ausführliche Referenz zur maschinenlesbaren API-Beschreibung: wann sie läuft und wie man sie ein- oder ausschaltet, was sie enthält, wie man sie ausprobiert und wie neue Endpunkte und DTOs hineinkommen. Der Überblick über Anmeldung, Rollen, Fehlerformat und alle Endpunkte steht in [API.md](../API.md), die Schlüssel in [CONFIGURATION.md](../CONFIGURATION.md#umgebung-und-adressen).

Die API beschreibt sich selbst: **Swagger UI** unter `/swagger` zum Ausprobieren im Browser und das **OpenAPI-3-Dokument** unter `/swagger/v1/swagger.json` (Titel "Lager API", Version = Version der Anwendung) für Werkzeuge und Integratoren. Jede Operation (derzeit 171) hat eine Zusammenfassung, die Mindestrolle und die Erfolgs- und Fehlerantworten.

## Ein- und ausschalten

Swagger läuft in der Umgebung `Development` (Standard bei `dotnet run`) oder mit `Swagger:Enabled=true`; `Swagger:Enabled=false` schaltet es auch in `Development` ab. Maßgeblich ist `LagerOpenApi.IsEnabled` in `src/Lager.Api/Program.cs`:

| Umgebung | `Swagger:Enabled` | Swagger |
|---|---|---|
| `Development` | nicht gesetzt | **an** |
| `Development` | `false` | aus (ausdrücklich abgeschaltet) |
| `Production` oder jede andere | nicht gesetzt | **aus** (Standard, auch im Docker-Image) |
| `Production` oder jede andere | `true` | **an** |

- Die Umgebungsvariable heißt `Swagger__Enabled` (siehe [CONFIGURATION.md](../CONFIGURATION.md#umgebung-und-adressen)). Die Einstellung wird beim Start gelesen: nach einer Änderung die Anwendung neu starten.
- **Docker:** die `docker-compose.yml` reicht `Swagger__Enabled` nicht aus der `.env` durch (Liste: [CONFIGURATION.md](../CONFIGURATION.md#docker)). Zum Einschalten `Swagger__Enabled: "true"` in den Block `environment` der `docker-compose.yml` eintragen oder eine Zusatzdatei nutzen.
- Ist Swagger aus, gibt es `/swagger` und `/swagger/v1/swagger.json` nicht (404); auch die Oberfläche der Anwendung übernimmt diese Pfade nicht.

## Sicherheit

- **Die Beschreibung ist ohne Anmeldung lesbar**: `/swagger` und `/swagger/v1/swagger.json` brauchen kein Token. Sie enthält keine Daten, aber die komplette Liste der Endpunkte, Felder und Fehlercodes. **Die Endpunkte selbst bleiben geschützt**: jeder Aufruf aus Swagger UI braucht ein gültiges Token mit der passenden Rolle.
- Deshalb gilt: im Internet **nur bewusst einschalten** (Produktion: Standard aus lassen). Soll Swagger in einer Produktionsumgebung erreichbar sein, den Pfad `/swagger` am Reverse-Proxy auf ein internes Netz oder bestimmte Adressen beschränken.
- Swagger UI braucht Inline-Skripte: `/swagger` ist deshalb von der Content-Security-Policy ausgenommen (siehe [CONFIGURATION.md](../CONFIGURATION.md#https-reverse-proxy-und-rate-limiting)); alle anderen Antworten tragen sie.
- Das Token der Weboberfläche gilt in Swagger nicht (andere Origin, eigener Speicher): in Swagger gesondert anmelden.

## Ausprobieren

1. Die Anwendung in `Development` starten (`dotnet run --project src/Lager.Api`) und `http://localhost:5099/swagger` öffnen.
2. `POST /api/auth/login` ausführen (**Try it out**) und das `token` aus der Antwort kopieren. Beim allerersten Start gilt das Einmalpasswort des Admins, das sofort zu wechseln ist (`POST /api/auth/change-password`, liefert ein **neues** Token); siehe [GETTING_STARTED.md](../GETTING_STARTED.md#4-erstes-login).
3. Oben auf **Authorize** klicken und das Token eintragen. Das Schema ist ein HTTP-Bearer-Schema: Swagger stellt `Bearer` selbst voran, nur das Token einfügen.
4. Endpunkte ausführen. Ein Endpunkt mit höherer Rolle als die des Benutzers antwortet 403, ohne Token 401.

Wer lieber aus dem Editor arbeitet: `src/Lager.Api/Lager.Api.http` enthält Beispielanfragen (Anmeldung mit Passwortwechsel, Lagerstruktur, Artikel, Bestellung und Pickliste, Auswertungen, CSV, Backups) für Visual Studio, Rider und Visual Studio Code (Erweiterung "REST Client"). Wer die Beschreibung offline braucht, speichert das JSON aus einer Instanz mit Swagger (`/swagger/v1/swagger.json`).

## Was das Dokument enthält

- **Kopf:** Titel, Version der Anwendung, eine Beschreibung von Anmeldung, Fehlerformat und Konventionen (JSON in camelCase, Enums nur als Text, Zeitstempel in UTC, Beträge in Cent, Maße in Millimetern, Gewichte in Gramm) und das Sicherheitsschema **Bearer** (JWT).
- **Jede Operation** (derzeit 171) hat eine `summary` aus dem XML-Kommentar der Controller-Action und die **Mindestrolle als erste Zeile der Beschreibung** (z. B. "Berechtigung: Rolle Manager, Admin", "jeder angemeldete Benutzer" oder "ohne Anmeldung" beim Login). Geschützte Operationen tragen die Bearer-Anforderung.
- **Antworten:** mindestens eine Erfolgsantwort und eine Fehlerantwort. Geschützte Endpunkte nennen 401 und 403; je Endpunkt kommen 400, 404 und 409 dazu, wo sie vorkommen. **Alle Fehler sind `application/problem+json`** mit dem Schema `ProblemDetails` (`type`, `title`, `status`, `detail`, dazu `code`, `correlationId` und `error`; Validierungsfehler als `ValidationProblemDetails` mit `errors` je Feld). Fehlercodes: [API.md](../API.md#fehlercodes).
- **Anlegen** antwortet 201 mit dem Header `Location` (auch im Dokument beschrieben); Löschen von Stammdaten 204. Dateidownloads (PDF, ZPL, CSV, Backup) sind als Binärdaten beschrieben, nicht als JSON.
- **`GET /api/version`** (Tag `System`) liefert die App-Version und den Stand des Datenbankschemas; er ist wie jeder Endpunkt außer dem Login nur mit Anmeldung erreichbar, eine bestimmte Rolle braucht er nicht.

## Wie Beschreibungen ins Dokument kommen

Swagger liest die XML-Dokumentation der Assemblies aus dem Anwendungsverzeichnis (`LagerOpenApi` in `src/Lager.Api/Program.cs`, Dateien `Lager.Api.xml` und `Lager.Contracts.xml`; `dotnet publish` und damit auch das Docker-Image legen beide neben `Lager.Api.dll`):

- **Controller** (`src/Lager.Api/Controllers`): der `<summary>`-Kommentar einer Action wird zur Zusammenfassung, `<param>` zur Beschreibung der Parameter, `[ProducesResponseType]` zu den Antworten. Die Tests (`tests/Lager.Tests/WP30`) verlangen für jede Operation eine Zusammenfassung sowie eine Erfolgs- und eine Fehlerantwort: ein neuer Endpunkt ohne Kommentar oder ohne Fehlerantwort fällt dort auf.
- **DTOs** (`src/Lager.Contracts`): `src/Lager.Contracts/Lager.Contracts.csproj` erzeugt die Datei `Lager.Contracts.xml` (`GenerateDocumentationFile`); die Kommentare der Typen und Eigenschaften erscheinen als Beschreibung von Schemas und Feldern (Beispiel: die Felder `gtin` und `isCurrentlyActive` von `ArticleDto`, die Beschreibung von `CreateArticleRequest`). Das Fehlen eines Kommentars ist keine Warnung (`CS1591` ist ausgenommen, wie in `Lager.Api`).
- **Grenze:** nicht jedes DTO und nicht jedes Feld hat schon einen Kommentar; ein Feld ohne Kommentar erscheint ohne Beschreibung. Ein `///`-Kommentar direkt an einem Positionsparameter eines Records übernimmt der Compiler nicht (Warnung `CS1587`, in `Lager.Contracts.csproj` ausgenommen); wirksam sind der Kommentar am Record mit einem `<param name="...">`-Tag je Feld oder der Kommentar an einer Eigenschaft.

## Tests

- `tests/Lager.Tests/WP30/OpenApiDocumentTests.cs`: Kopf, Sicherheitsschema, Fehlerformat, Zusammenfassung und Antworten jeder Operation, Mindestrolle, `Location` an jedem 201, Schalter `Swagger:Enabled` in den vier Kombinationen aus der Tabelle oben.
- `tests/Lager.Tests/WP34/OpenApiContractsDocsTests.cs`: die XML-Dokumentation von `Lager.Contracts` wird erzeugt, DTO-Beschreibungen erscheinen im Dokument, und die Beschreibung ist ohne Anmeldung lesbar, während ein Endpunkt mit 401 geschützt bleibt.
