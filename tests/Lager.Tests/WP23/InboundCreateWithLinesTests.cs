using System.Net;
using System.Net.Http.Json;
using System.Text;
using Lager.Contracts.Inbound;
using Lager.Domain.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP23;

/// <summary>
/// Regression zum Wareneingangs-Bug: Die Erfassungsmaske schickt beim Anlegen einer Lieferung <c>lines</c> mit, POST /api/inbound
/// legte aber nur den Kopf an - das Ergebnis war eine LEERE Lieferung, die Zeilen gingen still verloren. Jetzt legt der Request
/// Kopf und Zeilen atomar an (Zeilen geprüft wie bei POST {id}/lines).
/// </summary>
public class InboundCreateWithLinesTests : IClassFixture<Wp23Fixture>
{
    private readonly Wp23Fixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public InboundCreateWithLinesTests(Wp23Fixture fixture) => _fx = fixture;

    [Fact]
    public async Task A_shipment_created_with_lines_has_them_and_receiving_books_stock_per_lot_with_expiry()
    {
        var receiver = await W.ClientAsync("Receiver");
        var article = await W.AddArticleAsync(priceCents: 120);
        var expiryA = WorldBuilder.InDays(45);
        var expiryB = WorldBuilder.InDays(200);
        var number = WorldBuilder.Unique("WE");

        // genau der Body der Erfassungsmaske: camelCase, Datum als "yyyy-MM-dd", ohne die optionalen Felder
        var body = $$"""
            {"shipmentNumber":"{{number}}","supplierReference":"Lieferant WP23","lines":[
              {"articleId":"{{article.Id}}","targetBinId":"{{Site.PickA.Id}}","quantity":30,"lotNumber":"LOT-A","expiryDate":"{{expiryA:yyyy-MM-dd}}"},
              {"articleId":"{{article.Id}}","targetBinId":"{{Site.PickA.Id}}","quantity":12,"lotNumber":"LOT-B","expiryDate":"{{expiryB:yyyy-MM-dd}}"}]}
            """;
        var created = await (await receiver.PostAsync("/api/inbound", new StringContent(body, Encoding.UTF8, "application/json")))
            .ExpectAsync<InboundShipmentDto>(HttpStatusCode.Created);

        // die Zeilen sind da (früher: Lines leer) - in der Antwort und beim erneuten Lesen
        Assert.Equal("Draft", created.Status);
        Assert.Equal(2, created.Lines.Count);
        var reread = await (await receiver.GetAsync($"/api/inbound/{created.Id}")).ExpectAsync<InboundShipmentDto>();
        Assert.Equal(2, reread.Lines.Count);
        var lineA = Assert.Single(reread.Lines, l => l.LotNumber == "LOT-A");
        Assert.Equal((article.Id, Site.PickA.Id, 30, expiryA), (lineA.ArticleId, lineA.TargetBinId, lineA.Quantity, lineA.ExpiryDate));
        Assert.Empty(await receiver.StockOfAsync(article.Id)); // ein Entwurf bucht nichts

        // Buchen: eine Bestandszeile je Charge mit Charge und MHD, ein Ledger-Eintrag je Zeile
        var received = await (await receiver.PostAsync($"/api/inbound/{created.Id}/receive", null)).ExpectAsync<InboundShipmentDto>();
        Assert.Equal("Received", received.Status);
        var stock = await receiver.StockOfAsync(article.Id);
        Assert.Equal(2, stock.Count);
        Assert.Equal((30, expiryA), stock.Where(s => s.LotNumber == "LOT-A").Select(s => (s.Quantity, s.ExpiryDate!.Value)).Single());
        Assert.Equal((12, expiryB), stock.Where(s => s.LotNumber == "LOT-B").Select(s => (s.Quantity, s.ExpiryDate!.Value)).Single());
        var movements = await W.MovementsAsync(article.Id);
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Equal((StockMovementReason.Inbound, "InboundShipment", (Guid?)created.Id, 120),
            (m.Reason, m.ReferenceType, m.ReferenceId, m.UnitCostCents)));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task A_shipment_without_lines_is_still_created_empty_and_takes_lines_afterwards()
    {
        var receiver = await W.ClientAsync("Receiver");
        var article = await W.AddArticleAsync();

        var created = await (await receiver.PostAsJsonAsync("/api/inbound",
            new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), null, null))).ExpectAsync<InboundShipmentDto>(HttpStatusCode.Created);
        Assert.Empty(created.Lines);

        var filled = await (await receiver.PostAsJsonAsync($"/api/inbound/{created.Id}/lines",
            new AddInboundLineRequest(article.Id, Site.PickA.Id, 3, null, null))).ExpectAsync<InboundShipmentDto>();
        Assert.Single(filled.Lines);
    }

    [Fact]
    public async Task One_bad_line_creates_nothing_the_error_names_the_line_and_the_number_stays_free()
    {
        var receiver = await W.ClientAsync("Receiver");
        var article = await W.AddArticleAsync();
        var number = WorldBuilder.Unique("WE");
        var good = new AddInboundLineRequest(article.Id, Site.PickA.Id, 5, "LOT-OK", WorldBuilder.InDays(30));

        // unbekannter Artikel in Zeile 2 -> 404, nichts angelegt
        var unknownArticle = await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(number, null, null,
            new[] { good, new AddInboundLineRequest(Guid.NewGuid(), Site.PickA.Id, 1, null, null) }));
        var notFound = await unknownArticle.ExpectProblemAsync(HttpStatusCode.NotFound, "article_not_found");
        Assert.Contains("Zeile 2", notFound.DetailOf());

        // Menge 0 -> 400 mit Zeilennummer
        var zero = await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(number, null, null,
            new[] { good, good with { Quantity = 0 } }));
        Assert.Contains("Zeile 2", (await zero.ExpectProblemAsync(HttpStatusCode.BadRequest)).DetailOf());

        // dieselbe Charge im selben Platz mit zwei MHD -> 409 lot_expiry_mismatch
        var mismatch = await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(number, null, null,
            new[] { good, good with { ExpiryDate = WorldBuilder.InDays(31) } }));
        var conflict = await mismatch.ExpectProblemAsync(HttpStatusCode.Conflict, "lot_expiry_mismatch");
        Assert.Contains("Zeile 2", conflict.DetailOf());

        // eine Bestellzeile geht nicht (die neue Lieferung hat keine Bestellung) -> 409
        await (await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(number, null, null,
            new[] { good with { PurchaseOrderLineId = Guid.NewGuid() } }))).ExpectProblemAsync(HttpStatusCode.Conflict, "inbound_without_po");

        // nichts davon hat die Lieferung angelegt: die Nummer ist frei, und mit korrekten Zeilen klappt derselbe Aufruf
        var all = await (await receiver.GetAsync("/api/inbound")).ExpectAsync<List<InboundShipmentDto>>();
        Assert.DoesNotContain(all, s => s.ShipmentNumber == number);
        var created = await (await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(number, null, null, new[] { good })))
            .ExpectAsync<InboundShipmentDto>(HttpStatusCode.Created);
        Assert.Single(created.Lines);
    }

    [Fact]
    public async Task Lines_are_validated_like_add_line_and_only_receivers_may_create()
    {
        var receiver = await W.ClientAsync("Receiver");
        var viewer = await W.ClientAsync("Viewer");
        var article = await W.AddArticleAsync();
        var line = new AddInboundLineRequest(article.Id, Site.PickA.Id, 1, null, null);

        await (await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), null, null,
            new[] { line with { LotNumber = new string('L', 65) } }))).ExpectProblemAsync(HttpStatusCode.BadRequest);
        await (await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), null, null,
            new[] { line with { ExpiryDate = new DateTime(1999, 12, 31) } }))).ExpectProblemAsync(HttpStatusCode.BadRequest, "expiry_invalid");
        await (await receiver.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), null, null,
            new[] { line with { TargetBinId = Guid.NewGuid() } }))).ExpectProblemAsync(HttpStatusCode.NotFound, "bin_not_found");

        var forbidden = await viewer.PostAsJsonAsync("/api/inbound", new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), null, null, new[] { line }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
