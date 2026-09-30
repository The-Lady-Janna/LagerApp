using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Auth;
using Lager.Domain.Articles;
using Lager.Domain.Stock;
using Lager.Domain.Suppliers;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Tests.Infrastructure;

/// <summary>
/// Baut die Testwelt für Workflow-Tests: ein Lager (Lager, Zone, Gang, Regal) mit mehreren Lagerplätzen - Standard, Hot-Pick
/// und Reserve -, Artikel, Lieferanten, Altbestand und eingeloggte Clients je Rolle. Die Stammdaten entstehen direkt über den
/// <see cref="LagerDbContext"/> der Factory (die API hat dafür keine Endpunkte; die Factory braucht kein <c>Seed=true</c>),
/// die Geschäftsvorgänge laufen danach über HTTP oder die Services.
///
/// Eine Instanz gehört zu genau einer Factory: <see cref="LagerApiFactory"/> oder eine eigene
/// <c>WebApplicationFactory&lt;Program&gt;</c> (z. B. mit fester Datenbankdatei für Neustart-Tests). Voraussetzung ist der
/// Bootstrap-Admin aus <see cref="LagerApiFactory"/> (<c>admin</c> mit <see cref="LagerApiFactory.AdminPassword"/>).
/// Alle Namen sind eindeutig (<see cref="Unique"/>): mehrere Tests dürfen sich eine Factory und ein Lager teilen, ohne sich
/// zu stören. Tests, die globalen Zustand verändern (alle Picklisten löschen, Inventur ohne Platzfilter), bekommen eine eigene Factory.
/// </summary>
public sealed class WorldBuilder
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private readonly Dictionary<string, HttpClient> _clients = new();

    public WorldBuilder(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>Lager samt Regal; <see cref="ShelfId"/> ist der Anker für neue Lagerplätze.</summary>
    public sealed record Site(Guid WarehouseId, string Code, Guid ShelfId);

    public sealed record Bin(Guid Id, string Code, BinType Type);

    public sealed record ArticleRef(Guid Id, string Sku);

    /// <summary>Das fertige Standard-Lager: zwei Standard-Plätze, ein Hot-Pick-Platz (Schwelle 10) und ein Reserve-Platz.</summary>
    public sealed record World(Site Site, Bin PickA, Bin PickB, Bin HotPick, Bin Reserve);

    public const int DefaultReplenishmentThreshold = 10;

    public IServiceProvider Services => _factory.Services;

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Ein Datum relativ zu heute (UTC): die Tests hängen nicht an einem festen Kalendertag.</summary>
    public static DateTime InDays(int days) => DateTime.UtcNow.Date.AddDays(days);

    // ---- Zugriff --------------------------------------------------------------------------------------------------

    /// <summary>Führt Code mit einem DbContext in frischem Scope aus (wie ein Request: mit Audit-Interceptor und Pragmas).</summary>
    public async Task<T> DbAsync<T>(Func<LagerDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<LagerDbContext>());
    }

    public Task DbAsync(Func<LagerDbContext, Task> action) =>
        DbAsync<int>(async db => { await action(db); return 0; });

    /// <summary>Ein Dienst-Aufruf in frischem Scope.</summary>
    public async Task<T> WithAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    // ---- Clients --------------------------------------------------------------------------------------------------

    /// <summary>Der als einsatzbereiter Admin eingeloggte Client (für alle Aufrufe derselben Welt derselbe).</summary>
    public Task<HttpClient> AdminAsync() => ClientAsync("Admin");

    /// <summary>
    /// Ein eingeloggter Client mit genau diesen Rollen; je Rollenkombination wird er einmal angelegt und danach wiederverwendet
    /// (das Anlegen kostet BCrypt-Zeit). "Admin" liefert den Bootstrap-Admin (Passwortwechsel bereits erledigt); jede andere
    /// Kombination ist ein frisch angelegter Nutzer ohne Passwortwechsel-Zwang (Passwort
    /// <see cref="AuthTestExtensions.RoleUserPassword"/>), Rollennamen wie in <c>Lager.Domain.Auth.Role</c>.
    /// </summary>
    public async Task<HttpClient> ClientAsync(params string[] roles)
    {
        var key = string.Join("+", roles.Order(StringComparer.Ordinal));
        await _clientGate.WaitAsync();
        try
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;
            if (!_clients.TryGetValue("Admin", out var admin))
                admin = _clients["Admin"] = await _factory.CreateClient().AsReadyAdminAsync();
            if (key == "Admin") return admin;

            var username = Unique("user");
            var created = await admin.PostAsJsonAsync("/api/users",
                new CreateUserRequest(username, AuthTestExtensions.RoleUserPassword, roles, MustChangePassword: false));
            if (created.StatusCode != HttpStatusCode.Created)
                throw new InvalidOperationException(
                    $"Nutzer mit Rollen [{string.Join(", ", roles)}] konnte nicht angelegt werden: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");

            var client = await _factory.CreateClient().LoginAsync(username, AuthTestExtensions.RoleUserPassword);
            _clients[key] = client;
            return client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    /// <summary>Ein eigener, anonymer Client (ohne Token).</summary>
    public HttpClient Anonymous() => _factory.CreateClient();

    // ---- Stammdaten -----------------------------------------------------------------------------------------------

    /// <summary>Ein komplettes Lager mit Zone, Gang und Regal (ohne Lagerplätze).</summary>
    public Task<Site> AddSiteAsync(string? code = null) => DbAsync(async db =>
    {
        code ??= Unique("WH");
        var warehouse = new WarehouseEntity(code, code);
        var zone = new Zone(warehouse.Id, "Z-" + code, "Zone", Position.Origin);
        var aisle = new Aisle(zone.Id, "A-" + code, Position.Origin, new Position(10_000, 0, 0), AisleOrientation.AlongX);
        var shelf = new Shelf(aisle.Id, "S-" + code, new Position(0, 200, 0), 8_000, 600, 2_000);
        db.Warehouses.Add(warehouse);
        db.Zones.Add(zone);
        db.Aisles.Add(aisle);
        db.Shelves.Add(shelf);
        await db.SaveChangesAsync();
        return new Site(warehouse.Id, code, shelf.Id);
    });

    /// <summary>Ein Lagerplatz im Regal. Hot-Pick-Plätze brauchen eine Nachschub-Schwelle, sonst ignoriert sie der Scan.</summary>
    public Task<Bin> AddBinAsync(Site site, BinType type = BinType.Standard, int replenishmentThreshold = 0, int x = 1_000, string? code = null) =>
        DbAsync(async db =>
        {
            code ??= Unique("BIN");
            var bin = new StorageLocation(site.ShelfId, code, new Position(x, 200, 500), 600, 600, 500, 50_000);
            if (type != BinType.Standard || replenishmentThreshold > 0) bin.SetBinType(type, replenishmentThreshold);
            db.StorageLocations.Add(bin);
            await db.SaveChangesAsync();
            return new Bin(bin.Id, code, type);
        });

    /// <summary>Das Standard-Lager: <c>PickA</c> und <c>PickB</c> (Standard), <c>HotPick</c> (Schwelle 10) und <c>Reserve</c>.</summary>
    public async Task<World> BuildAsync()
    {
        var site = await AddSiteAsync();
        var pickA = await AddBinAsync(site, x: 1_000);
        var pickB = await AddBinAsync(site, x: 2_500);
        var hot = await AddBinAsync(site, BinType.HotPick, DefaultReplenishmentThreshold, x: 4_000);
        var reserve = await AddBinAsync(site, BinType.Reserve, x: 5_500);
        return new World(site, pickA, pickB, hot, reserve);
    }

    public Task<ArticleRef> AddArticleAsync(string? sku = null, int weightGrams = 100, int priceCents = 0, Guid? supplierId = null,
        int reorderPoint = 0, int maxStock = 0) => DbAsync(async db =>
    {
        sku ??= Unique("SKU");
        var article = new Article(sku, "Artikel " + sku, new Dimensions(100, 100, 100), weightGrams, StackingInfo.NotStackable);
        if (priceCents > 0 || supplierId is not null) article.SetPurchasing(supplierId, priceCents);
        if (reorderPoint > 0) article.SetStockThresholds(0, reorderPoint, maxStock);
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return new ArticleRef(article.Id, sku);
    });

    public Task<Guid> AddSupplierAsync() => DbAsync(async db =>
    {
        var supplier = new Supplier(Unique("SUP"), "Lieferant");
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    });

    /// <summary>
    /// Altbestand: legt eine Bestandszeile direkt an, OHNE Movement. Wer die Ledger-Invariante prüft, muss deshalb den
    /// Bestand über einen Wareneingang aufbauen (HTTP) - sonst fehlt die Buchung im Ledger.
    /// </summary>
    public Task<Guid> AddLegacyStockAsync(Guid articleId, Bin bin, int quantity, string? lot = null, DateTime? expiry = null) =>
        DbAsync(async db =>
        {
            var stock = new StockItem(articleId, bin.Id, quantity, lot, expiry);
            db.StockItems.Add(stock);
            await db.SaveChangesAsync();
            return stock.Id;
        });

    // ---- Zustand lesen --------------------------------------------------------------------------------------------

    public Task<List<StockItem>> StockRowsAsync(Guid articleId) =>
        DbAsync(db => db.StockItems.AsNoTracking().Where(s => s.ArticleId == articleId).ToListAsync());

    public Task<int> QuantityAsync(Guid articleId) =>
        DbAsync(db => db.StockItems.Where(s => s.ArticleId == articleId).SumAsync(s => s.Quantity));

    public Task<int> QuantityAsync(Guid articleId, Bin bin) =>
        DbAsync(db => db.StockItems.Where(s => s.ArticleId == articleId && s.StorageLocationId == bin.Id).SumAsync(s => s.Quantity));

    public Task<List<StockMovement>> MovementsAsync(Guid articleId) =>
        DbAsync(async db => (await db.StockMovements.AsNoTracking().Where(m => m.ArticleId == articleId).ToListAsync())
            .OrderBy(m => m.At).ThenByDescending(m => m.QuantityDelta).ToList());

    /// <summary>
    /// Prüft die Bestandsinvarianten (für einen Artikel oder alle) und liefert die Verstöße als Text - leer heißt konsistent:
    /// <list type="bullet">
    /// <item>je (Artikel, Lagerplatz, Charge) ist die Summe der Movement-Deltas gleich der Menge der Bestandszeile (ohne Zeile: 0),
    /// also auch je Artikel Summe(Deltas) = Summe(Mengen);</item>
    /// <item>keine negative Bestandsmenge;</item>
    /// <item>keine Bestandszeile doppelt je (Artikel, Lagerplatz, Charge);</item>
    /// <item>keine Movement mit Delta 0.</item>
    /// </list>
    /// Voraussetzung: der Bestand wurde ausschließlich über gebuchte Vorgänge aufgebaut (kein <see cref="AddLegacyStockAsync"/>).
    /// </summary>
    public Task<IReadOnlyList<string>> LedgerViolationsAsync(Guid? articleId = null) => DbAsync(async db =>
    {
        var rows = await db.StockItems.AsNoTracking().Where(s => articleId == null || s.ArticleId == articleId).ToListAsync();
        var movements = await db.StockMovements.AsNoTracking().Where(m => articleId == null || m.ArticleId == articleId).ToListAsync();
        var violations = new List<string>();

        foreach (var row in rows.Where(r => r.Quantity < 0))
            violations.Add($"Negativer Bestand: Artikel {row.ArticleId}, Platz {row.StorageLocationId}, Charge {row.LotNumber ?? "-"}: {row.Quantity}");

        foreach (var group in rows.GroupBy(r => (r.ArticleId, r.StorageLocationId, Lot: StockItem.NormalizeLot(r.LotNumber))).Where(g => g.Count() > 1))
            violations.Add($"Bestandszeile doppelt: Artikel {group.Key.ArticleId}, Platz {group.Key.StorageLocationId}, Charge {group.Key.Lot ?? "-"} ({group.Count()}x)");

        foreach (var movement in movements.Where(m => m.QuantityDelta == 0))
            violations.Add($"Movement ohne Menge: {movement.Id}");

        var deltas = movements
            .GroupBy(m => (m.ArticleId, m.BinId, Lot: StockItem.NormalizeLot(m.LotNumber)))
            .ToDictionary(g => g.Key, g => g.Sum(m => (long)m.QuantityDelta));
        var quantities = rows
            .GroupBy(r => (r.ArticleId, r.StorageLocationId, Lot: StockItem.NormalizeLot(r.LotNumber)))
            .ToDictionary(g => g.Key, g => g.Sum(r => (long)r.Quantity));
        foreach (var key in deltas.Keys.Union(quantities.Keys))
        {
            var delta = deltas.GetValueOrDefault(key);
            var quantity = quantities.GetValueOrDefault(key);
            if (delta != quantity)
                violations.Add($"Ledger passt nicht zum Bestand: Artikel {key.ArticleId}, Platz {key.Item2}, Charge {key.Lot ?? "-"}: Movements {delta}, Bestand {quantity}");
        }

        foreach (var perArticle in movements.GroupBy(m => m.ArticleId))
        {
            var sumDeltas = perArticle.Sum(m => (long)m.QuantityDelta);
            var sumRows = rows.Where(r => r.ArticleId == perArticle.Key).Sum(r => (long)r.Quantity);
            if (sumDeltas != sumRows)
                violations.Add($"Summe je Artikel {perArticle.Key}: Movements {sumDeltas}, Bestand {sumRows}");
        }

        return (IReadOnlyList<string>)violations;
    });
}
