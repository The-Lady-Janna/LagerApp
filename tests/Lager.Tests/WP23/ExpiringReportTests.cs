using System.Net;
using Lager.Contracts.Reports;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP23;

/// <summary>
/// GET /api/reports/expiring (MHD-Warnliste): abgelaufene und bald ablaufende Chargen mit Status und Tagen bis zum Ablauf, alles in
/// UTC-Kalendertagen, sortiert nach MHD. Die Grenzfälle (heute, Ablauftag = Frist, ein Tag darüber, Menge 0, kein MHD) sind die
/// Stellen, an denen eine Warnliste typischerweise um einen Tag danebenliegt.
/// </summary>
public class ExpiringReportTests : IClassFixture<Wp23Fixture>
{
    private readonly Wp23Fixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public ExpiringReportTests(Wp23Fixture fixture) => _fx = fixture;

    /// <summary>Eine Bestandszeile mit MHD (relativ zu heute) in einem eigenen Artikel; liefert die Artikel-Id.</summary>
    private async Task<Guid> StockAsync(int? expiryInDays, int quantity = 10, string? lot = "LOT", WorldBuilder.Bin? bin = null)
    {
        var article = await W.AddArticleAsync();
        await W.AddLegacyStockAsync(article.Id, bin ?? Site.PickA, quantity, lot, expiryInDays is int d ? WorldBuilder.InDays(d) : null);
        return article.Id;
    }

    private static async Task<List<ExpiringStockDto>> ExpiringAsync(HttpClient client, string query = "") =>
        await (await client.GetAsync("/api/reports/expiring" + query)).ExpectAsync<List<ExpiringStockDto>>();

    [Fact]
    public async Task Expired_and_due_within_30_days_are_listed_later_ones_are_not_sorted_by_expiry()
    {
        var admin = await W.AdminAsync();
        var expired = await StockAsync(-5);
        var yesterday = await StockAsync(-1);
        var today = await StockAsync(0);
        var seven = await StockAsync(7);
        var eight = await StockAsync(8);
        var thirty = await StockAsync(30);
        var thirtyOne = await StockAsync(31);
        var noExpiry = await StockAsync(null);
        var emptyRow = await StockAsync(3, quantity: 0);
        var mine = new[] { expired, yesterday, today, seven, eight, thirty, thirtyOne, noExpiry, emptyRow };

        var rows = (await ExpiringAsync(admin, "?days=30")).Where(r => mine.Contains(r.ArticleId)).ToList();

        // in der Frist: abgelaufen bis einschließlich Tag 30; nicht dabei: Tag 31, ohne MHD, Menge 0
        Assert.Equal(new[] { expired, yesterday, today, seven, eight, thirty }, rows.Select(r => r.ArticleId));
        // Tage und Status je Zeile (heute = 0 Tage und noch verwendbar -> kritisch, gestern = abgelaufen, Tag 8 = erst "bald")
        Assert.Equal(new[] { -5, -1, 0, 7, 8, 30 }, rows.Select(r => r.DaysUntilExpiry));
        Assert.Equal(new[] { "Expired", "Expired", "Critical", "Critical", "Soon", "Soon" }, rows.Select(r => r.Status));
        // Sortierung nach MHD (älteste zuerst)
        Assert.Equal(rows.OrderBy(r => r.ExpiryDate).Select(r => r.ArticleId), rows.Select(r => r.ArticleId));
        // Inhalt der Zeile: Artikel, Charge, Lagerplatz, Menge, MHD
        var row = Assert.Single(rows, r => r.ArticleId == today);
        Assert.Equal((Site.PickA.Code, "LOT", 10, WorldBuilder.InDays(0)), (row.BinCode, row.LotNumber, row.Quantity, row.ExpiryDate.Date));
        Assert.Equal(Site.PickA.Id, row.BinId);
        Assert.False(string.IsNullOrEmpty(row.ArticleSku));
        Assert.False(string.IsNullOrEmpty(row.ArticleName));
    }

