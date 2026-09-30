using System.Text.Json;
using Lager.Tests.WP04;

namespace Lager.Tests.WP11;

/// <summary>
/// Reine Frontend-Logik aus WP11 (src/lib/format.ts, src/lib/pickCounts.ts) - direkt mit Node ausgeführt, wie die Tests
/// von WP04. Ergänzt die Vitest-Tests in frontend/lager-ui/src/tests/WP11 um eine zweite, unabhängige Prüfung durch
/// <c>dotnet test</c>. Ohne passendes Node (>= 22.18) werden die Tests als übersprungen gemeldet.
/// </summary>
public class FrontendLibLogicTests
{
    [NodeFact]
    public void Zeitpunkt_ohne_Zeitzone_wird_als_UTC_gelesen_und_ergibt_dieselbe_Ortszeit_wie_mit_Z()
    {
        using var doc = FrontendFixture.RunNode($$"""
            import { parseServerDate, formatDateTime, formatDate, formatDateOnly } from '{{FrontendFixture.ModuleUrl("src/lib/format.ts")}}'
            // Nicht-ASCII-Zeichen (Gedankenstrich) als Unicode-Escape ausgeben: die Konsolen-Codepage der Testausgabe ist nicht UTF-8.
            const out = (o) => console.log(JSON.stringify(o).split('').map((c) => (c.charCodeAt(0) > 126 ? '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0') : c)).join(''))
            out({
              ohneZoneIso: parseServerDate('2025-01-01T10:00:00')?.toISOString(),
              mitZIso: parseServerDate('2025-01-01T10:00:00Z')?.toISOString(),
              versatz: parseServerDate('2025-01-01T12:00:00+02:00')?.toISOString(),
              siebenStellen: parseServerDate('2026-05-25T18:27:56.6880910')?.toISOString(),
              ungueltig: parseServerDate('kein Datum'),
              ohneZoneAnzeige: formatDateTime('2025-01-01T10:00:00'),
              mitZAnzeige: formatDateTime('2025-01-01T10:00:00Z'),
              berlinWinter: formatDateTime('2025-01-01T10:00:00', { timeZone: 'Europe/Berlin' }),
              berlinSommer: formatDateTime('2025-07-01T10:00:00Z', { timeZone: 'Europe/Berlin' }),
              tagGrenze: formatDate('2025-01-01T23:30:00', { timeZone: 'Europe/Berlin' }),
              kalendertag: formatDateOnly('2026-05-25T00:00:00'),
              fehlend: formatDateTime(null),
            })
            """);
        var r = doc.RootElement;

        Assert.Equal("2025-01-01T10:00:00.000Z", r.GetProperty("ohneZoneIso").GetString());
        Assert.Equal(r.GetProperty("mitZIso").GetString(), r.GetProperty("ohneZoneIso").GetString());
        Assert.Equal("2025-01-01T10:00:00.000Z", r.GetProperty("versatz").GetString());
        Assert.Equal("2026-05-25T18:27:56.688Z", r.GetProperty("siebenStellen").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("ungueltig").ValueKind);

        // Akzeptanzkriterium: beide Schreibweisen ergeben dieselbe Ortszeit.
        Assert.Equal(r.GetProperty("mitZAnzeige").GetString(), r.GetProperty("ohneZoneAnzeige").GetString());
        Assert.Equal("01.01.2025, 11:00", r.GetProperty("berlinWinter").GetString());
        Assert.Equal("01.07.2025, 12:00", r.GetProperty("berlinSommer").GetString());
        Assert.Equal("02.01.2025", r.GetProperty("tagGrenze").GetString());
        Assert.Equal("25.05.2026", r.GetProperty("kalendertag").GetString());
        Assert.Equal("—", r.GetProperty("fehlend").GetString());
    }

    [NodeFact]
    public void Mobile_Picker_zaehlt_ab_0_und_reicht_nur_gueltige_Zaehlstaende_an_die_Pack_Seite()
    {
        using var doc = FrontendFixture.RunNode($$"""
            import { actualQuantity, nextScanCount, findDeviations, pickCountsFor, readPickedQuantities } from '{{FrontendFixture.ModuleUrl("src/lib/pickCounts.ts")}}'
            const items = [{ id: 'i1', quantity: 5 }, { id: 'i2', quantity: 2 }]
            console.log(JSON.stringify({
              ohneZaehlung: actualQuantity({}, items[0]),
              gezaehlt: actualQuantity({ i1: 3 }, items[0]),
              nullGezaehlt: actualQuantity({ i1: 0 }, items[0]),
              ersterScan: nextScanCount(undefined),
              scanNachNull: nextScanCount(0),
              scanNachVier: nextScanCount(4),
              abweichungen: findDeviations(items, { i1: 3, i2: 2 }).map(d => [d.item.id, d.actual]),
              nurEigene: pickCountsFor(items, { i1: 3, fremd: 9 }),
              gelesen: readPickedQuantities({ pickedQuantities: { i1: 3, i2: -1, i3: 4, i4: 1.5, i5: '2' } }, [{ id: 'i1' }, { id: 'i2' }, { id: 'i4' }, { id: 'i5' }]),
              keinState: readPickedQuantities(null, items),
            }))
            """);
        var r = doc.RootElement;

        Assert.Equal(5, r.GetProperty("ohneZaehlung").GetInt32());   // nicht angefasst = wie geplant bestätigt
        Assert.Equal(3, r.GetProperty("gezaehlt").GetInt32());
        Assert.Equal(0, r.GetProperty("nullGezaehlt").GetInt32());   // 0 ist eine echte Zählung
        Assert.Equal(1, r.GetProperty("ersterScan").GetInt32());     // Scan zählt ab 0, nicht ab Soll
        Assert.Equal(1, r.GetProperty("scanNachNull").GetInt32());
        Assert.Equal(5, r.GetProperty("scanNachVier").GetInt32());
        Assert.Equal("[[\"i1\",3]]", r.GetProperty("abweichungen").GetRawText());
        Assert.Equal("{\"i1\":3}", r.GetProperty("nurEigene").GetRawText());
        Assert.Equal("{\"i1\":3}", r.GetProperty("gelesen").GetRawText()); // negativ, Kommazahl, String und unbekannte ID fliegen raus
        Assert.Equal("{}", r.GetProperty("keinState").GetRawText());
    }
}
