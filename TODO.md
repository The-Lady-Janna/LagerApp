# Roadmap

**Stand:** Vor 1.0, Version 0.1.0 ([CHANGELOG.md](CHANGELOG.md)). Das System ist funktional vollständig für den Kernprozess Wareneingang → Bestand → Bestellung → Pickliste → Packen → Versand → Retoure, inklusive Inventur, Nachschub, Einkauf, Auswertungen, Lagerstruktur, Chargen/MHD, CSV-Import/-Export, Etiketten und Backup. Die API ist standardmäßig geschlossen (Anmeldung, Rollen), Fehler kommen einheitlich, der Bestand läuft über ein Ledger, ein Docker-Setup und ein Demo-Modus liegen bei, und die CI läuft. Offen sind vor allem: einige Lücken in der Oberfläche (vieles geht nur per API), echte Integrationen (Carrier, Shop/ERP, Hardware), Verpackungseinheiten und Artikelvarianten, ein paar kleine Restpunkte und die Veröffentlichung (Lizenz, praktischer Docker-Test, Screenshots).

Legende: `[x]` erledigt, `[ ]` offen. Die Ausgangslage je Bereich beschreibt [docs/USAGE.md](docs/USAGE.md#bekannte-einschränkungen).

---

## ✅ Was steht

### Sicherheit und Betrieb
- [x] **Standardmäßig geschlossene API:** `FallbackPolicy` (angemeldet, kein ausstehender Passwortwechsel) plus `RequireAuthorization()` an jedem Controller; anonym sind nur der Login und die Health-Endpunkte.
- [x] **Rollen-Policies an allen Endpunkten** (Admin, Manager, Receiver, Picker, Packer; Viewer nur lesen), geprüft durch eine Endpunkt-Matrix gegen die echten Routen.
- [x] **Kein Standardpasswort:** Bootstrap-Admin mit Einmalpasswort auf der Konsole oder `Auth:BootstrapAdminPassword`; Passwortwechsel-Zwang serverseitig.
- [x] **JWT-Key-Pflicht** (`Jwt:SigningKey` oder `Jwt:KeyFile`), Mindestlänge, Ablehnung des früheren Beispiel-Keys; **Token-Widerruf** bei Deaktivierung, Passwort- und Rollenänderung.
- [x] **Login-Härtung:** gleiche Antwort für jeden Fehler, Kontosperre (konfigurierbar), Rate-Limit je IP, Sicherheits-Log (`SecurityAudit`).
- [x] **HTTPS opt-in** (HSTS, Weiterleitung), Forwarded-Header für Reverse-Proxys, Host-Filter (`AllowedHosts`), Sicherheits-Header, CORS mit konkreter Origin-Liste.
- [x] **Logging:** Serilog (Konsole + Datei), Verzeichnis, Aufbewahrung (Standard 14 Tage) und Level per Konfiguration (`Logging:Directory`, `Logging:RetainedFileCount`, Abschnitt `Serilog`), Korrelations-ID je Anfrage, auch in Fehlerantworten.
- [x] **Health-Endpunkte** `/health/live` und `/health/ready` (anonym, nur der Status; 503 bei nicht erreichbarer Datenbank) und **Auslieferung des Frontends durch das Backend**, sobald `wwwroot/index.html` vorhanden ist (SPA-Fallback, Cache-Regeln).
- [x] **Docker:** `Dockerfile` (Oberfläche und API in einem Container), `docker-compose.yml` mit Volume für Datenbank, JWT-Schlüssel, Logs und Backups, MySQL-Zusatzdatei, Caddy-Profil für HTTPS, `.env.example`; Anleitung in [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#schnellstart-mit-docker), Einstellungen in [docs/CONFIGURATION.md](docs/CONFIGURATION.md#docker).
- [x] **Demo-Modus** (`Demo:Enabled` bzw. `LAGER_DEMO=1`): neutraler Demo-Datensatz mit rund 60 Tagen Historie und Demo-Benutzern, nur in eine leere Datenbank, in `Production` nur mit Freigabe ([docs/features/demo-modus.md](docs/features/demo-modus.md)).
- [x] **Backup-Oberfläche:** Seite **System > Backup & Restore** mit Liste, Download, Zeitplan (`Backup:Schedule`), Aufbewahrung (`Backup:RetentionCount`) und Restore mit Bestätigung ([docs/features/backup-restore.md](docs/features/backup-restore.md)).
- [x] **Repository-Basisdateien:** `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `CHANGELOG.md`, Issue-/PR-Vorlagen, `.gitignore` gegen Geheimnisse und Datenbanken.
- [x] **CI:** GitHub Actions (Backend strikt mit Warnungen als Fehler und NuGet-Audit, Frontend auf Node 20.19 und 22 mit Lint, Typprüfung, Tests, Build, `npm audit`), Docker-Build-Check, CodeQL (wöchentlich), Dependabot.

### Datenbank und Persistenz
- [x] **Schema-Evolution über `SchemaUpgrader`** mit Schritt-Dateien und Versionstabelle `__LagerSchemaVersion` (keine EF-Migrationen), für SQLite und MySQL.
- [x] **Alle Zeitstempel UTC** (mit `Z` in der API); SQLite im WAL-Modus; SKU/Bestellnummer/Benutzername ohne Beachtung der Schreibweise eindeutig.
- [x] **Backup und Restore für SQLite:** konsistenter Snapshot (`VACUUM INTO`), Restore mit Prüfung, Sicherheitskopie und Sperre in Produktion (`Backup:AllowRestore`).
- [x] **Demo-Daten nur ausdrücklich** und nur in eine leere Datenbank; atomare Nummernkreise; Fremdschlüssel mit `Restrict` (Löschen verwendeter Datensätze scheitert mit 409).
- [x] **Optimistische Sperre** für Bestand und alle Belege mit Statuswechsel (409 `concurrency_conflict`).

### Stammdaten
- [x] **Lagerstruktur:** Lager, Zonen, Gänge, Regale und Lagerplätze per API und Oberfläche anlegen, ändern und löschen, **Bin-Typ** (Standard, HotPick, Reserve) und Nachschub-Schwelle ([docs/features/lager-stammdaten.md](docs/features/lager-stammdaten.md)).
- [x] **Artikel-Editor vollständig:** GTIN/EAN (mit Prüfziffer und Eindeutigkeit, auch UPC-A/EAN-13-Äquivalenz), Alternativ-SKUs, Saison-Fenster und Bundle-Komponenten (mit Zyklus- und Tiefenprüfung) pflegbar; Suche und Statusfilter in der Artikelliste; Auflösung eines Codes per `GET /api/articles/by-code/...` ([docs/features/artikel-gtin.md](docs/features/artikel-gtin.md)).
- [x] **CSV-Import und -Export** (Artikel, Bestand, Bestellungen; Export zusätzlich Bewegungen und Audit): Trockenlauf, Zeilenfehler, Übernahme in einer Transaktion, nur über die vorhandenen Dienste und das Ledger, ein Sammel-Eintrag im Audit ([docs/features/csv-import-export.md](docs/features/csv-import-export.md)).
- [x] **Etiketten:** Druck im Browser (Code 128; Einzeletikett und A4-Bogen), ZPL-Download mit `?copies`, Code-128-Barcode im Lieferschein-PDF ([docs/features/etiketten.md](docs/features/etiketten.md)).

### Bestand, Ledger und Bestellungen
- [x] **StockMovement-Ledger** (nur anhängend, signiertes Delta, Kosten-Snapshot, Grund, Vorgangsverweis, Charge/MHD) mit einheitlichem Buchungsweg (`StockBooking`) für Wareneingang, Pick, Retoure, Inventur, Nachschub, Korrektur und CSV-Import.
- [x] **Chargen lot-genau und in der Oberfläche:** Bestand je Artikel, Lagerplatz und Charge; ein MHD je Charge; FEFO beim Kommissionieren, abgelaufene Chargen werden nicht gepickt; Charge und MHD im Wareneingang und bei der Bestandsbuchung, Karte **Ablaufende Chargen**, Seite **Chargen-Trace** ([docs/features/chargen-mhd.md](docs/features/chargen-mhd.md)).
- [x] **FIFO-Bestandsbewertung** aus dem Ledger, echter **Bestandsverlauf** (API), Dead-Stock aus dem Ledger.
- [x] **Bestell-Lebenszyklus komplett:** New → Picking → Picked → Packed → Shipped, Storno aus New/Picking/Picked, Priorität und Fälligkeit, Kunde und Lieferadresse an der Bestellung, Saison-Prüfung beim Anlegen, externe Bestell-API mit `Idempotency-Key`/`externalReference`.
- [x] **Picken und Packen:** Bestandsallokation über alle Positionen (alles oder nichts), Packen bucht genau einmal, Ist-Mengen 0..Soll, Kurz-Pick, Pickliste zurücksetzen nur ohne Buchungswirkung, Wellen bis `Completed`, Routen je Lager.
- [x] **Wareneingang, Inventur, Retouren, Nachschub, Einkauf:** Wareneingang mit Positionen (Kopf und Zeilen atomar), Wareneingang aus der Einkaufsbestellung (Oberfläche: **Wareneingang anlegen**); Inventur gleicht auf den gezählten Wert gegen den aktuellen Bestand ab; Retouren mit QC (Sellable, B-Ware, Defekt, Vernichtung) und Prüfung gegen die Bestellung; Nachschub findet auch leergepickte Plätze; Bestellvorschläge berücksichtigen offene Bestellmengen.
- [x] **Versand ehrlich:** Sendungen nur für gepackte Bestellungen, Bestellung wird `Shipped`, DHL/UPS als "nicht verfügbar" statt Fake-Tracking.
- [x] **Algorithmen:** Pickroute um Wände (bis 12 Stopps exakt), Packvorschlag mit geometrischer Prüfung, ABC nach Pareto, Slotting mit Netto-Ersparnis, Einlagerungsvorschläge mit Passprüfung.

### API und Frontend
- [x] **Einheitliches Fehlerformat** (ProblemDetails mit `code` und `correlationId`) über den globalen Handler für alle Controller, Eingabegrenzen und Validatoren. Ausnahmen: 429 und `password_change_required` sind kurze `{ code, error }`-Bodies, der Login-Fehler hat kein `correlationId` im Body.
- [x] **Feature-Pakete im Frontend:** neue Seiten melden sich über `src/features/<name>/route.tsx` an (Route-Registry), ohne `App.tsx` zu ändern.
- [x] **Mehrsprachige Oberfläche (DE/EN):** react-i18next, Deutsch als Quelle und Standardsprache, Englisch vollständig übersetzt (22 Namespaces unter `frontend/lager-ui/src/locales`), Umschalter in Seitenleiste und Anmeldeseite, Wahl pro Browser (`lager.lang`), Prüfskript `npm run i18n:check`; Meldungen des Servers und die Doku bleiben deutsch ([docs/features/i18n.md](docs/features/i18n.md)).
- [x] **Swagger/OpenAPI:** Swagger UI und OpenAPI-Beschreibung in `Development` oder mit `Swagger:Enabled=true` (mit `Swagger:Enabled=false` auch in `Development` abschaltbar), jede Operation mit Zusammenfassung, Mindestrolle sowie Erfolgs- und Fehlerantworten (`problem+json`), XML-Kommentare von Controllern und DTOs, Beispielanfragen in `src/Lager.Api/Lager.Api.http`, `GET /api/version` ([docs/features/openapi.md](docs/features/openapi.md)).
- [x] **Frontend-Grundlagen:** Vitest-Tests, zentrale Fehleranzeige (Toasts, Banner), Bestätigungsdialoge und Doppelklick-Schutz, Session-Ablauf und Passwortwechsel-Dialog, Mobile-Picker mit Zählständen, Service-Worker mit Update-Hinweis, Rollen-Guards, Dark Mode, globale Suche.
- [x] **Doku:** README (deutsch und englisch), Getting Started (mit Docker und Demo-Modus), Benutzer-Handbuch, Architektur, Konfiguration, API, Datenmodell, Fehlersuche, Referenzen je Bereich unter `docs/features/`, CSV-Beispieldateien, Anleitung für Screenshots; die Doku wird durch Tests gegen den Code geprüft.

---

## 🟡 Offen (priorisiert)

### Veröffentlichung und Betrieb
- [x] **Lizenz** festgelegt: MIT (`LICENSE`); QuestPDF bleibt unter seiner Community-Lizenz (siehe [README](README.md#drittlizenzen)).
- [x] Repository-Pfad eingetragen (`The-Lady-Janna/LagerApp`: Badge, Issue-Konfiguration, Klon-Befehle, Lizenz-Inhaber).
- [ ] Im GitHub-Repository **Private Vulnerability Reporting** aktivieren (*Settings > Code security*) und optional eine Kontakt-E-Mail in `SECURITY.md` und `CODE_OF_CONDUCT.md` ergänzen.
- [ ] **Docker praktisch testen:** `docker compose up --build` einmal auf einem Rechner mit laufendem Docker durchspielen (Login, Deep-Link `/orders`, PDF-Lieferschein im Container, `down` und `up` behält die Daten, MySQL-Zusatzdatei, Caddy-Profil). Bisher geprüft sind nur `docker compose config` (Standard und MySQL-Zusatzdatei) und `dotnet publish`; das Image wurde noch nie praktisch gebaut und gestartet (der Workflow `.github/workflows/docker.yml` baut es, sobald das Repository auf GitHub liegt). Das sollte vor dem ersten Release einmal geschehen.
- [ ] **Screenshots** für README und Doku: die Anleitung liegt vor ([docs/screenshots/README.md](docs/screenshots/README.md)), die Bilder erstellt der Eigentümer im Demo-Modus.
- [ ] **Etiketten auf Papier prüfen:** ein gedruckter Bin-Etikettenbogen soll sich mit einem Handy exakt zum Lagerplatz-Code lesen lassen (Vorgehen in [docs/features/etiketten.md](docs/features/etiketten.md#manuelle-prüfung-scan-mit-dem-handy)).
- [ ] **Backup, Ausbau:** verschlüsselte Backups und eine Kopie an einen anderen Ort (heute liegen Backups im Backup-Verzeichnis und müssen von außen weggesichert werden); Backup und Restore auch für MySQL.
- [ ] **MySQL absichern:** Tests der Schema-Schritte gegen einen echten Server, dokumentierte Freigabe.
- [ ] **PWA-Icons:** Manifest und Icons gibt es nur als SVG; PNG 192/512 und `apple-touch-icon` fehlen.
- [ ] **Demo-Modus, Ausbau:** Demo-Hinweis in der Oberfläche (die Anwendung meldet den Modus dem Frontend noch nicht) und ein Demo-Reset-Endpunkt (`POST /api/admin/demo/reset`, auch außerhalb von `Development`, nur mit `Demo:Enabled`); `POST /api/admin/reseed` legt heute nur den kleinen Altbestand-Datensatz an.
- [ ] **Versionsanzeige:** die Oberfläche zeigt die Version noch nicht an (der Endpunkt `GET /api/version` liefert App-Version und Schemastand).

### Bekannte Restpunkte
- [ ] **Fehlerformat, Reste:** 429 (Rate-Limit) und 403 `password_change_required` als vollständiges ProblemDetails mit `correlationId`; auch im Login-Fehler fehlt `correlationId` im Body.
- [ ] **Bestätigungen:** `window.confirm` in `OrdersPage`, `OrderDetailPage`, `PickListsPage` und `PackPickListPage` durch den `ConfirmDialog` ersetzen; `OrderStatusPill` (Bestellungen) und `StatusPill` vereinheitlichen.
- [ ] **Bundle-Tiefe:** die Prüfung beim Speichern (`ArticleService.EnsureBundleIsValidAsync`) betrachtet nur die Komponenten nach unten, nicht die Bundles, die den Artikel enthalten.
- [ ] **Gezählte Mengen des Mobile-Pickers** werden nicht auf dem Server gespeichert: `PickItem` kennt nur die beim Packen bestätigte Menge; die Zählung wandert als Vorbelegung zur Pack-Seite.
- [ ] **Warnungen der Pickroute** (`OptimizedRoute.Warnings`, z. B. Punkt liegt in einer Wand) zeigt die Oberfläche nicht.
- [ ] **Schreibweise-unabhängige Eindeutigkeit** auch für Kunden-, Lieferanten-, Wareneingangs- und Lagerplatz-Codes auf SQLite (heute SKU, Bestellnummer, Benutzername).
- [ ] **Aufräumen:** `PickList.Assign`/Status `InProgress` wird von keinem Vorgang benutzt (nur vom Demo-Generator).

### Lager und Stammdaten
- [ ] **Multi-Warehouse durchziehen:** Filter für Bestand, Bestellungen und Wareneingang nach Lager (heute nur der Layout-Editor).
- [ ] **GTIN im Alltag:** Mobile-Picker (Scan von SKU **oder** GTIN über `by-code`), Globalsuche und ZPL-Artikeletikett (EAN-13-Strichcode statt Code 128 der SKU) nutzen die GTIN noch nicht; **Alternativ-Vorschläge** bei fehlendem Bestand (es gibt nur die Auflösung per Scan); Bundle-Artikel aus Bestandsalarmen und Bestellvorschlägen herausnehmen.
- [ ] **Lagerstruktur, Ausbau:** Verschieben von Regalen, Zonen und Lagerplätzen in andere Knoten; Löschen sperrt nicht gegen gleichzeitige Buchungen (nur die Fremdschlüssel).

### Bestand und Prozesse
- [ ] **Abbruch-Knöpfe** für Wareneingang, Inventur, Einkaufsbestellung, Retoure, Nachschub, Sendung und freigegebene Wellen; **Retoure zu einer Bestellung** in der Oberfläche; Positionen eines Wareneingangs-Entwurfs ergänzen oder entfernen.
- [ ] **Zonen-Split** für Wellen (heute eine konsolidierte Pickliste).
- [ ] **Audit erweitern:** Kunden, Lieferanten, Einkauf, Wareneingang, Inventur, Retouren, Sendungen; Benutzer am Ledger-Eintrag.
- [ ] **Teillieferung/Rückstand:** Fehlmengen beim Packen nachliefern statt die Bestellung als gepackt zu führen.
- [ ] **CSV-Import, Ausbau:** Bundle-Komponenten, Lieferanten, Kunden und Lagerstruktur importieren; `.xlsx` lesen; schnellerer Massenimport für sehr große Dateien (heute bis 20.000 Zeilen, Zeile für Zeile über die Dienste).

### Integrationen (warten auf Zugangsdaten und Konten)
- [ ] **Carrier-Adapter:** DHL, UPS, GLS, DPD (Label und Tracking). Die Schnittstelle `ICarrierAdapter` ist vorbereitet, die Adapter für DHL und UPS sind Platzhalter.
- [ ] **Shop-/ERP-Connectoren** (Shopify, WooCommerce, JTL, SAP Business One ...) über `IExternalOrderSource`: aktuell gibt es nur das Interface, **keinen Abruf**. Dazu ein Import-Dienst.
- [ ] **API-Keys und Webhooks** für Maschinen-Zugriffe (heute ein normaler Benutzer mit Rolle Manager) und ausgehende Ereignisse.
- [ ] **EDI** (EDIFACT/IDoc), nur bei B2B-Großhandel.

### Hardware
- [ ] **Netzwerkdruck zum Zebra** (TCP/9100) direkt aus der Anwendung (heute: ZPL-Datei herunterladen und an den Drucker senden).
- [ ] **Waagen-Treiber** für ein konkretes Modell (heute `NullScaleReader`); **RFID**.

### Oberfläche und Qualität
- [ ] **i18n, Ausbau:** Meldungen des Servers (heute deutsch, die Oberfläche übersetzt nur häufige Fehlercodes), StatusPills (zeigen teils den englischen Serverwert), konkretere englische Fehlertexte statt der allgemeinen Sätze für `validation_failed` und `not_found`, `<Trans>` mit Nutzerdaten in `values` (wird als HTML geparst und kann die Seite in den ErrorBoundary fallen lassen), englische Doku, weitere Sprachen; siehe [docs/features/i18n.md](docs/features/i18n.md#grenzen).
- [ ] **Paging, Filter, Sortierung** für Listen-Endpunkte und virtuelles Scrollen bei sehr vielen Zeilen (heute liefern Listen alles; nur die Artikelliste hat `?search=`).
- [ ] **Browser-Benachrichtigungen** (opt-in), **Offline-Sync** für den Mobile-Picker, **Druck-Layouts** als PDF (Zähllisten, Picklisten).

---

## ⏸ Bewusst verschoben

Diese Punkte sind **keine Fehler**, sondern bewusst nicht Teil von 0.1.0: Sie greifen tief in Datenmodell und Abläufe ein und sollen erst gebaut werden, wenn ein echter Bedarf besteht (jeweils ein eigener Ausbauschritt, nicht nebenher).

| Thema | Was und warum verschoben |
|---|---|
| **Verpackungseinheiten (VE)** | Stück/Karton/Palette mit Umrechnungsfaktoren. Greift in Kommissionierung, Bestandsanzeige, Bestellungen, Einkauf und Etiketten ein. |
| **Artikel-Varianten** | Stammartikel mit Varianten (Größe/Farbe), Bestand je Variante. Ändert Artikel, Bestand, Bestellung und Import. |
| **Carrier-Integrationen** | DHL, UPS, GLS, DPD: brauchen Konten und Zugangsdaten; bis dahin manuelle Tracking-Nummern. |
| **Reservierung und ATP** (verfügbar zum Versprechen) | Heute keine Reservierung, Bestand wird erst beim Packen gebucht. Ein Reservierungs-Modell verändert den gesamten Bestandsfluss. |
| **Zyklus-Inventur** | ABC-gesteuerte, rollierende Zählung und Fund in leerem Lagerplatz in der Inventur; sinnvoll erst, wenn der Prozess es verlangt. |
| **API-Keys und Webhooks** | Maschinen-Zugriffe mit eigenem Schlüssel und ausgehende Ereignisse statt eines Benutzers mit Rolle Manager. |
| **Umlagerung und Kapazität** | Bin-zu-Bin-Umlagerung (Ledger-Grund `BinMove` ist vorgesehen, aber nicht genutzt), Auslastungsansicht und Kapazitätsplanung der Lagerplätze. |

---

## ⚫ Branchen-Spezialisierung (nur on demand)

Nur bauen, wenn die Branche tatsächlich bedient wird, sonst ist es toter Code.

| Branche | Was |
|---|---|
| **Pharma / GMP** | Signierter Audit-Trail, lückenlose Charge-Rückverfolgung, automatische MHD-Sperren, elektronische Signaturen |
| **Food / HACCP** | Temperatur-Zonen mit Logger-Anbindung, strengerer MHD-Workflow, Reinigungs-Logs |
| **Gefahrgut (ADR)** | ADR-Klasse pro Artikel, Lagerverbote, Mengenlimits pro Brandabschnitt |
| **Tiefkühl** | Temperatur-Constraint pro Zone, Picker-Limits im TK-Bereich, Tau-Workflow |

---

## Pragmatische Reihenfolge

1. **Veröffentlichung:** Kontakte und Repository-Pfad eintragen, Docker einmal praktisch durchspielen, Screenshots erstellen.
2. **Bekannte Restpunkte** beheben (kleine Aufgaben, sofort spürbar).
3. **Oberfläche ergänzen**, wo das Backend schon fertig ist (Abbruch-Knöpfe, GTIN im Mobile-Picker, Retoure zu Bestellung, Paging).
4. **Reservierung/Teillieferung** und **Zyklus-Inventur**, sobald der Prozess sie verlangt.
5. **VE und Varianten** nur, wenn das Sortiment es fordert (eigene Ausbauschritte, nicht nebenher).
6. **Integrationen** (Carrier, Shop/ERP, API-Keys), sobald Zugangsdaten und Konten existieren; **Hardware**, sobald Drucker und Waage im Lager stehen.
7. **Branchen-Spezialisierung** nur bei konkreter Anforderung.

**Faustregel:** Nie zwei große Themen gleichzeitig. Backend abschließen, Frontend nachziehen, dann das nächste.
