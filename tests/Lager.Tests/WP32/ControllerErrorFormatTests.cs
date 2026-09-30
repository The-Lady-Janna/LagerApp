using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Auth;
using Lager.Contracts.Customers;
using Lager.Contracts.PickLists;
using Lager.Contracts.Purchasing;
using Lager.Contracts.Returns;
using Lager.Contracts.Shipping;
using Lager.Domain.Orders;
using Lager.Tests.Infrastructure;
using Lager.Tests.WP09;
using Lager.Tests.WP13;

namespace Lager.Tests.WP32;

/// <summary>
/// Kein Controller mappt Fachfehler mehr selbst: Regelverstoß (409), ungültige Eingabe (400) und nicht gefunden (404)
/// kommen über den globalen Handler als <c>application/problem+json</c> mit type/title/status/detail/code/correlationId
/// und dem Kompatibilitätsfeld <c>error</c> - bei Picklisten, Wellen, Einkauf, Retouren, Versand, Kunden, Bestellungen
/// (inkl. Storno-Klasse) und der Administration. Vorher lieferten sie <c>{ error }</c> bzw. <c>{ code, error }</c>.
/// </summary>
public class ControllerErrorFormatTests : IClassFixture<Wp32Fixture>
{
    private readonly Wp32Fixture _fx;

    public ControllerErrorFormatTests(Wp32Fixture fixture) => _fx = fixture;

    private HttpClient Admin => _fx.Admin;
    private Wp13World Orders => _fx.Orders;

    // ---- Picklisten (früher RunAsync mit { error }) ---------------------------------------------------

