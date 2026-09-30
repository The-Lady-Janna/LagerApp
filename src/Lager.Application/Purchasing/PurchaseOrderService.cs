using Lager.Application.Abstractions;
using Lager.Application.Stock;
using Lager.Contracts.Purchasing;
using Lager.Domain.Purchasing;
using Lager.Domain.Suppliers;

namespace Lager.Application.Purchasing;

/// <summary>
/// CRUD + Status-Workflow für PurchaseOrders, plus Bestellvorschläge.
/// Diese Klasse bucht nie Bestand: Wareneingang ist der Single Source of Truth für Stock-Buchungen. Der reguläre
/// Empfang läuft über "Wareneingang aus Bestellung" (InboundService.CreateFromPurchaseOrderAsync): Der Wareneingang
/// verweist auf die Bestellzeilen und schreibt beim Buchen die empfangene Menge fort. <see cref="ReceiveLineAsync"/>
/// ist nur noch die Statuskorrektur ohne Bestandsbuchung.
/// </summary>
public class PurchaseOrderService
{
    private readonly IPurchaseOrderRepository _repo;
    private readonly ISupplierRepository _suppliers;
    private readonly IArticleRepository _articles;
    private readonly IStockRepository _stock;
    private readonly IUnitOfWork _uow;

    public PurchaseOrderService(IPurchaseOrderRepository repo, ISupplierRepository suppliers,
        IArticleRepository articles, IStockRepository stock, IUnitOfWork uow)
    {
        _repo = repo;
        _suppliers = suppliers;
        _articles = articles;
        _stock = stock;
        _uow = uow;
    }

    public async Task<IReadOnlyList<PurchaseOrderDto>> ListAsync(CancellationToken ct = default)
    {
        var pos = await _repo.ListAsync(ct);
        var suppliers = await _suppliers.GetManyAsync(pos.Select(p => p.SupplierId), ct);
        return pos.Select(p => ToDto(p, suppliers.GetValueOrDefault(p.SupplierId))).ToList();
    }

