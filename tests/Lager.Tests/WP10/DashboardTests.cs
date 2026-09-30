using Lager.Application.Reports;
using Lager.Domain.PickLists;

namespace Lager.Tests.WP10;

/// <summary>Dashboard-Kennzahlen: Picks/h über die tatsächliche Zeitspanne, Tagesreihen einschließlich heute, Ist-Mengen.</summary>
public class DashboardTests
{
    private static readonly DateTime T0 = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Picks_per_hour_divides_by_the_span_between_first_and_last_pick()
    {
        // 4 Picks zwischen 08:00 und 10:00 = 2 h -> 2 Picks/h (vorher: Picklisten / (Tage * 24 h)).
        var times = new[] { T0, T0.AddMinutes(30), T0.AddMinutes(90), T0.AddHours(2) };

        var perHour = DashboardMath.PicksPerHour(times, T0.AddDays(-7), T0.AddDays(1));

        Assert.Equal(2.0, perHour, 6);
    }

    [Fact]
    public void Picks_per_hour_uses_at_least_one_hour_and_at_most_the_window()
    {
        // Ein einzelner Pick-Schub: Mindestspanne 1 h statt Division durch 0.
        Assert.Equal(3.0, DashboardMath.PicksPerHour(new[] { T0, T0, T0 }, T0.AddDays(-1), T0.AddDays(1)), 6);

        // Spanne größer als das Fenster (Zeitstempel außerhalb) wird auf das Fenster gekappt: 2 Picks / 24 h.
        var wide = new[] { T0.AddDays(-10), T0 };
        Assert.Equal(2.0 / 24.0, DashboardMath.PicksPerHour(wide, T0.AddDays(-1), T0), 6);

        Assert.Equal(0.0, DashboardMath.PicksPerHour(Array.Empty<DateTime>(), T0.AddDays(-1), T0));
    }

    [Fact]
    public void Day_buckets_run_from_the_start_day_up_to_and_including_today()
    {
        var to = new DateTime(2026, 3, 10, 15, 30, 0, DateTimeKind.Utc);
        var from = to.AddDays(-7);
        var todayEvents = new[] { to.AddHours(-1), to.AddHours(-2) };
        var startDayEvent = new[] { from.AddHours(1) };

        var series = DashboardMath.BucketByDay(todayEvents.Concat(startDayEvent), from, to);

        Assert.Equal(8, series.Count); // 7 Tage zurück + heute
        Assert.Equal(DateOnly.FromDateTime(from), series[0].Date);
        Assert.Equal(1, series[0].Count);
        Assert.Equal(DateOnly.FromDateTime(to), series[^1].Date);
        Assert.Equal(2, series[^1].Count); // Ereignisse von heute fehlten früher
        Assert.Equal(3, series.Sum(p => p.Count));
        Assert.Equal(series.Select(p => p.Date).Distinct().Count(), series.Count);
    }

    [Fact]
    public async Task Dashboard_counts_today_and_uses_actual_picked_quantities_for_top_articles()
    {
        var hot = Guid.NewGuid();
        var cold = Guid.NewGuid();
        var bin = Guid.NewGuid();
        var gateway = new FakeGateway();
        gateway.Names[hot] = ("SKU-HOT", "Heiß");
        gateway.Names[cold] = ("SKU-COLD", "Kalt");
        gateway.PickLists.Add(new PickListSnapshot(Guid.NewGuid(), PickListStatus.Completed, DateTime.UtcNow, 2000));
        // Der Gateway liefert nur echte Picks (Ist-Menge > 0); das Dashboard summiert genau diese Mengen.
        gateway.Picked.Add(Wp10.Pick(hot, bin, quantity: 2, at: DateTime.UtcNow.AddHours(-3)));
        gateway.Picked.Add(Wp10.Pick(hot, bin, quantity: 1, at: DateTime.UtcNow.AddHours(-1)));
        gateway.Picked.Add(Wp10.Pick(cold, bin, quantity: 9, at: DateTime.UtcNow.AddHours(-2)));

        var dashboard = await new ReportService(gateway).DashboardAsync(7);

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), dashboard.PickListsPerDay[^1].Date);
        Assert.Equal(1, dashboard.PickListsPerDay[^1].Count);
        Assert.Equal(new[] { "SKU-HOT", "SKU-COLD" }, dashboard.TopArticles.Select(t => t.Sku).ToArray());
        Assert.Equal(3, dashboard.TopArticles[0].TotalQuantity);
        Assert.Equal(2, dashboard.TopArticles[0].PickCount);

        var picksPerHour = dashboard.Headline.Single(k => k.Label == "Picks pro Stunde");
        Assert.Equal((3 / 2.0).ToString("0.00"), picksPerHour.Value); // 3 Picks in 2 h zwischen erstem und letztem Pick
    }
}
