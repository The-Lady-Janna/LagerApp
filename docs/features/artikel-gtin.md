# Artikel-Stammdaten: GTIN/EAN, Scan-Auflösung, Bundle, Alternativ-SKUs, Saison

Ausführliche Referenz zu Artikel-Stammdaten, GTIN und Scan-Auflösung. Die Kurzfassung für die Bedienung steht in [USAGE.md](../USAGE.md#gtin-und-scan-auflösung).

## Was der Artikel-Editor jetzt pflegt

| Bereich | Feld | Regel |
|---|---|---|
| Kennungen | **GTIN / EAN** | nur Ziffern, 8 / 12 / 13 / 14 Stellen, Prüfziffer nach GS1, eindeutig über alle Artikel; leer = keine GTIN |
| Kennungen | **Alternativ-SKUs** (Chips) | weitere Kennungen, unter denen ein Artikel gefunden wird: Die Artikelsuche und der Scan über `by-code` berücksichtigen sie als **Rückfall**. Einen Ersatzartikel-Vorschlag bei fehlendem Bestand gibt es **nicht**. Der Editor prüft, dass es einen Artikel mit dieser SKU gibt, dass es nicht der Artikel selbst ist und dass sie nicht doppelt ist |
| Bundle / Kit | **Komponenten** (Artikelsuche + Menge) | ein Artikel mit mindestens einer Komponente ist ein Bundle (ohne eigenen Bestand); keine Selbstreferenz, keine Zyklen, keine doppelte Komponente |
| Saison | **Gültig ab / Gültig bis** | Ende >= Beginn; der **letzte Tag gilt noch vollständig**; leer = ganzjährig |

Weitere Bedienung: Der GTIN-Hinweis erscheint schon beim Tippen (gültig mit Art der GTIN, oder der Grund samt erwarteter Prüfziffer);
Konflikte des Servers (doppelte SKU/GTIN) stehen am Feld und im Fehlerbanner; wer mit ungespeicherten Änderungen "Abbrechen" wählt,
einen Link der Seitenleiste anklickt oder den Tab schließt, wird gewarnt.

Die **Artikelliste** hat eine GTIN-Spalte, eine Suche über Name, SKU, Alternativ-SKU und GTIN (Groß-/Kleinschreibung und Leerraum egal),
einen Statusfilter (alle / aktuell bestellbar / außerhalb der Saison) und kennzeichnet Bundles (Komponenten im Tooltip) sowie den
Saison-Status.

## GTIN

- `Article.Gtin` (`string?`), Regeln in `src/Lager.Domain/Articles/Gtin.cs` (`Normalize`, `GetError`, `IsValid`, `EquivalentForms`);
  das Frontend spiegelt sie in `frontend/lager-ui/src/pages/articleEditor/gtin.ts` (dieselben Testvektoren auf beiden Seiten).
- **Normalisieren**: aller Leerraum wird entfernt ("4 006381333931" = "4006381333931"). Sonst wird nichts erraten (Bindestriche fallen durch).
- **Gleichwertige Schreibweisen**: Ein UPC-A (`036000291452`) ist dieselbe GTIN wie der EAN-13 `0036000291452` und der GTIN-14
  `00036000291452`. Die Eindeutigkeitsprüfung und die Scan-Auflösung behandeln sie als eine GTIN. (Der Unique-Index der Datenbank
  fängt nur identische Texte ab; die gleichwertigen Formen prüft der Service vorher.)
- **Fehler**: ungültige GTIN = 400 mit Feld `Gtin` (`errors.Gtin`); doppelte GTIN = 409 mit Code `duplicate_gtin`.
- **Änderung per PUT**: `gtin` fehlend/`null` = **unverändert** (ein Client, der das Feld nicht kennt, löscht keine GTIN), leerer Text = GTIN
  entfernen, sonst die neue GTIN. Das ist bewusst anders als bei `alternativeSkus`/`validFrom`/`validUntil` (dort setzt ein fehlender
  Wert zurück); der Editor sendet alle Felder immer mit.
- **Schema**: Schritt `2000_AddArticleGtin` (`SchemaSteps/AddArticleGtinStep.cs`) ergänzt bestehenden Datenbanken die Spalte
  `Articles.Gtin` (SQLite `TEXT NULL`, MySQL `VARCHAR(14) NULL`) und den eindeutigen Index `IX_Articles_Gtin`
  (SQLite: partiell `WHERE Gtin IS NOT NULL`, MySQL: Unique-Index; NULLs kollidieren nie). Neue Datenbanken bekommen beides aus dem Modell.

## Scan-Auflösung

`GET /api/articles/by-code/{code}` (jeder Angemeldete darf lesen):

1. **Identität**: SKU (exakt, Groß-/Kleinschreibung und Leerraum am Rand egal) und GTIN (auch in gleichwertiger Länge).
2. **Nur wenn dort nichts passt**: Alternativ-SKU. So verdrängt die Alternativ-SKU eines Artikels nie den Artikel, dessen SKU sie ist.

| Antwort | Bedeutung |
|---|---|
| 200 | der Artikel (`ArticleDto`) |
| 404 | unbekannter Code (`code: not_found`, Meldung nennt den Code) |
| 409 | mehrdeutig (`code: ambiguous_code`, Feld `candidates` = Liste der Artikel), z. B. dieselbe Alternativ-SKU an zwei Artikeln |

Sonderzeichen im Code URL-kodieren (`encodeURIComponent`); ein kodierter Schrägstrich (`%2F`) wird aufgelöst. Frontend: Hook
`useArticleByCode(code)` bzw. `fetchArticleByCode(code)`; `ambiguousArticleCandidates(error)` liefert die Kandidaten eines 409.

`GET /api/articles?search=` filtert die Liste nach Name, SKU, Alternativ-SKU und GTIN (Teilstring; weiter ohne Paging).

Rollenmatrix: Die Zeile `Get("api/articles/by-code/{**code}")` (Stufe Authenticated) steht in `tests/Lager.Tests/WP02/EndpointMatrix.cs`; die Matrix verlangt für jede Action jeder Controller-Klasse eine Zeile.

## Bundle-Komponenten beim Speichern

Der Server prüft (`ArticleService`): jede Komponente höchstens einmal (400 `duplicate_bundle_component`), sie muss existieren
(400 `unknown_bundle_component`), keine Selbstreferenz und keine Zyklen über beliebig viele Ebenen (409 `bundle_cycle`, die Meldung
nennt den Weg "A → B → A"), Verschachtelung höchstens so tief, wie Kommissionieren, Packen und Retouren auflösen können (5 Ebenen,
409 `bundle_too_deep`). Verschachtelte Bundles ohne Zyklus bleiben erlaubt. Bei einem Fehler bleibt der Artikel unverändert.

`bundleComponents` fehlend/`null` im PUT = **unverändert**, leere Liste = Bundle auflösen. Der Editor sendet die Liste, sobald der
Artikel Komponenten hat oder sie bearbeitet wurden; die Komponenten-SKU steht in `bundleComponents[].componentSku` der Antwort.

## Saison-Fenster

`ValidFrom`/`ValidUntil` sind Kalendertage (UTC). `Article.IsCurrentlyActive`: der Beginn gilt ab 00:00 UTC des ersten Tages, das Ende
**inklusive** (`ValidUntil = 30.09.` = am 30.09. bis 23:59 UTC noch bestellbar, ab 01.10. nicht mehr). Ein Ein-Tages-Fenster
(Beginn = Ende) ist gültig, ein Ende vor dem Beginn ist ein 400. Der Server liefert den Wert als `isCurrentlyActive` im `ArticleDto`;
Bestellungen mit Artikeln außerhalb der Saison lehnt `OrderService` mit `article_not_orderable` ab (409).

## Offene Punkte

- Mobile-Picker: beim Artikel-Scan SKU **oder** GTIN akzeptieren (`useArticleByCode`), Globalsuche um GTIN erweitern.
- ZPL-Artikeletikett: bei gesetzter GTIN `^BE` (EAN-13) statt Code128 der SKU.
- Bundle-Artikel aus Bestandsalarmen und Bestellvorschlägen herausnehmen; Alternativ-Vorschlag bei Bestand 0 in der Bestell-UI; Saison-Filter im Bestell-Dropdown.
- Die Tiefenprüfung der Bundles beim Speichern betrachtet nur die Komponenten **nach unten**, nicht die Bundles, die den Artikel selbst enthalten; ein nachträglich tiefer verschachteltes Bundle kann ein darüberliegendes über die Grenze von fünf Ebenen schieben.
- Schreibweise-unabhängige Eindeutigkeit gibt es für SKU, Bestellnummer und Benutzername; Kunden-, Lieferanten- und Wareneingangs-Codes sind auf SQLite noch schreibweise-abhängig eindeutig.