    public async Task<PurchaseOrderDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var po = await _repo.GetWithLinesAsync(id, ct);
        if (po is null) return null;
        var s = await _suppliers.GetAsync(po.SupplierId, ct);
        return ToDto(po, s);
    }

    public async Task<PurchaseOrderDto> CreateAsync(CreatePurchaseOrderRequest req, CancellationToken ct = default)
    {
        var supplier = await _suppliers.GetAsync(req.SupplierId, ct)
            ?? throw StockErrors.NotFound("supplier_not_found", "Lieferant nicht gefunden");
        if (!supplier.IsActive)
            throw StockErrors.Conflict("supplier_inactive", $"Lieferant {supplier.Name} ist deaktiviert - keine neue Bestellung möglich");
        var seq = await _repo.NextSequenceAsync(ct);
        var poNumber = $"PO-{DateTime.UtcNow:yyyyMMdd}-{seq:D5}";

        var po = new PurchaseOrder(poNumber, supplier.Id, supplier.Currency, req.ExpectedDate, req.Notes);

        // Default unit prices to the article's PurchasePriceCents when not provided.
        var articleIds = req.Lines.Select(l => l.ArticleId).Distinct().ToList();
        var articleMap = await _articles.GetManyAsync(articleIds, ct);
        foreach (var line in req.Lines)
        {
            if (!articleMap.TryGetValue(line.ArticleId, out var article))
                throw StockErrors.NotFound("article_not_found", $"Artikel {line.ArticleId} nicht gefunden");
            var unitPrice = line.UnitPriceCents ?? article.PurchasePriceCents;
            po.AddLine(article.Id, article.Sku, line.OrderedQty, unitPrice);
        }

        await _repo.AddAsync(po, ct);
        await _uow.SaveChangesAsync(ct);
        return ToDto(po, supplier);
    }

    public async Task<PurchaseOrderDto?> AddLineAsync(Guid id, AddPurchaseOrderLineRequest req, CancellationToken ct = default)
    {
        var po = await _repo.GetWithLinesAsync(id, ct);
        if (po is null) return null;
        var article = await _articles.GetAsync(req.ArticleId, ct)
            ?? throw StockErrors.NotFound("article_not_found", "Artikel nicht gefunden");
        po.AddLine(article.Id, article.Sku, req.OrderedQty, req.UnitPriceCents ?? article.PurchasePriceCents);
        await _uow.SaveChangesAsync(ct);
        var s = await _suppliers.GetAsync(po.SupplierId, ct);
        return ToDto(po, s);
    }

    public async Task<PurchaseOrderDto?> RemoveLineAsync(Guid id, Guid lineId, CancellationToken ct = default)
    {
        var po = await _repo.GetWithLinesAsync(id, ct);
        if (po is null) return null;
        po.RemoveLine(lineId);
        await _uow.SaveChangesAsync(ct);
        var s = await _suppliers.GetAsync(po.SupplierId, ct);
        return ToDto(po, s);
    }

    public async Task<PurchaseOrderDto?> SendAsync(Guid id, CancellationToken ct = default)
    {
        var po = await _repo.GetWithLinesAsync(id, ct);
        if (po is null) return null;
        po.MarkSent();
        await _uow.SaveChangesAsync(ct);
        var s = await _suppliers.GetAsync(po.SupplierId, ct);
        return ToDto(po, s);
    }

    /// <summary>
    /// Schreibt die empfangene Menge einer Zeile fort (Statuskorrektur) - bucht KEINEN Bestand. Den Bestand bucht der
    /// Wareneingang aus der Bestellung; wer beides tut, stößt an die Übermengen-Prüfung der Zeile.
    /// </summary>
    public async Task<PurchaseOrderDto?> ReceiveLineAsync(Guid id, Guid lineId, ReceivePurchaseOrderLineRequest req, CancellationToken ct = default)
    {
        var po = await _repo.GetWithLinesAsync(id, ct);
        if (po is null) return null;
        po.ReceiveLine(lineId, req.ReceivedQty);
        await _uow.SaveChangesAsync(ct);
        var s = await _suppliers.GetAsync(po.SupplierId, ct);
        return ToDto(po, s);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var po = await _repo.GetWithLinesAsync(id, ct);
        if (po is null) return false;
        po.Cancel();
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Bestellvorschläge: gruppiere alle Artikel, deren Bestand plus bereits bestellte, noch nicht gelieferte Menge
    /// (offene Bestellungen im Status Sent/PartiallyReceived) unter dem ReorderPoint liegt, nach ihrem
    /// PrimarySupplierId. Artikel ohne Lieferant landen in einer "kein Lieferant"-Gruppe. Vorgeschlagene
    /// Bestellmenge = MaxStock - (Bestand + offen) (mindestens ReorderPoint - (Bestand + offen)). Damit erscheint
    /// derselbe Bedarf nach dem Versenden nicht erneut (keine Doppelbestellung).
    /// </summary>
    public async Task<IReadOnlyList<PurchaseSuggestionDto>> SuggestionsAsync(CancellationToken ct = default)
    {
        var allArticles = await _articles.ListAsync(ct);
        var stock = await _stock.ListAllAsync(ct);
        var stockByArticle = stock.GroupBy(s => s.ArticleId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        var suppliers = await _suppliers.ListAsync(includeInactive: false, ct);
        var supplierMap = suppliers.ToDictionary(s => s.Id);

        // Offene Bestellmengen je Artikel (bestellt minus empfangen, nie negativ).
        var openByArticle = (await _repo.ListOpenAsync(ct))
            .SelectMany(po => po.Lines)
            .GroupBy(l => l.ArticleId)
            .ToDictionary(g => g.Key, g => g.Sum(l => (long)l.OpenQuantity()));

        var below = allArticles
            .Select(a => new
            {
                Article = a,
                Current = stockByArticle.GetValueOrDefault(a.Id),
                Open = (int)Math.Min(openByArticle.GetValueOrDefault(a.Id), int.MaxValue),
            })
            .Where(x => x.Article.ReorderPoint > 0 && (long)x.Current + x.Open < x.Article.ReorderPoint)
            .Select(x =>
            {
                var available = x.Current + x.Open;
                var target = x.Article.MaxStock > 0 ? x.Article.MaxStock : x.Article.ReorderPoint * 2;
                var suggested = Math.Max(target - available, x.Article.ReorderPoint - available);
                return new { x.Article, x.Current, x.Open, Suggested = suggested };
            })
            .ToList();

        var grouped = below
            .GroupBy(x => x.Article.PrimarySupplierId)
            .Select(g =>
            {
                Supplier? sup = g.Key.HasValue ? supplierMap.GetValueOrDefault(g.Key.Value) : null;
                return new PurchaseSuggestionDto(
                    g.Key, sup?.Name, sup?.LeadTimeDays ?? 0,
                    g.Select(x => new PurchaseSuggestionLineDto(
                        x.Article.Id, x.Article.Sku, x.Article.Name,
                        x.Current, x.Article.MinStock, x.Article.ReorderPoint, x.Article.MaxStock,
                        x.Suggested, x.Open)).ToList());
            })
            .OrderBy(s => s.SupplierName ?? "ZZZ")
            .ToList();

        return grouped;
    }

    private static PurchaseOrderDto ToDto(PurchaseOrder po, Supplier? supplier) => new(
        po.Id, po.PoNumber, po.SupplierId, supplier?.Name,
        po.Status.ToString(), po.Currency, po.Notes,
        po.CreatedAt, po.SentAt, po.ExpectedDate, po.ReceivedAt,
        po.TotalValueCents(),
        po.Lines.Select(l => new PurchaseOrderLineDto(
            l.Id, l.ArticleId, l.ArticleSku, l.OrderedQty, l.ReceivedQty, l.UnitPriceCents)).ToList());
}