    [Fact]
    public async Task The_days_parameter_moves_the_window_and_values_outside_the_api_limits_are_rejected()
    {
        var admin = await W.AdminAsync();
        var expired = await StockAsync(-2);
        var today = await StockAsync(0);
        var tomorrow = await StockAsync(1);
        var in90 = await StockAsync(90);
        var in400 = await StockAsync(400);
        var mine = new[] { expired, today, tomorrow, in90, in400 };

        async Task<Guid[]> IdsAsync(string query) =>
            (await ExpiringAsync(admin, query)).Where(r => mine.Contains(r.ArticleId)).Select(r => r.ArticleId).ToArray();

        Assert.Equal(new[] { expired, today, tomorrow }, await IdsAsync("?days=1"));
        Assert.Equal(new[] { expired, today, tomorrow }, await IdsAsync("?days=7"));
        Assert.Equal(new[] { expired, today, tomorrow, in90 }, await IdsAsync("?days=90"));
        Assert.Equal(new[] { expired, today, tomorrow, in90, in400 }, await IdsAsync("?days=3660"));
        // Standard = 30 Tage
        Assert.Equal(await IdsAsync("?days=30"), await IdsAsync(""));
        Assert.Equal(new[] { expired, today, tomorrow }, await IdsAsync(""));

        // die globale Grenze für "days" (1 bis 3660) gilt auch hier: 400 statt stillem Kappen
        foreach (var bad in new[] { "0", "-5", "3661" })
            await (await admin.GetAsync($"/api/reports/expiring?days={bad}")).ExpectProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Every_stock_row_of_a_lot_is_listed_with_its_bin_and_lot_less_rows_with_expiry_too()
    {
        var admin = await W.AdminAsync();
        var article = await W.AddArticleAsync();
        await W.AddLegacyStockAsync(article.Id, Site.PickA, 4, "LOT-A", WorldBuilder.InDays(10));
        await W.AddLegacyStockAsync(article.Id, Site.PickB, 6, "LOT-A", WorldBuilder.InDays(10));
        await W.AddLegacyStockAsync(article.Id, Site.PickA, 3, "LOT-B", WorldBuilder.InDays(20));
        await W.AddLegacyStockAsync(article.Id, Site.Reserve, 9, null, WorldBuilder.InDays(15));

        var rows = (await ExpiringAsync(admin, "?days=30")).Where(r => r.ArticleId == article.Id).ToList();

        // je Bestandszeile eine Zeile (dieselbe Charge in zwei Lagerplätzen = zwei Zeilen), nach MHD sortiert
        Assert.Equal(new[] { 10, 10, 15, 20 }, rows.Select(r => r.DaysUntilExpiry));
        Assert.Equal(
            new[] { $"LOT-A|10|4|{Site.PickA.Code}", $"LOT-A|10|6|{Site.PickB.Code}", $"-|15|9|{Site.Reserve.Code}", $"LOT-B|20|3|{Site.PickA.Code}" }
                .Order(StringComparer.Ordinal),
            rows.Select(r => $"{r.LotNumber ?? "-"}|{r.DaysUntilExpiry}|{r.Quantity}|{r.BinCode}").Order(StringComparer.Ordinal));
        Assert.Contains(rows, r => r.BinId == Site.PickB.Id && r.LotNumber == "LOT-A" && r.Quantity == 6);
        Assert.Contains(rows, r => r.BinId == Site.Reserve.Id && r.LotNumber is null && r.Quantity == 9);
    }

    [Fact]
    public async Task Any_signed_in_role_may_read_the_list_but_anonymous_may_not()
    {
        var viewer = await W.ClientAsync("Viewer");
        var article = await StockAsync(5);

        var rows = await ExpiringAsync(viewer, "?days=30");

        Assert.Contains(rows, r => r.ArticleId == article);
        Assert.Equal(HttpStatusCode.Unauthorized, (await W.Anonymous().GetAsync("/api/reports/expiring")).StatusCode);
    }
}
