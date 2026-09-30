## Beschreibung

<!-- Was ändert dieser Pull Request und warum? -->

## Art der Änderung

- [ ] Fehlerbehebung
- [ ] Neues Feature
- [ ] Refactoring ohne Verhaltensänderung
- [ ] Dokumentation
- [ ] Sonstiges (Build, Abhängigkeiten, Repo-Dateien)

## Verknüpfte Issues

<!-- Zum Beispiel: Closes #123 -->

## Checkliste

- [ ] `dotnet build Lager.sln -p:LagerStrict=true` läuft ohne Fehler und ohne Warnungen durch
- [ ] `dotnet test tests/Lager.Tests` ist grün; für Fehlerbehebungen und neues Verhalten gibt es Tests
- [ ] Frontend (falls betroffen): `npm run lint`, `npm run typecheck`, `npm test` und `npm run build` in `frontend/lager-ui` laufen ohne Fehler durch
- [ ] Bei neuen Entitäten, Tabellen oder Spalten: Schritt im `SchemaUpgrader` ergänzt (idempotent, SQLite und MySQL), siehe `CONTRIBUTING.md`
- [ ] Clean-Architecture-Regeln eingehalten (Abhängigkeiten nur nach innen)
- [ ] Neue Kommentare und Meldungen des Servers sind auf Deutsch; neue UI-Texte stehen in den Sprachdateien `de` und `en` (`npm run i18n:check` ist sauber)
- [ ] Doku angepasst (README, `docs/`, falls das Verhalten sich ändert); die Doku-Tests (`tests/Lager.Tests/WP18`) sind Teil von `dotnet test`
- [ ] Bei Änderungen an `Dockerfile` oder `docker-compose*.yml`: `docker compose config` läuft durch
- [ ] Eintrag unter `[Unreleased]` im `CHANGELOG.md`
- [ ] Keine Zugangsdaten, Schlüssel, Datenbank- oder Logdateien im Commit

## Hinweise für das Review

<!-- Worauf soll besonders geachtet werden? Screenshots bei UI-Änderungen. -->
