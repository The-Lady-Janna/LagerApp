using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Orders;
using Lager.Domain.Orders;

namespace Lager.Tests.WP13;

/// <summary>
/// Die externe Bestell-API (POST /api/orders): wiederholbar über ExternalReference bzw. Idempotency-Key, klare Fehler statt
/// 500 bei doppelter Nummer, Artikel per Id oder SKU, nur bestellbare Artikel.
/// </summary>
public class ExternalOrderApiTests : IClassFixture<Wp13Fixture>
{
    private readonly Wp13Fixture _fx;
    private Wp13World W => _fx.World;

    public ExternalOrderApiTests(Wp13Fixture fixture) => _fx = fixture;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static CreateOrderRequest Request(string number, Guid article, string? externalReference = null, int quantity = 1) =>
        Wp13World.OrderRequest(number, article, quantity, externalReference: externalReference);

    // ---- Idempotenz -----------------------------------------------------

    [Fact]
    public async Task The_same_external_reference_returns_the_existing_order_instead_of_a_duplicate()
    {
        var (article, _) = await W.AddStockedArticleAsync(10);
        var admin = await _fx.AdminAsync();
        var number = Wp13World.Unique("EXT");
        var request = Request(number, article, externalReference: "SHOP-" + number);

        var first = await admin.PostAsJsonAsync("/api/orders", request);
        var second = await admin.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<OrderDto>())!;
        var replayed = (await second.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal("SHOP-" + number, replayed.ExternalReference);
        Assert.Equal("Api", replayed.Source);
        Assert.Equal(1, await W.OrderCountAsync(number));
    }

