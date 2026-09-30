# Sicherheitsrichtlinie

Lager ist ein Self-hosted Warehouse-Management-System mit Anmeldung (JWT, Rollen), Benutzerverwaltung und Backup/Restore. Sicherheitsprobleme nehmen wir ernst. Bitte melde sie **vertraulich** und nicht als öffentliches Issue.

## Unterstützte Versionen

| Version | Unterstützt |
|---|---|
| `main` bzw. neuester 0.x-Stand | ja |
| ältere 0.x-Stände | nein |

Das Projekt befindet sich vor 1.0. Sicherheitskorrekturen gibt es nur für den jeweils neuesten Minor-Stand; ein Backport auf ältere Stände findet nicht statt. Bislang gibt es keine veröffentlichten Releases.

## Schwachstelle melden

1. **Bevorzugter Weg:** Auf der GitHub-Seite des Repositories unter **Security** den Button **Report a vulnerability** wählen (Private Vulnerability Reporting). Der Bericht ist dann nur für die Maintainer sichtbar.
   Hinweis für Repository-Betreiber: Diese Funktion muss nach dem Anlegen des Repositories unter *Settings > Code security* aktiviert werden.
2. **Falls Weg 1 nicht möglich ist:** eine kurze Nachricht ohne technische Details über das GitHub-Profil von [@The-Lady-Janna](https://github.com/The-Lady-Janna) mit der Bitte um einen vertraulichen Kontaktweg. Schwachstellendetails bitte erst nach der Rückmeldung senden.

Bitte lege keine öffentlichen Issues, Pull Requests oder Diskussionen zu ungepatchten Schwachstellen an.

**Hilfreich für die Bewertung:**

- betroffene Version bzw. Commit, Datenbank-Provider (SQLite/MySQL) und Umgebung (Development/Production)
- Beschreibung des Problems und der Auswirkung
- Schritte zur Reproduktion oder ein kleiner Proof of Concept
- gegebenenfalls ein Lösungsvorschlag

Bitte keine echten Zugangsdaten, Tokens oder personenbezogenen Daten in den Bericht schreiben.

## Was du erwarten kannst

Das Projekt wird in der Freizeit gepflegt. Wir bemühen uns um folgende Ziele, garantieren sie aber nicht:

| Schritt | Ziel |
|---|---|
| Eingangsbestätigung | innerhalb von 7 Tagen |
| Einschätzung und Rückmeldung | innerhalb von 14 Tagen |
| Korrektur oder Gegenmaßnahme | innerhalb von 90 Tagen |

Wir arbeiten mit **koordinierter Offenlegung**: Details werden erst veröffentlicht, wenn eine Korrektur verfügbar ist oder die 90 Tage abgelaufen sind. Wer möchte, wird im Changelog bzw. im Security Advisory namentlich erwähnt.

## Geltungsbereich

**Im Scope:** der Quelltext in diesem Repository (Backend `src/`, Frontend `frontend/lager-ui`), zum Beispiel Umgehung der Anmeldung oder der Rollenprüfung, Rechteausweitung, Injection, unsichere Behandlung von Tokens oder Passwörtern, Zugriff auf Dateien außerhalb des vorgesehenen Bereichs.

**Kein Scope:**

- Unveränderte Standard-Zugangsdaten, der Standard-Signing-Key oder andere Beispielwerte aus `appsettings.json` in einer produktiv betriebenen Instanz. Das ist ein **Konfigurationsfehler des Betreibers**, keine Schwachstelle der Software (siehe nächster Abschnitt).
- Demo-Daten und Development-Einstellungen in lokalen Entwicklungs- oder Testinstanzen.
- Angriffe, die bereits vollen Zugriff auf den Server, das Dateisystem oder ein Admin-Konto voraussetzen.
- Schwachstellen in Drittabhängigkeiten ohne konkreten Bezug zu Lager (bitte direkt beim jeweiligen Projekt melden).

## Betriebs-Sicherheit (für Betreiber)

Wer Lager produktiv betreibt, ist für eine sichere Konfiguration verantwortlich. Mindestens:

- **JWT-Signing-Key:** `Jwt:SigningKey` (Umgebungsvariable `Jwt__SigningKey`) auf einen eigenen, zufälligen Wert mit mindestens 32 Zeichen setzen. Niemals einen Beispiel- oder Standardwert (auch nicht aus der Dokumentation oder aus `appsettings.json` eines älteren Stands) produktiv verwenden. Den Key nicht ins Repository einchecken; er gehört in Umgebungsvariablen oder einen Secret-Store.
- **Einmalpasswort für den Bootstrap-Admin:** `Auth:BootstrapAdminPassword` (`Auth__BootstrapAdminPassword`) wird nur beim allerersten Start verwendet, wenn noch kein Benutzer existiert. Ein eigenes, sicheres Einmalpasswort setzen (keinen Beispiel- oder Standardwert), sich damit sofort anmelden und es ändern (die App erzwingt den Wechsel beim ersten Login). Das Einmalpasswort nicht dauerhaft in Dateien, Tickets oder Chats aufbewahren.
- **HTTPS:** `Security:RequireHttps` auf `true` setzen und TLS am Server bzw. Reverse-Proxy terminieren. Anmeldedaten und Tokens niemals über unverschlüsseltes HTTP übertragen.
- **CORS:** `Cors:AllowedOrigins` (`Cors__AllowedOrigins__0`, `...__1`) auf die tatsächliche Frontend-Domain einschränken.
- **Demo-Daten aus:** `Database:Seed` auf `false` setzen.
- **Backups:** Backup-Dateien enthalten alle Daten inklusive Passwort-Hashes. Speicherort absichern und nicht ins Repository legen.
- **Geheimnisse und Datenbanken nicht einchecken:** Produktiv-Konfiguration (`appsettings.Production.json`), `.env`-Dateien, Schlüssel/Zertifikate und `*.db`/`*.sqlite`-Dateien sind in der `.gitignore` ausgeschlossen. Nach `git add` trotzdem einmal `git status` prüfen.

Die schrittweise Anleitung steht in [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md#7-production-setup-kurz), die Konfigurationsschlüssel sind in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#konfiguration) beschrieben.

## English summary

Please report security vulnerabilities privately through GitHub's **Report a vulnerability** button (Private Vulnerability Reporting) on the repository's Security tab, and do not open public issues for them. If that is not possible, send a short message without technical details via the GitHub profile of [@The-Lady-Janna](https://github.com/The-Lady-Janna) and ask for a confidential channel. We aim to acknowledge reports within 7 days and to provide a fix or mitigation within 90 days, followed by coordinated disclosure. Only the latest 0.x state is supported. Running an instance with unchanged default credentials or the default signing key in production is an operator misconfiguration, not a vulnerability in the software; see the section "Betriebs-Sicherheit" above for the hardening checklist.
