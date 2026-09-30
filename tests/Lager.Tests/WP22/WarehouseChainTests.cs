using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Warehouse;
using Lager.Domain.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP22;

/// <summary>
/// Auf einer leeren Datenbank (Seed aus) lässt sich die ganze Lagerstruktur über die API aufbauen und danach lesen -
/// vorher gab es für Lager, Zonen und Gänge keinen Endpunkt, ohne Demodaten war kein Lager möglich.
/// </summary>
public class WarehouseChainTests : IClassFixture<LagerApiFactory>
{
    private readonly LagerApiFactory _factory;
    public WarehouseChainTests(LagerApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_warehouse_with_zone_aisle_shelf_and_four_bins_can_be_built_on_an_empty_database()
    {
        // Eigene Factory: nur so ist die Datenbank garantiert leer (die Klassen-Factory teilen sich die anderen Tests).
        using var emptyFactory = new LagerApiFactory();
        var admin = await emptyFactory.CreateClient().AsReadyAdminAsync();

        // Ausgangslage: nichts vorhanden.
        Assert.Empty(await admin.LayoutAsync());
        Assert.Empty(await admin.BinsAsync());

        var site = await admin.BuildSiteAsync(bins: 4, code: "WH-LEER");

        // Der Baum der API zeigt genau die angelegte Kette (Lager -> Zone -> Gang -> Regal -> 4 Lagerplätze).
        var layout = await admin.LayoutAsync();
        var warehouse = Assert.Single(layout);
        Assert.Equal("WH-LEER", warehouse.Code);
        var zone = Assert.Single(warehouse.Zones);
        Assert.Equal(("Z-A", "Zone A"), (zone.Code, zone.Name));
        var aisle = Assert.Single(zone.Aisles);
        Assert.Equal("A1", aisle.Code);
        var shelf = Assert.Single(aisle.Shelves);
        Assert.Equal(site.Shelf.Id, shelf.Id);
        Assert.Equal(new[] { "S-WH-LEER-01", "S-WH-LEER-02", "S-WH-LEER-03", "S-WH-LEER-04" },
            shelf.Locations.Select(b => b.Code).Order().ToArray());

        // Dieselben Lagerplätze stehen in der flachen Liste (Grundlage von Bestand, Wareneingang, Picken).
        Assert.Equal(4, (await admin.BinsAsync()).Count);
    }

    [Fact]
    public async Task A_new_aisle_gets_sensible_defaults_and_bin_type_and_threshold_can_be_set_on_a_new_bin()
    {
        var admin = await _factory.CreateClient().AsReadyAdminAsync();
        var warehouse = await admin.CreateWarehouseAsync();
        var zone = await admin.CreateZoneAsync(warehouse.Id);

        // Ohne Angaben: Ausrichtung AlongX, Start im Ursprung, Ende 10 m weiter entlang der Ausrichtung.
        var alongX = await admin.CreateAisleAsync(zone.Id, "A-X");
        Assert.Equal(("AlongX", new PositionDto(0, 0, 0), new PositionDto(10_000, 0, 0)), (alongX.Orientation, alongX.StartPosition, alongX.EndPosition));

        var alongY = await (await admin.PostAsJsonAsync("/api/warehouse/aisles",
                new CreateAisleRequest(zone.Id, "A-Y", new PositionDto(500, 1_000, 0), null, "alongy")))
            .ExpectAsync<AisleDto>(HttpStatusCode.Created);
        Assert.Equal(("AlongY", new PositionDto(500, 1_000, 0), new PositionDto(500, 11_000, 0)), (alongY.Orientation, alongY.StartPosition, alongY.EndPosition));

        // Bin-Typ und Nachschub-Schwelle (bestehender Endpunkt) am neu angelegten Lagerplatz.
        var shelf = await admin.CreateShelfAsync(alongX.Id, "S-" + warehouse.Code, bins: 1);
        var bin = Assert.Single(shelf.Locations);
        var updated = await (await admin.PutAsJsonAsync($"/api/warehouse/storage-locations/{bin.Id}/bin-type", new SetBinTypeRequest("HotPick", 12)))
            .ExpectAsync<StorageLocationDto>(HttpStatusCode.OK);
        Assert.Equal((nameof(BinType.HotPick), 12), (updated.BinType, updated.ReplenishmentThreshold));

        var inLayout = (await admin.FindWarehouseAsync(warehouse.Id))!.Zones.Single().Aisles.First(a => a.Id == alongX.Id).Shelves.Single().Locations.Single();
        Assert.Equal((nameof(BinType.HotPick), 12), (inLayout.BinType, inLayout.ReplenishmentThreshold));
    }
}