    [Fact]
    public async Task PickLists_report_rule_violations_and_bad_input_as_problem_details()
    {
        // Regelverstoß (InvalidOperationException im Service): die Bestellung gibt es nicht -> 409
        var unknownOrder = await Admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { Guid.NewGuid() }));
        await ProblemAssert.HasAsync(unknownOrder, HttpStatusCode.Conflict, "conflict");

        // ungültige Eingabe (ArgumentException im Service): die Position gehört nicht zur Liste -> 400
        var (article, _) = await Orders.AddStockedArticleAsync(5);
        var list = await Orders.GenerateAsync(await Orders.AddOrderAsync(article, 1));
        var foreignItem = await Admin.PostAsJsonAsync($"/api/picklists/{list.Id}/pack",
            new PackPickListRequest(new[] { new ConfirmPackedItemRequest(Guid.NewGuid(), 1) }));
        await ProblemAssert.HasAsync(foreignItem, HttpStatusCode.BadRequest, "validation_failed");

        // Zustandsfehler: das zweite Packen derselben Liste -> 409 mit der Regel-Meldung des Services im detail
        var pack = new PackPickListRequest(list.Items.Select(i => new ConfirmPackedItemRequest(i.Id, i.Quantity)).ToList());
        (await Admin.PostAsJsonAsync($"/api/picklists/{list.Id}/pack", pack)).EnsureSuccessStatusCode();
        var again = await ProblemAssert.HasAsync(await Admin.PostAsJsonAsync($"/api/picklists/{list.Id}/pack", pack), HttpStatusCode.Conflict, "conflict");
        Assert.Contains("bereits verpackt", again.GetProperty("detail").GetString());
    }

    // ---- Kommissionierwellen (RunAsync und Cancel) ----------------------------------------------------

    [Fact]
    public async Task PickWaves_report_state_conflicts_as_problem_details()
    {
        var created = await Admin.PostAsJsonAsync("/api/pick-waves", new CreatePickWaveRequest("WP32", null, Array.Empty<Guid>()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var wave = (await created.Content.ReadFromJsonAsync<PickWaveDto>())!;

        // Release (früher RunAsync): eine leere Welle lässt sich nicht freigeben
        var release = await ProblemAssert.HasAsync(
            await Admin.PostAsJsonAsync($"/api/pick-waves/{wave.Id}/release", new ReleaseWaveRequest()), HttpStatusCode.Conflict, "conflict");
        Assert.Contains("Leere Welle", release.GetProperty("detail").GetString());

        // Cancel (früher eigener try/catch): einmal geht, das zweite Mal ist ein Konflikt
        Assert.Equal(HttpStatusCode.NoContent, (await Admin.PostAsync($"/api/pick-waves/{wave.Id}/cancel", null)).StatusCode);
        await ProblemAssert.HasAsync(await Admin.PostAsync($"/api/pick-waves/{wave.Id}/cancel", null), HttpStatusCode.Conflict, "conflict");
    }

    // ---- Einkauf (inkl. Brücke Bestellung -> Wareneingang) --------------------------------------------

    [Fact]
    public async Task PurchaseOrders_report_domain_errors_with_their_code_as_problem_details()
    {
        var article = await _fx.Stock.AddArticleAsync();
        Task<HttpResponseMessage> Create(Guid supplier) => Admin.PostAsJsonAsync("/api/purchase-orders",
            new CreatePurchaseOrderRequest(supplier, null, null, new[] { new CreatePurchaseOrderLineRequest(article, 5) }));

        await ProblemAssert.HasAsync(await Create(Guid.NewGuid()), HttpStatusCode.NotFound, "supplier_not_found");
        await ProblemAssert.HasAsync(await Create(await _fx.Stock.AddSupplierAsync(active: false)), HttpStatusCode.Conflict, "supplier_inactive");

        // PurchaseOrderInboundController: ein Entwurf (noch nicht versendet) erwartet keine Ware -> 409 mit Code
        var draft = await Create(await _fx.Stock.AddSupplierAsync());
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var po = (await draft.Content.ReadFromJsonAsync<PurchaseOrderDto>())!;
        var bin = await _fx.Stock.AddBinAsync(await _fx.Stock.AddSiteAsync());
        var inbound = await Admin.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/create-inbound", new CreateInboundFromPurchaseOrderRequest(bin.Id));
        await ProblemAssert.HasAsync(inbound, HttpStatusCode.Conflict, "po_not_receivable");
    }

    // ---- Retouren -------------------------------------------------------------------------------------

    [Fact]
    public async Task Returns_report_domain_errors_with_their_code_as_problem_details()
    {
        var article = await _fx.Stock.AddArticleAsync();
        var notDelivered = await _fx.Stock.AddOrderAsync(OrderStatus.New, (article, 2));
        Task<HttpResponseMessage> Create(Guid? order, Guid articleId) => Admin.PostAsJsonAsync("/api/returns",
            new CreateReturnShipmentRequest(order, null, null, new[] { new CreateReturnLineRequest(articleId, 1) }));

        await ProblemAssert.HasAsync(await Create(notDelivered, article), HttpStatusCode.Conflict, "return_order_not_delivered");
        await ProblemAssert.HasAsync(await Create(null, Guid.NewGuid()), HttpStatusCode.NotFound, "article_not_found");
    }

    // ---- Versand (früher DomainErrorResults) ----------------------------------------------------------

    [Fact]
    public async Task Shipments_report_domain_errors_with_their_code_as_problem_details()
    {
        var (article, _) = await Orders.AddStockedArticleAsync(5);
        static CreateShipmentRequest For(Guid order) => new(order, null, "MANUAL", 300, 200, 100, 1500);

        // Regelverstoß (409): die Bestellung ist noch nicht gepackt; ungültige Eingabe (400): die Bestellung gibt es nicht
        var open = await Orders.AddOrderAsync(article, 1);
        await ProblemAssert.HasAsync(await Admin.PostAsJsonAsync("/api/shipments", For(open)), HttpStatusCode.Conflict, "order_not_packed");
        await ProblemAssert.HasAsync(await Admin.PostAsJsonAsync("/api/shipments", For(Guid.NewGuid())), HttpStatusCode.BadRequest, "unknown_order");

        // Tracking-Pflicht bei manuellem Carrier (400) und Versand ohne Label (409)
        var created = await Admin.PostAsJsonAsync("/api/shipments", For(await Orders.AddPackedOrderAsync(article, 1)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shipment = (await created.Content.ReadFromJsonAsync<ShipmentDto>())!;
        await ProblemAssert.HasAsync(
            await Admin.PostAsJsonAsync($"/api/shipments/{shipment.Id}/tracking", new AssignTrackingRequest()),
            HttpStatusCode.BadRequest, "tracking_number_required");
        await ProblemAssert.HasAsync(await Admin.PostAsync($"/api/shipments/{shipment.Id}/ship", null), HttpStatusCode.Conflict, "conflict");
    }

    // ---- Kunden (früher DomainErrorResults bei Create/AddAddress) -------------------------------------

    [Fact]
    public async Task Customers_report_a_duplicate_code_as_problem_details()
    {
        var code = Wp13World.Unique("K");
        var first = await Admin.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(code, "Erster Kunde"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var duplicate = await ProblemAssert.HasAsync(
            await Admin.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(code, "Zweiter Kunde")),
            HttpStatusCode.Conflict, "duplicate_customer_code");
        Assert.Contains("bereits vergeben", duplicate.GetProperty("detail").GetString());
    }

    // ---- Bestellungen (früher DomainErrorResults und lokales BadRequest mit { code, error }) ----------

    [Fact]
    public async Task Orders_report_domain_errors_and_idempotency_key_errors_as_problem_details()
    {
        var (article, _) = await Orders.AddStockedArticleAsync(10);
        var number = Wp13World.Unique("DUP");
        var request = Wp13World.OrderRequest(number, article, 1);
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/orders/manual", request)).StatusCode);

        // doppelte Bestellnummer: Regelverstoß mit Code (409)
        await ProblemAssert.HasAsync(await Admin.PostAsJsonAsync("/api/orders/manual", request), HttpStatusCode.Conflict, "duplicate_order_number");

        // Idempotency-Key: zu lang bzw. dem Body widersprechend (400, Code der Prüfung im Controller)
        Task<HttpResponseMessage> Post(string key, string? externalReference)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
            {
                Content = JsonContent.Create(Wp13World.OrderRequest(Wp13World.Unique("KEY"), article, 1, externalReference: externalReference)),
            };
            message.Headers.Add("Idempotency-Key", key);
            return Admin.SendAsync(message);
        }
        await ProblemAssert.HasAsync(await Post(new string('k', Order.MaxExternalReferenceLength + 1), null),
            HttpStatusCode.BadRequest, "invalid_idempotency_key");
        await ProblemAssert.HasAsync(await Post("schluessel", "etwas-anderes"), HttpStatusCode.BadRequest, "idempotency_key_mismatch");

        // Storno (OrderCancellationController): eine gepackte Bestellung lässt sich nicht mehr stornieren (409)
        var packed = await Orders.AddPackedOrderAsync(article, 1);
        await ProblemAssert.HasAsync(await Admin.PostAsync($"/api/orders/{packed}/cancel", null), HttpStatusCode.Conflict, "order_not_cancellable");
    }

    // ---- Administration -------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_reports_environment_and_configuration_errors_as_problem_details()
    {
        // Testumgebung = weder Development noch Restore freigegeben
        var reseed = await ProblemAssert.HasAsync(await Admin.PostAsync("/api/admin/reseed", null), HttpStatusCode.Forbidden, "development_only");
        Assert.Contains("Development", reseed.GetProperty("detail").GetString());
        await ProblemAssert.HasAsync(await Admin.PostAsync("/api/admin/seed-bulk?count=5", null), HttpStatusCode.Forbidden, "development_only");
        await ProblemAssert.HasAsync(await Admin.PostAsync("/api/admin/restore", FormWithoutFile()), HttpStatusCode.Forbidden, "restore_disabled");
    }

    /// <summary>Ein gültiges Multipart-Formular ohne Datei (ein leeres Formular würde schon das Model-Binding mit 400 abweisen).</summary>
    private static MultipartFormDataContent FormWithoutFile() => new() { { new StringContent("kein Upload"), "hinweis" } };

    [Fact]
    public async Task Admin_reports_range_and_upload_errors_as_problem_details()
    {
        using var development = new Wp09Factory("Development");
        var admin = await development.CreateClient().AsReadyAdminAsync();

        await ProblemAssert.HasAsync(await admin.PostAsync("/api/admin/seed-bulk?count=0", null), HttpStatusCode.BadRequest, "validation_failed");
        var noFile = await ProblemAssert.HasAsync(await admin.PostAsync("/api/admin/restore", FormWithoutFile()), HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("Keine Datei", noFile.GetProperty("detail").GetString());

        using var garbage = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "kaputt.db" } };
        await ProblemAssert.HasAsync(await admin.PostAsync("/api/admin/restore", garbage), HttpStatusCode.BadRequest, "invalid_backup_file");
    }

    /// <summary>
    /// Die IO-Ausnahme beim Austausch der DB-Datei ist der einzige catch im AdminController: er übersetzt sie in eine
    /// InvalidOperationException mit Code, die Antwort (409 database_in_use) baut die zentrale Fehlerabbildung. Nachgestellt
    /// mit einer Datei, die ein anderer Prozess offen hält (ohne Löschfreigabe): nur Windows verweigert dann das Ersetzen -
    /// unter Linux ersetzt rename die Datei trotzdem, dort gibt es diesen Fehler nicht.
    /// </summary>
    [Fact]
    public async Task Admin_restore_onto_a_database_file_in_use_is_a_conflict_with_code_database_in_use()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var development = new Wp09Factory("Development");
        var admin = await development.CreateClient().AsReadyAdminAsync();

        // eine gültige Sicherung als Upload
        var backup = await admin.PostAsync("/api/admin/backup", null);
        backup.EnsureSuccessStatusCode();
        var fileName = JsonDocument.Parse(await backup.Content.ReadAsStringAsync()).RootElement.GetProperty("fileName").GetString()!;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(development.Directory, fileName));

        // SQLite darf die Datei weiter lesen und schreiben, ersetzt werden kann sie nicht
        using (new FileStream(development.DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "backup.db" } };
            var response = await ProblemAssert.HasAsync(await admin.PostAsync("/api/admin/restore", form), HttpStatusCode.Conflict, "database_in_use");
            Assert.Contains("noch in Benutzung", response.GetProperty("detail").GetString());
        }

        // die App läuft danach normal weiter (nichts wurde ersetzt, keine Temp-Datei bleibt liegen)
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
        Assert.Empty(Directory.GetFiles(development.Directory, "*.tmp"));
    }

    // ---- Die frühere DomainRuleExceptionMiddleware: user_rule_violation und unknown_role bleiben -------

    [Fact]
    public async Task User_rule_violations_and_unknown_roles_are_delivered_by_the_global_handler()
    {
        var me = (await Admin.GetFromJsonAsync<UserDto>("/api/auth/me"))!;

        // Selbst-Aussperrung (UserRuleViolationException) -> 409; der Account bleibt aktiv
        await ProblemAssert.HasAsync(await Admin.PostAsync($"/api/users/{me.Id}/deactivate", null), HttpStatusCode.Conflict, "user_rule_violation");
        Assert.True((await Admin.GetFromJsonAsync<UserDto>("/api/auth/me"))!.IsActive);

        // Tippfehler im Rollennamen (UnknownRoleException) -> 400
        var unknownRole = await Admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest("wp32-rolle", "Ein-gutes-Passwort-2025!", new[] { "Lagermeister" }, MustChangePassword: false));
        await ProblemAssert.HasAsync(unknownRole, HttpStatusCode.BadRequest, "unknown_role");
    }
}
