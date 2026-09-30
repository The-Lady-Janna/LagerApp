using FluentValidation.TestHelper;
using Lager.Api.Validation;
using Lager.Contracts.Articles;
using Lager.Contracts.Auth;
using Lager.Contracts.PickLists;
using Lager.Contracts.Stock;
using Lager.Contracts.Suppliers;
using Lager.Contracts.Warehouse;

namespace Lager.Tests.WP12;

/// <summary>Grenzwerte der Validatoren ohne Host: jeweils die Grenze selbst (gültig) und einen Schritt darüber/darunter (ungültig).</summary>
public class ValidatorTests
{
    private static readonly PositionDto Origin = new(0, 0, 0);

    private static IReadOnlyList<PositionDto> Points(int count) =>
        Enumerable.Range(0, count).Select(i => new PositionDto(i, i, 0)).ToList();

    // ---- Gemeinsame Regeln ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1_000_000, true)]
    [InlineData(-1_000_000, true)]
    [InlineData(0, true)]
    [InlineData(1_000_001, false)]
    [InlineData(-1_000_001, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void Coordinates_are_limited_to_plus_minus_one_million_mm(int value, bool valid)
    {
        var validator = new PositionDtoValidator();

        Assert.Equal(valid, validator.TestValidate(new PositionDto(value, 0, 0)).IsValid);
        Assert.Equal(valid, validator.TestValidate(new PositionDto(0, value, 0)).IsValid);
        Assert.Equal(valid, validator.TestValidate(new PositionDto(0, 0, value)).IsValid);
    }

    [Theory]
    [InlineData("days", 1, true)]
    [InlineData("days", 3660, true)]
    [InlineData("days", 0, false)]
    [InlineData("days", 3661, false)]
    [InlineData("Range", 3661, false)]
    [InlineData("rangeDays", 100_000, false)]
    [InlineData("top", 1000, true)]
    [InlineData("take", 1001, false)]
    [InlineData("pageSize", 0, false)]
    [InlineData("skip", 0, true)]
    [InlineData("skip", -1, false)]
    [InlineData("page", 0, true)]
    [InlineData("page", -1, false)]
    [InlineData("page", 3, true)]
    [InlineData("quantity", int.MaxValue, true)] // unbekannte Parameter bleiben unberührt
    [InlineData("count", -1, true)]
    public void Period_and_paging_parameters_have_bounds(string name, int value, bool valid)
    {
        Assert.Equal(valid, QueryLimits.Validate(name, value) is null);
    }

    // ---- Lager ---------------------------------------------------------------------------------------------------

    private static CreateShelfRequest Shelf(int bins = 3) =>
        new(Guid.NewGuid(), "A1-05", Origin, 2000, 600, 2000, bins, 400, 600, 300, 20_000);

    [Theory]
    [InlineData(0, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    [InlineData(100_000, false)]
    [InlineData(-1, false)]
    public void A_shelf_gets_at_most_500_initial_bins(int bins, bool valid)
    {
        var result = new CreateShelfRequestValidator().TestValidate(Shelf(bins));

        Assert.Equal(valid, result.IsValid);
        if (!valid) result.ShouldHaveValidationErrorFor(x => x.InitialBinCount);
    }

    [Fact]
    public void Shelf_dimensions_must_be_positive_and_bounded()
    {
        var request = Shelf() with { WidthMm = 0, DepthMm = -1, HeightMm = 1_000_001, Position = new PositionDto(0, 2_000_000, 0) };

        var result = new CreateShelfRequestValidator().TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.WidthMm);
        result.ShouldHaveValidationErrorFor(x => x.DepthMm);
        result.ShouldHaveValidationErrorFor(x => x.HeightMm);
        result.ShouldHaveValidationErrorFor("Position.YMm");
    }

    [Fact]
    public void An_empty_shelf_does_not_need_bin_sizes_but_a_missing_position_is_an_error()
    {
        var empty = Shelf(0) with { BinWidthMm = 0, BinDepthMm = 0, BinHeightMm = 0, BinMaxWeightGrams = 0 };

        new CreateShelfRequestValidator().TestValidate(empty).ShouldNotHaveAnyValidationErrors();
        new CreateShelfRequestValidator().TestValidate(Shelf() with { Position = null! }).ShouldHaveValidationErrorFor(x => x.Position);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(500, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(501, false)]
    public void A_wall_has_2_to_500_points(int count, bool valid)
    {
        var result = new CreateWallRequestValidator().TestValidate(new CreateWallRequest(Guid.NewGuid(), "Wand", Points(count), 200));

        Assert.Equal(valid, result.IsValid);
        if (!valid) result.ShouldHaveValidationErrorFor(x => x.Points);
    }

    [Fact]
    public void A_huge_point_list_is_rejected_by_counting_without_validating_every_point()
    {
        // Jeder Punkt liegt außerhalb des Koordinatenbereichs: Würde der Validator trotz der falschen Anzahl jeden Punkt prüfen,
        // gäbe es 300000 Einzelfehler. Das ist die zeitunabhängige Form der früheren Laufzeitgrenze (eine Wanduhr-Grenze kippt auf
        // einem ausgelasteten Rechner, ohne dass im Code etwas kaputt ist).
        var tooFar = Enumerable.Repeat(new PositionDto(5_000_000, 0, 0), 300_000).ToList();

        var result = new UpdateWallPointsRequestValidator().TestValidate(new UpdateWallPointsRequest(tooFar));

        result.ShouldHaveValidationErrorFor(x => x.Points);
        var error = Assert.Single(result.Errors); // nur die Anzahl wird gemeldet, keine 300000 Einzelfehler
        Assert.Equal("Points", error.PropertyName);
    }

    [Fact]
    public void Every_wall_point_is_checked_and_thickness_and_label_are_bounded()
    {
        var points = new[] { Origin, new PositionDto(0, 0, 5_000_000) };

        var result = new CreateWallRequestValidator().TestValidate(
            new CreateWallRequest(Guid.Empty, new string('x', 129), points, 0));

        result.ShouldHaveValidationErrorFor(x => x.WarehouseId);
        result.ShouldHaveValidationErrorFor(x => x.Label);
        result.ShouldHaveValidationErrorFor(x => x.ThicknessMm);
        Assert.Contains(result.Errors, e => e.PropertyName.EndsWith("ZMm"));
    }

    [Fact]
    public void Pick_point_and_bin_type_names_are_checked_against_the_enums()
    {
        var bin = new SetBinTypeRequestValidator();

        Assert.True(bin.TestValidate(new SetBinTypeRequest("HotPick", 3)).IsValid);
        Assert.True(bin.TestValidate(new SetBinTypeRequest("reserve", 0)).IsValid);
        Assert.False(bin.TestValidate(new SetBinTypeRequest("2", 0)).IsValid);
        Assert.False(bin.TestValidate(new SetBinTypeRequest("", 0)).IsValid);
        Assert.False(bin.TestValidate(new SetBinTypeRequest("Standard", -1)).IsValid);
        Assert.True(new UpdatePickPointRequestValidator().TestValidate(new UpdatePickPointRequest("Rampe", "Both")).IsValid);
        Assert.False(new UpdatePickPointRequestValidator().TestValidate(new UpdatePickPointRequest("Rampe", "Mitte")).IsValid);
    }

    // ---- Picken --------------------------------------------------------------------------------------------------

    private static IReadOnlyList<Guid> Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Theory]
    [InlineData(1, true)]
    [InlineData(200, true)]
    [InlineData(0, false)]
    [InlineData(201, false)]
    public void A_pick_list_takes_1_to_200_orders(int count, bool valid)
    {
        var result = new GeneratePickListRequestValidator().TestValidate(new GeneratePickListRequest(Ids(count)));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void A_pick_list_rejects_duplicate_and_empty_order_ids()
    {
        var id = Guid.NewGuid();
        var validator = new GeneratePickListRequestValidator();

        validator.TestValidate(new GeneratePickListRequest(new[] { id, id })).ShouldHaveValidationErrorFor(x => x.OrderIds);
        validator.TestValidate(new GeneratePickListRequest(new[] { Guid.Empty })).ShouldHaveValidationErrorFor(x => x.OrderIds);
        validator.TestValidate(new GeneratePickListRequest(null!)).ShouldHaveValidationErrorFor(x => x.OrderIds);
        validator.TestValidate(new GeneratePickListRequest(new[] { id }, Guid.Empty)).ShouldHaveValidationErrorFor(x => x.StartPickPointId);
        validator.TestValidate(new GeneratePickListRequest(new[] { id }, Guid.NewGuid(), Guid.NewGuid())).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1_000_000, true)]
    [InlineData(1_000_001, false)]
    [InlineData(-1, false)]
    public void A_packed_quantity_is_between_0_and_1000000(int quantity, bool valid)
    {
        var request = new PackPickListRequest(new[] { new ConfirmPackedItemRequest(Guid.NewGuid(), quantity) });

        Assert.Equal(valid, new PackPickListRequestValidator().TestValidate(request).IsValid);
    }

    [Fact]
    public void A_pack_request_has_a_list_limit_and_no_repeated_positions()
    {
        var validator = new PackPickListRequestValidator();
        var repeated = Guid.NewGuid();

        var tooMany = validator.TestValidate(new PackPickListRequest(
            Enumerable.Range(0, 5001).Select(_ => new ConfirmPackedItemRequest(Guid.NewGuid(), 1)).ToList()));

        tooMany.ShouldHaveValidationErrorFor(x => x.Items);
        validator.TestValidate(new PackPickListRequest(new[] { new ConfirmPackedItemRequest(repeated, 1), new ConfirmPackedItemRequest(repeated, 1) }))
            .ShouldHaveValidationErrorFor(x => x.Items);
        validator.TestValidate(new PackPickListRequest(null!)).ShouldHaveValidationErrorFor(x => x.Items);
        Assert.True(validator.TestValidate(new PackPickListRequest(
            Enumerable.Range(0, 5000).Select(_ => new ConfirmPackedItemRequest(Guid.NewGuid(), 1)).ToList())).IsValid);
    }

    [Fact]
    public void Cart_configs_have_positive_bounded_dimensions()
    {
        var ok = new CreatePickCartConfigRequest("Wagen 1", 4, 600, 800, 300, 50_000);
        var create = new CreatePickCartConfigRequestValidator();
        var update = new UpdatePickCartConfigRequestValidator();

        Assert.True(create.TestValidate(ok).IsValid);
        Assert.True(update.TestValidate(new UpdatePickCartConfigRequest("Wagen 1", 4, 600, 800, 300, 50_000)).IsValid);
        create.TestValidate(ok with { LevelCount = 101 }).ShouldHaveValidationErrorFor(x => x.LevelCount);
        create.TestValidate(ok with { LevelWidthMm = 10_001 }).ShouldHaveValidationErrorFor(x => x.LevelWidthMm);
        create.TestValidate(ok with { MaxWeightGrams = 0 }).ShouldHaveValidationErrorFor(x => x.MaxWeightGrams);
        create.TestValidate(ok with { Name = " " }).ShouldHaveValidationErrorFor(x => x.Name);
        update.TestValidate(new UpdatePickCartConfigRequest("", 0, 0, 0, 0, 0)).ShouldHaveValidationErrorFor(x => x.LevelHeightMm);
    }

    [Fact]
    public void Wave_requests_limit_text_and_order_lists()
    {
        var wave = new CreatePickWaveRequestValidator();

        Assert.True(wave.TestValidate(new CreatePickWaveRequest(null, null, Array.Empty<Guid>())).IsValid); // leere Welle ist erlaubt
        Assert.True(wave.TestValidate(new CreatePickWaveRequest(new string('x', 500), null, Ids(500))).IsValid);
        wave.TestValidate(new CreatePickWaveRequest(new string('x', 501), null, Ids(1))).ShouldHaveValidationErrorFor(x => x.Description);
        wave.TestValidate(new CreatePickWaveRequest(null, null, Ids(501))).ShouldHaveValidationErrorFor(x => x.OrderIds);
        new AddOrdersToWaveRequestValidator().TestValidate(new AddOrdersToWaveRequest(null!)).ShouldHaveValidationErrorFor(x => x.OrderIds);
    }

    // ---- Anmeldung, Benutzer, Lieferanten ------------------------------------------------------------------------------

    [Fact]
    public void Credentials_have_upper_bounds_but_empty_logins_are_left_to_the_login_itself()
    {
        var login = new LoginRequestValidator();

        // Leer bleibt erlaubt: der Login antwortet darauf mit der einheitlichen 401 (keine Rückschlüsse auf Konten).
        Assert.True(login.TestValidate(new LoginRequest("", "")).IsValid);
        Assert.True(login.TestValidate(new LoginRequest(new string('u', 256), new string('p', 1024))).IsValid);
        login.TestValidate(new LoginRequest(new string('u', 257), "x")).ShouldHaveValidationErrorFor(x => x.Username);
        login.TestValidate(new LoginRequest("x", new string('p', 1025))).ShouldHaveValidationErrorFor(x => x.Password);
        new ChangePasswordRequestValidator().TestValidate(new ChangePasswordRequest("a", new string('p', 1025))).ShouldHaveValidationErrorFor(x => x.NewPassword);
        new AdminResetPasswordRequestValidator().TestValidate(new AdminResetPasswordRequest(new string('p', 1025))).ShouldHaveValidationErrorFor(x => x.NewPassword);
    }

    [Fact]
    public void User_requests_bound_the_role_list_and_texts_but_leave_role_names_to_the_service()
    {
        var create = new CreateUserRequestValidator();
        var update = new UpdateUserRequestValidator();

        Assert.True(create.TestValidate(new CreateUserRequest("anna", "Ein-gutes-Passwort-1", new[] { "Picker", "Lagermeister" })).IsValid); // Namen prüft der Service (unknown_role)
        create.TestValidate(new CreateUserRequest("anna", "x", null!)).ShouldHaveValidationErrorFor(x => x.Roles);
        create.TestValidate(new CreateUserRequest("anna", "x", Enumerable.Repeat("Picker", 21).ToArray())).ShouldHaveValidationErrorFor(x => x.Roles);
        create.TestValidate(new CreateUserRequest("anna", "x", new[] { "Picker" }, Email: new string('e', 257))).ShouldHaveValidationErrorFor(x => x.Email);
        update.TestValidate(new UpdateUserRequest(new[] { "Picker" }, DisplayName: new string('n', 129))).ShouldHaveValidationErrorFor(x => x.DisplayName);
        update.TestValidate(new UpdateUserRequest(new[] { new string('r', 65) })).ShouldHaveValidationErrorFor(x => x.Roles);
    }

    [Fact]
    public void Supplier_fields_follow_the_database_column_sizes()
    {
        var create = new CreateSupplierRequestValidator();
        var ok = new CreateSupplierRequest("SUP-1", "Lieferant GmbH");

        Assert.True(create.TestValidate(ok).IsValid);
        Assert.True(create.TestValidate(ok with { Code = new string('c', 32), Name = new string('n', 256), Notes = new string('x', 1000), Currency = "CHF" }).IsValid);
        create.TestValidate(ok with { Code = new string('c', 33) }).ShouldHaveValidationErrorFor(x => x.Code);
        create.TestValidate(ok with { Code = "" }).ShouldHaveValidationErrorFor(x => x.Code);
        create.TestValidate(ok with { Name = " " }).ShouldHaveValidationErrorFor(x => x.Name);
        create.TestValidate(ok with { Notes = new string('x', 1001) }).ShouldHaveValidationErrorFor(x => x.Notes);
        create.TestValidate(ok with { ContactPhone = new string('1', 65) }).ShouldHaveValidationErrorFor(x => x.ContactPhone);
        create.TestValidate(ok with { Currency = "EURO" }).ShouldHaveValidationErrorFor(x => x.Currency);
        create.TestValidate(ok with { LeadTimeDays = -1 }).ShouldHaveValidationErrorFor(x => x.LeadTimeDays);
        create.TestValidate(ok with { MinOrderValueCents = -1 }).ShouldHaveValidationErrorFor(x => x.MinOrderValueCents);
        new UpdateSupplierRequestValidator().TestValidate(new UpdateSupplierRequest("", null, null, new string('x', 1001), 7, 0, "EUR"))
            .ShouldHaveValidationErrorFor(x => x.Name);
    }

    // ---- Bestand und Artikel ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(1_000_000, true)]
    [InlineData(-1_000_000, true)]
    [InlineData(0, false)]
    [InlineData(1_000_001, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void A_stock_adjustment_is_1_to_1000000_in_either_direction(int delta, bool valid)
    {
        var result = new AdjustStockRequestValidator().TestValidate(
            new AdjustStockRequest(Guid.NewGuid(), Guid.NewGuid(), delta, null, null));

        Assert.Equal(valid, result.IsValid);
    }

    private static CreateArticleRequest Article(Func<CreateArticleRequest, CreateArticleRequest>? change = null)
    {
        var article = new CreateArticleRequest("SKU-1", "Artikel", null, new DimensionsDto(10, 10, 10), 100,
            new StackingInfoDto(false, "Z", 0, null));
        return change is null ? article : change(article);
    }

    [Fact]
    public void Article_limits_cover_text_lists_and_numbers()
    {
        var validator = new CreateArticleRequestValidator();

        validator.TestValidate(Article()).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(Article(a => a with { Description = new string('d', 2000) })).ShouldNotHaveAnyValidationErrors();
        validator.TestValidate(Article(a => a with { Description = new string('d', 2001) })).ShouldHaveValidationErrorFor(x => x.Description);
        validator.TestValidate(Article(a => a with { AlternativeSkus = new[] { "A,B" } })).ShouldHaveValidationErrorFor(x => x.AlternativeSkus);
        validator.TestValidate(Article(a => a with { AlternativeSkus = new[] { new string('x', 65) } })).ShouldHaveValidationErrorFor(x => x.AlternativeSkus);
        validator.TestValidate(Article(a => a with { AlternativeSkus = Enumerable.Range(0, 501).Select(i => "S" + i).ToArray() }))
            .ShouldHaveValidationErrorFor(x => x.AlternativeSkus);
        validator.TestValidate(Article(a => a with { BundleComponents = new[] { new CreateBundleComponentRequest(Guid.NewGuid(), 1_000_001) } }))
            .ShouldHaveValidationErrorFor(x => x.BundleComponents);
        validator.TestValidate(Article(a => a with { PurchasePriceCents = -1 })).ShouldHaveValidationErrorFor(x => x.PurchasePriceCents);
        validator.TestValidate(Article(a => a with { WeightGrams = 100_000_001 })).ShouldHaveValidationErrorFor(x => x.WeightGrams);
        validator.TestValidate(Article(a => a with { Dimensions = new DimensionsDto(10, 1_000_001, 10) })).ShouldHaveValidationErrorFor("Dimensions.WidthMm");
        validator.TestValidate(Article(a => a with { Stacking = new StackingInfoDto(true, "W", 0, null) })).ShouldHaveValidationErrorFor("Stacking.StackingAxis");
        validator.TestValidate(Article(a => a with { ValidFrom = new DateTime(2026, 6, 1), ValidUntil = new DateTime(2026, 5, 1) }))
            .ShouldHaveValidationErrorFor("ValidUntil");
    }

    [Fact]
    public void Alternative_skus_must_fit_together_into_the_1000_character_database_column()
    {
        // Jede SKU einzeln ist gültig (<= 64 Zeichen), aber zusammen (mit Komma verbunden) sprengen sie die Spalte:
        // SQLite erzwingt das nicht, MySQL bricht mit "Data too long" ab.
        static string[] Skus(int count) => Enumerable.Range(0, count).Select(i => new string((char)('a' + i), 64)).ToArray();
        var validator = new CreateArticleRequestValidator();

        // 15 x 64 + 14 Kommas = 974 Zeichen: passt.
        validator.TestValidate(Article(a => a with { AlternativeSkus = Skus(15) })).ShouldNotHaveAnyValidationErrors();
        // 16 x 64 + 15 Kommas = 1039 Zeichen: zu lang.
        validator.TestValidate(Article(a => a with { AlternativeSkus = Skus(16) })).ShouldHaveValidationErrorFor(x => x.AlternativeSkus);
        // Leereinträge und Duplikate speichert der Artikel nicht: sie zählen nicht mit.
        validator.TestValidate(Article(a => a with { AlternativeSkus = Skus(15).Concat(new[] { "", " ", Skus(1)[0].ToUpperInvariant() }).ToArray() }))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void The_update_validator_shares_the_article_limits()
    {
        var update = new UpdateArticleRequest("Artikel", new string('d', 2001), new DimensionsDto(10, 10, 10), 100, new StackingInfoDto(false, "Z", 0, null),
            AlternativeSkus: new[] { "A,B" });

        var result = new UpdateArticleRequestValidator().TestValidate(update);

        result.ShouldHaveValidationErrorFor(x => x.Description);
        result.ShouldHaveValidationErrorFor(x => x.AlternativeSkus);
    }
}
