using Lager.Application.Abstractions;
using Lager.Application.PickLists;
using Lager.Contracts.Customers;
using Lager.Contracts.Orders;
using Lager.Domain.Articles;
using Lager.Domain.Customers;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;

namespace Lager.Application.Orders;

/// <summary>Ergebnis von <see cref="OrderService.CreateOrGetAsync"/>: die Bestellung und ob sie neu angelegt wurde (sonst eine Wiederholung).</summary>
public sealed record CreateOrderResult(OrderDto Order, bool Created);

/// <summary>
/// Bestellungen: anlegen (manuell und über die externe API), lesen, stornieren. Fachliche Fehler:
/// <see cref="InvalidOperationException"/> = Regelverstoß/Konflikt, <see cref="ArgumentException"/> = ungültige Eingabe;
/// ein maschinenlesbarer Code steht jeweils in <c>exception.Data["code"]</c> (snake_case).
/// </summary>
public class OrderService
{
    /// <summary>Höchstzahl der Positionen einer Bestellung.</summary>
    public const int MaxLines = 500;

    /// <summary>Höchstmenge je Position.</summary>
    public const int MaxQuantity = 100_000;

    private readonly IOrderRepository _orders;
    private readonly IArticleRepository _articles;
    private readonly IStockRepository _stock;
    private readonly ICustomerRepository _customers;
    private readonly IPickWaveRepository _waves;
    private readonly PickListService _pickLists;
    private readonly IUnitOfWork _uow;

    public OrderService(
        IOrderRepository orders, IArticleRepository articles, IStockRepository stock,
        ICustomerRepository customers, IPickWaveRepository waves, PickListService pickLists, IUnitOfWork uow)
    {
        _orders = orders;
        _articles = articles;
        _stock = stock;
        _customers = customers;
        _waves = waves;
        _pickLists = pickLists;
        _uow = uow;
    }

    /// <summary>Wie <see cref="CreateOrGetAsync"/>, liefert aber nur die Bestellung (neu angelegt oder bereits vorhanden).</summary>
    public async Task<OrderDto> CreateAsync(CreateOrderRequest request, OrderSource source, CancellationToken ct = default) =>
        (await CreateOrGetAsync(request, source, ct)).Order;

    /// <summary>
    /// Legt eine Bestellung an - oder liefert sie, wenn dieselbe Anfrage schon einmal ankam (Idempotenz):
    ///  - Die Bestellnummer wird getrimmt geprüft. Existiert sie schon und trägt die Bestellung dieselbe
    ///    <c>ExternalReference</c> wie die Anfrage, ist es eine Wiederholung: die bestehende Bestellung kommt zurück
    ///    (<see cref="CreateOrderResult.Created"/> = false). Sonst <see cref="InvalidOperationException"/> mit
    ///    Code <c>duplicate_order_number</c>.
    ///  - Zeilen nennen den Artikel per Id oder SKU; unbekannte Artikel sind ein Eingabefehler (<c>unknown_article</c>),
    ///    Artikel außerhalb ihres Saison-Fensters (ValidFrom/ValidUntil) ein Konflikt (<c>article_not_orderable</c>).
    ///  - Kunde und Lieferadresse sind optional: der Kunde muss existieren und aktiv sein, die Adresse eine
    ///    Liefer- oder Doppeladresse dieses Kunden (<c>Order.LinkCustomer</c>).
    /// </summary>
    public async Task<CreateOrderResult> CreateOrGetAsync(CreateOrderRequest request, OrderSource source, CancellationToken ct = default)
    {
        var orderNumber = request.OrderNumber?.Trim();
        if (string.IsNullOrEmpty(orderNumber))
            throw Invalid("order_number_required", "Die Bestellnummer darf nicht leer sein");
        if (request.Lines is null || request.Lines.Count == 0)
            throw Invalid("order_without_lines", "Bestellung braucht mindestens eine Position");
        if (request.Lines.Count > MaxLines)
            throw Invalid("too_many_lines", $"Eine Bestellung darf höchstens {MaxLines} Positionen haben");
        if (request.Lines.Any(l => l.Quantity < 1 || l.Quantity > MaxQuantity))
            throw Invalid("invalid_quantity", $"Die Menge je Position muss zwischen 1 und {MaxQuantity} liegen");

        var externalReference = string.IsNullOrWhiteSpace(request.ExternalReference) ? null : request.ExternalReference.Trim();

        var existing = await _orders.GetByNumberAsync(orderNumber, ct);
        if (existing is not null)
        {
            if (externalReference is not null && string.Equals(existing.ExternalReference, externalReference, StringComparison.Ordinal))
                return new CreateOrderResult(await ToDtoAsync(existing, ct), Created: false);
            throw Rule("duplicate_order_number", $"Bestellung '{orderNumber}' existiert bereits");
        }

        var resolved = await ResolveArticlesAsync(request.Lines, ct);
        EnsureOrderable(resolved);
        var customer = await ResolveCustomerAsync(request, ct);

        var lines = request.Lines.Select((l, i) => new OrderLine(resolved[i].Id, l.Quantity));
        var order = new Order(orderNumber, source, request.CustomerReference, lines);
        order.SetPlanning(request.Priority, request.DueDate);
        if (externalReference is not null) order.SetExternalReference(externalReference);
        if (customer is not null) order.LinkCustomer(customer.Id, request.ShippingAddressId);

        await _orders.AddAsync(order, ct);
        await _uow.SaveChangesAsync(ct);

        return new CreateOrderResult(await ToDtoAsync(order, ct), Created: true);
    }

