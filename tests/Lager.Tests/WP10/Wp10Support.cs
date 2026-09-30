using Lager.Application.Reports;
using Lager.Contracts.Reports;
using Lager.Domain.Articles;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP10;

/// <summary>Kleine Baukästen für die Analytics-Tests (Artikel, Bins, Bewegungen, Picklisten).</summary>
internal static class Wp10
{
    public static Article NewArticle(string sku, int lengthMm = 10, int widthMm = 10, int heightMm = 10,
        int weightGrams = 100, int priceCents = 100)
    {
        var a = new Article(sku, "Artikel " + sku, new Dimensions(lengthMm, widthMm, heightMm), weightGrams, StackingInfo.NotStackable);
        a.SetPurchasing(null, priceCents);
        return a;
    }

    public static StorageLocation NewBin(string code, int xMm, int yMm, int widthMm = 600, int depthMm = 600,
        int heightMm = 500, int maxWeightGrams = 0, BinType type = BinType.Standard, Guid? shelfId = null)
    {
        var bin = new StorageLocation(shelfId ?? Guid.NewGuid(), code, new Position(xMm, yMm, 0),
            widthMm, depthMm, heightMm, maxWeightGrams);
        if (type != BinType.Standard) bin.SetBinType(type, 0);
        return bin;
    }

    public static PickedItemSnapshot Pick(Guid articleId, Guid binId, int quantity = 1, DateTime? at = null) =>
        new(Guid.NewGuid(), articleId, binId, quantity, at ?? DateTime.UtcNow);

    public static LedgerMovementSnapshot Move(Guid articleId, DateTime at, int delta, int costCents,
        StockMovementReason reason, Guid? referenceId = null) =>
        new(articleId, at, delta, costCents, reason, referenceId);

    /// <summary>Pickliste mit einer Zeile je Eintrag: (Artikel, Bin, Soll, bestätigt oder null) und dem gewünschten Endstatus.</summary>
    public static PickList NewPickList(string number, PickListStatus status,
        params (Guid ArticleId, Guid BinId, int Planned, int? Confirmed)[] lines)
    {
        var items = lines.Select((l, i) =>
            new PickItem(i + 1, Guid.NewGuid(), Guid.NewGuid(), l.ArticleId, l.BinId, l.Planned)).ToList();
        var pl = new PickList(number, items, 1000);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Confirmed is int c) items[i].ConfirmPacked(c);
        switch (status)
        {
            case PickListStatus.Completed: pl.MarkPacked(); break;
            case PickListStatus.Picked: pl.MarkPickingComplete(); break;
            case PickListStatus.InProgress: pl.Assign("picker"); break;
            case PickListStatus.Pending: break;
            default: throw new ArgumentOutOfRangeException(nameof(status));
        }
        return pl;
    }
}

/// <summary>
/// In-Memory-SQLite mit dem echten EF-Modell. Fremdschlüssel sind aus (es geht um die Abfragen des
/// Berichts-Gateways, nicht um die Stammdatenkette Lager/Zone/Gang/Regal).
/// </summary>
internal sealed class Wp10Db : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=False");

    public LagerDbContext Db { get; }

    public Wp10Db()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<LagerDbContext>().UseSqlite(_connection).Options;
        Db = new LagerDbContext(options);
        Db.Database.EnsureCreated();
    }

    public ReportQueryGateway Gateway => new(Db);

    public async Task SaveAsync(params object[] entities)
    {
        Db.AddRange(entities);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>Legt eine Ledger-Buchung mit frei wählbarem Zeitstempel an (die Domäne stempelt sonst immer "jetzt").</summary>
    public async Task AddMovementAsync(Guid articleId, Guid binId, int delta, int costCents,
        StockMovementReason reason, DateTime at, Guid? referenceId = null)
    {
        var movement = new StockMovement(articleId, binId, delta, costCents, reason,
            referenceId is null ? null : "Test", referenceId);
        Db.StockMovements.Add(movement);
        Db.Entry(movement).Property(m => m.At).CurrentValue = at;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>Legt eine Pickliste mit frei wählbarem "letzter Änderung" an (Basis des Berichtsfensters).</summary>
    public async Task AddPickListAsync(PickList pickList, DateTime updatedAt)
    {
        Db.PickLists.Add(pickList);
        Db.Entry(pickList).Property(p => p.UpdatedAt).CurrentValue = updatedAt;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>Gateway-Attrappe für Service-Tests ohne Datenbank. Alles Nicht-Gesetzte ist leer.</summary>
internal sealed class FakeGateway : IReportQueryGateway
{
    public List<OrderSnapshot> Orders { get; } = new();
    public List<PickListSnapshot> PickLists { get; } = new();
    public List<PickedItemSnapshot> Picked { get; } = new();
    public Dictionary<Guid, (string Sku, string Name)> Names { get; } = new();
    public List<ValuationArticleSnapshot> ValuationArticles { get; } = new();
    public List<LedgerMovementSnapshot> Ledger { get; } = new();
    public Dictionary<Guid, DateTime?> LastMoves { get; } = new();
    public Dictionary<Guid, ArticleStockTotals> Totals { get; } = new();

    public Task<IReadOnlyList<OrderSnapshot>> OrdersInRangeAsync(DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OrderSnapshot>>(Orders);
    public Task<IReadOnlyList<PickListSnapshot>> PickListsInRangeAsync(DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PickListSnapshot>>(PickLists);
    public Task<IReadOnlyList<PickedItemSnapshot>> PickedItemsInRangeAsync(DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PickedItemSnapshot>>(Picked);
    public Task<IReadOnlyList<BinStockSnapshot>> BinStockSummaryAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BinStockSnapshot>>(Array.Empty<BinStockSnapshot>());
    public Task<IReadOnlyDictionary<Guid, (string Sku, string Name)>> ArticleNamesAsync(IEnumerable<Guid> articleIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, (string Sku, string Name)>>(Names);
    public Task<IReadOnlyDictionary<Guid, string>> BinCodesAsync(IEnumerable<Guid> binIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    public Task<IReadOnlyList<BinHeatSnapshot>> BinPickCountsAsync(DateTime from, DateTime to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BinHeatSnapshot>>(Array.Empty<BinHeatSnapshot>());
    public Task<IReadOnlyDictionary<Guid, DateTime?>> ArticleLastMovementAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DateTime?>>(LastMoves);
    public Task<LiveStatusSnapshot> LiveStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<StockMovementEvent>> StockMovementsAsync(Guid articleId, DateTime from, DateTime to, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<(Guid Id, string Sku, string Name, int CurrentQty)?> ArticleStockSummaryAsync(Guid articleId, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<IReadOnlyDictionary<Guid, ArticleStockTotals>> ArticleStockTotalsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ArticleStockTotals>>(Totals);
    public Task<IReadOnlyList<ChargeInboundDto>> ChargeInboundsAsync(string lotNumber, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ChargeStockDto>> ChargeStockAsync(string lotNumber, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ChargeMovementDto>> ChargeMovementsAsync(string lotNumber, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<ValuationArticleSnapshot>> StockValuationBaseAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ValuationArticleSnapshot>>(ValuationArticles);
    public Task<IReadOnlyList<LedgerMovementSnapshot>> ValuationLedgerAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LedgerMovementSnapshot>>(Ledger);
    public Task<IReadOnlyList<PickerActivitySnapshot>> PickerActivityAsync(DateTime from, DateTime to, CancellationToken ct) =>
        throw new NotSupportedException();
}