    [Fact]
    public async Task The_Idempotency_Key_header_acts_as_the_external_reference()
    {
        var (article, _) = await W.AddStockedArticleAsync(10);
        var admin = await _fx.AdminAsync();
        var number = Wp13World.Unique("KEY");
        var key = "key-" + number;

        Task<HttpResponseMessage> Post(string headerKey, CreateOrderRequest body)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(body) };
            message.Headers.Add("Idempotency-Key", headerKey);
            return admin.SendAsync(message);
        }

        var first = await Post(key, Request(number, article));
        var second = await Post(key, Request(number, article));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(key, (await second.Content.ReadFromJsonAsync<OrderDto>())!.ExternalReference);
        Assert.Equal(1, await W.OrderCountAsync(number));

        // Header und Body dürfen sich nicht widersprechen
        var mismatch = await Post(key, Request(Wp13World.Unique("KEY"), article, externalReference: "etwas-anderes"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal("idempotency_key_mismatch", await ErrorCodeAsync(mismatch));
    }

    [Fact]
    public async Task A_duplicate_order_number_without_a_matching_reference_is_a_conflict_with_a_code_not_a_500()
    {
        var (article, _) = await W.AddStockedArticleAsync(10);
        var number = Wp13World.Unique("DUP");
        await W.OrdersAsync(s => s.CreateAsync(Request(number, article), OrderSource.Api));

        // Service: Regelverstoß mit Code - auch mit Leerzeichen um die Nummer (die Prüfung trimmt) und mit fremder Referenz
        foreach (var repeat in new[] { Request(number, article), Request($"  {number} ", article), Request(number, article, "andere-referenz") })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => W.OrdersAsync(s => s.CreateAsync(repeat, OrderSource.Api)));
            Assert.Equal("duplicate_order_number", ex.Data["code"]);
        }
        Assert.Equal(1, await W.OrderCountAsync(number));

        // HTTP: 409 mit demselben Code
        var admin = await _fx.AdminAsync();
        var response = await admin.PostAsJsonAsync("/api/orders", Request(number, article));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("duplicate_order_number", await ErrorCodeAsync(response));
    }

    // ---- Artikel per SKU ------------------------------------------------

    [Fact]
    public async Task Lines_can_name_the_article_by_sku_or_by_id()
    {
        var bySku = await W.AddArticleAsync(sku: Wp13World.Unique("SKU-A"));
        var byId = await W.AddArticleAsync();
        var skuOfFirst = await W.DbAsync(db => Task.FromResult(db.Articles.Single(a => a.Id == bySku).Sku));
        var skuOfSecond = await W.DbAsync(db => Task.FromResult(db.Articles.Single(a => a.Id == byId).Sku));
        var admin = await _fx.AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/orders", new CreateOrderRequest(
            Wp13World.Unique("SKU"), null,
            new[] { new CreateOrderLineRequest(null, 2, skuOfFirst), new CreateOrderLineRequest(byId, 3) }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(new[] { (bySku, skuOfFirst, 2), (byId, skuOfSecond, 3) },
            order.Lines.Select(l => (l.ArticleId, l.ArticleSku, l.Quantity)).ToArray());
    }

    [Fact]
    public async Task Unknown_or_contradicting_articles_are_an_input_error()
    {
        var known = await W.AddArticleAsync();
        var other = await W.AddArticleAsync();
        var knownSku = await W.DbAsync(db => Task.FromResult(db.Articles.Single(a => a.Id == known).Sku));
        var admin = await _fx.AdminAsync();
        Task<HttpResponseMessage> Post(params CreateOrderLineRequest[] lines) =>
            admin.PostAsJsonAsync("/api/orders", new CreateOrderRequest(Wp13World.Unique("BAD"), null, lines));

        var unknownSku = await Post(new CreateOrderLineRequest(null, 1, "GIBT-ES-NICHT"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownSku.StatusCode);
        Assert.Equal("unknown_article", await ErrorCodeAsync(unknownSku));
        Assert.Contains("GIBT-ES-NICHT", await unknownSku.Content.ReadAsStringAsync());

        var unknownId = await Post(new CreateOrderLineRequest(Guid.NewGuid(), 1));
        Assert.Equal(HttpStatusCode.BadRequest, unknownId.StatusCode);
        Assert.Equal("unknown_article", await ErrorCodeAsync(unknownId));

        var mismatch = await Post(new CreateOrderLineRequest(other, 1, knownSku));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal("article_mismatch", await ErrorCodeAsync(mismatch));
    }

    // ---- Saison-Fenster -------------------------------------------------

    [Fact]
    public async Task Articles_outside_their_season_window_are_rejected_and_nothing_is_created()
    {
        var notYet = await W.AddArticleAsync(validFrom: DateTime.UtcNow.AddDays(10));
        var over = await W.AddArticleAsync(validUntil: DateTime.UtcNow.AddDays(-10));
        var inSeason = await W.AddArticleAsync(validFrom: DateTime.UtcNow.AddDays(-10), validUntil: DateTime.UtcNow.AddDays(10));
        var skuOfOver = await W.DbAsync(db => Task.FromResult(db.Articles.Single(a => a.Id == over).Sku));

        foreach (var article in new[] { notYet, over })
        {
            var number = Wp13World.Unique("SEASON");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => W.OrdersAsync(s => s.CreateAsync(Request(number, article), OrderSource.Manual)));
            Assert.Equal("article_not_orderable", ex.Data["code"]);
            Assert.Equal(0, await W.OrderCountAsync(number));
        }

        // mit klarer Meldung (SKU und Fenster) und über HTTP als 409
        var admin = await _fx.AdminAsync();
        var response = await admin.PostAsJsonAsync("/api/orders/manual", Request(Wp13World.Unique("SEASON"), over));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("article_not_orderable", await ErrorCodeAsync(response));
        Assert.Contains(skuOfOver, await response.Content.ReadAsStringAsync());

        var ok = await W.OrdersAsync(s => s.CreateAsync(Request(Wp13World.Unique("SEASON"), inSeason), OrderSource.Manual));
        Assert.Equal("New", ok.Status);
    }

    // ---- Planung und Validierung ----------------------------------------

    [Fact]
    public async Task Priority_and_due_date_are_stored_and_returned()
    {
        var article = await W.AddArticleAsync();
        var due = new DateTime(2031, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        var admin = await _fx.AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/orders/manual",
            Wp13World.OrderRequest(Wp13World.Unique("PLAN"), article, 1, priority: 2, dueDate: due));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal(2, order.Priority);
        Assert.Equal(due, order.DueDate);
        var listed = (await admin.GetFromJsonAsync<List<OrderDto>>("/api/orders"))!.Single(o => o.Id == order.Id);
        Assert.Equal((2, due), (listed.Priority, listed.DueDate));
    }

    [Fact]
    public async Task The_validator_rejects_out_of_range_input_with_a_400()
    {
        var article = await W.AddArticleAsync();
        var admin = await _fx.AdminAsync();
        Task<HttpResponseMessage> Post(CreateOrderRequest request) => admin.PostAsJsonAsync("/api/orders", request);
        var line = new CreateOrderLineRequest(article, 1);

        var tooManyLines = Enumerable.Range(0, 501).Select(_ => line).ToArray();
        var cases = new[]
        {
            new CreateOrderRequest(Wp13World.Unique("V"), null, new[] { new CreateOrderLineRequest(article, 0) }),
            new CreateOrderRequest(Wp13World.Unique("V"), null, new[] { new CreateOrderLineRequest(article, 100_001) }),
            new CreateOrderRequest(Wp13World.Unique("V"), null, tooManyLines),
            new CreateOrderRequest(Wp13World.Unique("V"), null, new[] { new CreateOrderLineRequest(null, 1) }),
            new CreateOrderRequest(Wp13World.Unique("V"), null, new[] { line }, Priority: 4),
            new CreateOrderRequest(Wp13World.Unique("V"), null, new[] { line }, ShippingAddressId: Guid.NewGuid()),
            new CreateOrderRequest(new string('N', 65), null, new[] { line }),
            new CreateOrderRequest(Wp13World.Unique("V"), null, new[] { line }, ExternalReference: new string('E', 129)),
        };

        foreach (var request in cases)
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(request)).StatusCode);
        // die Obergrenzen selbst sind erlaubt
        var atLimit = new CreateOrderRequest(Wp13World.Unique("V"), null, new[] { new CreateOrderLineRequest(article, 100_000) }, Priority: 3);
        Assert.Equal(HttpStatusCode.Created, (await Post(atLimit)).StatusCode);
    }
}