    /// <summary>Löst die Artikel der Zeilen auf (Id und/oder SKU); alle unbekannten werden in EINER Meldung genannt.</summary>
    private async Task<IReadOnlyList<Article>> ResolveArticlesAsync(IReadOnlyList<CreateOrderLineRequest> lines, CancellationToken ct)
    {
        var ids = lines.Where(l => l.ArticleId is Guid id && id != Guid.Empty).Select(l => l.ArticleId!.Value).Distinct().ToList();
        var byId = await _articles.GetManyAsync(ids, ct);

        var bySku = new Dictionary<string, Article?>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in lines.Select(l => l.Sku?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            bySku[sku!] = await _articles.GetBySkuAsync(sku!, ct);

        var unknown = new List<string>();
        var result = new List<Article>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var sku = line.Sku?.Trim();
            var hasId = line.ArticleId is Guid g && g != Guid.Empty;
            var hasSku = !string.IsNullOrEmpty(sku);
            if (!hasId && !hasSku)
                throw Invalid("article_missing", $"Position {i + 1}: Artikel-Id oder SKU fehlt");

            Article? byIdArticle = null, bySkuArticle = null;
            if (hasId && !byId.TryGetValue(line.ArticleId!.Value, out byIdArticle))
                unknown.Add($"Artikel-Id {line.ArticleId}");
            if (hasSku) bySku.TryGetValue(sku!, out bySkuArticle);
            if (hasSku && bySkuArticle is null)
                unknown.Add($"SKU {sku}");
            if (byIdArticle is not null && bySkuArticle is not null && byIdArticle.Id != bySkuArticle.Id)
                throw Invalid("article_mismatch", $"Position {i + 1}: Artikel-Id und SKU {sku} meinen verschiedene Artikel");

            var article = byIdArticle ?? bySkuArticle;
            if (article is not null) result.Add(article);
        }

        if (unknown.Count > 0)
            throw Invalid("unknown_article", "Unbekannte Artikel: " + string.Join(", ", unknown.Distinct()));
        return result;
    }

    /// <summary>Artikel außerhalb ihres Saison-Fensters (ValidFrom/ValidUntil) sind nicht bestellbar.</summary>
    private static void EnsureOrderable(IEnumerable<Article> articles)
    {
        var now = DateTime.UtcNow;
        var blocked = articles.Where(a => !a.IsCurrentlyActive(now)).DistinctBy(a => a.Id).ToList();
        if (blocked.Count == 0) return;

        static string Day(DateTime? d) => d?.ToString("dd.MM.yyyy") ?? "offen";
        throw Rule("article_not_orderable",
            "Nicht bestellbar (außerhalb des Saison-Fensters): " +
            string.Join(", ", blocked.Select(a => $"{a.Sku} (gültig {Day(a.ValidFrom)} bis {Day(a.ValidUntil)})")));
    }

    /// <summary>Kunde und Lieferadresse der Anfrage prüfen; ohne Kunde null.</summary>
    private async Task<Customer?> ResolveCustomerAsync(CreateOrderRequest request, CancellationToken ct)
    {
        if (request.CustomerId is null)
        {
            if (request.ShippingAddressId is not null)
                throw Invalid("shipping_address_requires_customer", "Eine Lieferadresse setzt einen Kunden voraus");
            return null;
        }

        var customerId = request.CustomerId.Value;
        var customer = (await _customers.GetManyAsync(new[] { customerId }, ct)).GetValueOrDefault(customerId)
            ?? throw Invalid("unknown_customer", "Kunde nicht gefunden");
        if (!customer.IsActive)
            throw Rule("customer_inactive", $"Kunde {customer.Name} ist deaktiviert - für ihn lassen sich keine Bestellungen anlegen");

        if (request.ShippingAddressId is Guid addressId)
        {
            var address = customer.Addresses.FirstOrDefault(a => a.Id == addressId)
                ?? throw Invalid("address_not_of_customer", "Die Lieferadresse gehört nicht zu diesem Kunden");
            if (address.Kind == AddressKind.Billing)
                throw Invalid("address_not_shipping", "Die gewählte Adresse ist eine reine Rechnungsadresse und kann nicht als Lieferadresse dienen");
        }

        return customer;
    }

    public async Task<OrderDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var order = await _orders.GetAsync(id, ct);
        return order is null ? null : await ToDtoAsync(order, ct);
    }

    /// <summary>
    /// Storniert die Bestellung (nur aus New, Picking oder Picked): dort ist noch nichts gebucht, es gibt also keinen
    /// Bestandseffekt. Steht sie schon auf einer Pickliste (Picking/Picked), werden ihre Positionen aus den aktiven
    /// Listen entfernt; eine Liste ohne Positionen wird storniert. Aus einer noch offenen Welle fliegt die Bestellung
    /// heraus. Aus Packed und Shipped (Bestand gebucht) sowie aus Cancelled wirft die Methode
    /// <see cref="InvalidOperationException"/> mit dem Code <c>order_not_cancellable</c>; unbekannte Id: null.
    /// Bestellung, Picklisten und Wellen landen in EINEM SaveChanges.
    /// </summary>
    public async Task<OrderDto?> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var order = await _orders.GetAsync(id, ct);
        if (order is null) return null;

        var onPickList = order.Status is OrderStatus.Picking or OrderStatus.Picked;
        order.Cancel();   // Guard: wirft, bevor irgendetwas anderes angefasst wird

        if (onPickList)
            await _pickLists.RemoveOrderFromActiveListsAsync(order.Id, ct);

        foreach (var wave in await _waves.ListAsync(ct))
        {
            if (wave.Status != PickWaveStatus.Open || !wave.OrderIds.Contains(order.Id)) continue;
            (await _waves.GetAsync(wave.Id, ct))?.RemoveOrder(order.Id);
        }

        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(order, ct);
    }

    /// <summary>
    /// Reihenfolge, in der Bestellungen kommissioniert werden: Priorität absteigend, Fälligkeit aufsteigend (ohne
    /// Termin zuletzt), dann Eingang (FIFO) und Nummer. Gilt für das Wagen-Füllen und die Bestandsampel.
    /// </summary>
    internal static IOrderedEnumerable<Order> InPickingOrder(IEnumerable<Order> orders) =>
        orders
            .OrderByDescending(o => o.Priority)
            .ThenBy(o => o.DueDate ?? DateTime.MaxValue)
            .ThenBy(o => o.CreatedAt)
            .ThenBy(o => o.OrderNumber, StringComparer.Ordinal);

    /// <summary>
    /// Lists all orders with two stock indicators per order (physischer Bedarf: Bundles sind in ihre Komponenten
    /// aufgelöst, wie beim Kommissionieren):
    /// <list type="bullet">
    ///   <item><c>HasStockNow</c> — would this order be pickable in isolation?</item>
    ///   <item><c>HasStockAfterFifo</c> — after reserving stock for the orders that are already being picked
    ///   (Picking/Picked: zugeteilt, aber noch nicht abgebucht) and for all New orders ahead of this one in
    ///   <see cref="InPickingOrder"/>, is there still enough left? This is what the dashboard uses to surface
    ///   orders that will fall out due to contention with more urgent or older requests.</item>
    /// </list>
    /// Verpackte, versendete und stornierte Bestellungen konkurrieren nicht mehr um Bestand: beide Werte true.
    /// </summary>
    public async Task<IReadOnlyList<OrderDto>> ListAsync(CancellationToken ct = default)
    {
        var items = await _orders.ListAsync(ct);
        if (items.Count == 0) return Array.Empty<OrderDto>();

        var lineArticleIds = items.SelectMany(o => o.Lines).Select(l => l.ArticleId).Distinct().ToList();
        var articleMap = await PickListService.LoadArticlesWithComponentsAsync(_articles, lineArticleIds, ct);
        var skuMap = articleMap.ToDictionary(kv => kv.Key, kv => kv.Value.Sku);

        // Physischer Bedarf je Bestellung (Artikel -> Menge); null = Bundle nicht auflösbar (zyklisch/zu tief) -> nie pickbar.
        var demand = items.ToDictionary(o => o.Id, o => PhysicalDemand(o, articleMap));
        var physicalIds = demand.Values.Where(d => d is not null).SelectMany(d => d!.Keys).Distinct().ToList();

        // Initial stock per article (sum over all bins) — one batched query.
        var sums = await _stock.SumQuantitiesByArticleAsync(physicalIds, ct);
        var stockMap = physicalIds.ToDictionary(id => id, id => (long)sums.GetValueOrDefault(id, 0));

        bool Covers(IReadOnlyDictionary<Guid, long> stock, Dictionary<Guid, long>? need) =>
            need is not null && need.All(kv => stock.GetValueOrDefault(kv.Key, 0) >= kv.Value);

        // hasStockNow: pure pre-reservation check, independent of order age.
        var hasStockNow = items.ToDictionary(o => o.Id, o => Covers(stockMap, demand[o.Id]));

        // hasStockAfterFifo: erst die schon zugeteilten (Picking/Picked), dann die neuen in Kommissionier-Reihenfolge.
        var reserved = new Dictionary<Guid, long>(stockMap);
        void Take(Dictionary<Guid, long>? need)
        {
            if (need is null) return;
            foreach (var (articleId, quantity) in need)
                reserved[articleId] = Math.Max(0, reserved.GetValueOrDefault(articleId, 0) - quantity);
        }

        var hasStockAfterFifo = new Dictionary<Guid, bool>();
        foreach (var o in items.Where(o => o.Status is OrderStatus.Picking or OrderStatus.Picked))
        {
            Take(demand[o.Id]);
            hasStockAfterFifo[o.Id] = hasStockNow[o.Id];
        }
        foreach (var o in InPickingOrder(items.Where(o => o.Status == OrderStatus.New)))
        {
            var canPick = Covers(reserved, demand[o.Id]);
            hasStockAfterFifo[o.Id] = canPick;
            if (canPick) Take(demand[o.Id]);
        }
        // Packed/Shipped/Cancelled: nichts mehr zu bepicken, also nichts zu warnen.
        foreach (var o in items.Where(o => o.Status is OrderStatus.Packed or OrderStatus.Shipped or OrderStatus.Cancelled))
        {
            hasStockNow[o.Id] = true;
            hasStockAfterFifo[o.Id] = true;
        }

        var customers = await LoadCustomersAsync(items, ct);
        return items.Select(o => ToDto(o, skuMap, customers, hasStockNow[o.Id], hasStockAfterFifo[o.Id])).ToList();
    }

    /// <summary>Physischer Bedarf (Bundles in Komponenten aufgelöst, gleiche Artikel summiert); null, wenn ein Bundle nicht auflösbar ist.</summary>
    private static Dictionary<Guid, long>? PhysicalDemand(Order order, IReadOnlyDictionary<Guid, Article> articles)
    {
        try
        {
            var need = new Dictionary<Guid, long>();
            foreach (var request in PickListService.ExpandOrder(order, articles))
                need[request.ArticleId] = need.GetValueOrDefault(request.ArticleId) + request.Quantity;
            return need;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<Guid, Customer>> LoadCustomersAsync(IEnumerable<Order> orders, CancellationToken ct)
    {
        var ids = orders.Where(o => o.CustomerId.HasValue).Select(o => o.CustomerId!.Value).Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<Guid, Customer>() : await _customers.GetManyAsync(ids, ct);
    }

    private async Task<OrderDto> ToDtoAsync(Order order, CancellationToken ct)
    {
        var articleMap = await _articles.GetManyAsync(order.Lines.Select(l => l.ArticleId).Distinct().ToList(), ct);
        var customers = await LoadCustomersAsync(new[] { order }, ct);
        return ToDto(order, articleMap.ToDictionary(kv => kv.Key, kv => kv.Value.Sku), customers);
    }

    private static OrderDto ToDto(
        Order o, IReadOnlyDictionary<Guid, string> skuMap, IReadOnlyDictionary<Guid, Customer> customers,
        bool hasStockNow = true, bool hasStockAfterFifo = true)
    {
        var customer = o.CustomerId is Guid customerId ? customers.GetValueOrDefault(customerId) : null;
        var address = customer is not null && o.ShippingAddressId is Guid addressId
            ? customer.Addresses.FirstOrDefault(a => a.Id == addressId)
            : null;

        return new OrderDto(
            o.Id,
            o.OrderNumber,
            o.CustomerReference,
            o.Status.ToString(),
            o.Source.ToString(),
            o.CreatedAt,
            o.Lines.Select(l => new OrderLineDto(l.Id, l.ArticleId, skuMap.GetValueOrDefault(l.ArticleId, "?"), l.Quantity)).ToList(),
            hasStockNow,
            hasStockAfterFifo,
            o.CustomerId,
            customer?.Name,
            o.ShippingAddressId,
            address is null ? null : new CustomerAddressDto(
                address.Id, address.Kind.ToString(), address.Label, address.Street, address.Street2,
                address.Zip, address.City, address.Country),
            o.Priority,
            o.DueDate,
            o.ExternalReference);
    }

    private static InvalidOperationException Rule(string code, string message)
    {
        var ex = new InvalidOperationException(message);
        ex.Data["code"] = code;
        return ex;
    }

    private static ArgumentException Invalid(string code, string message)
    {
        var ex = new ArgumentException(message);
        ex.Data["code"] = code;
        return ex;
    }
}
