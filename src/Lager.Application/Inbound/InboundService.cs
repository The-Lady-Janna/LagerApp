using Lager.Application.Abstractions;
using Lager.Application.Stock;
using Lager.Contracts.Inbound;
using Lager.Contracts.Purchasing;
using Lager.Domain.Inbound;
using Lager.Domain.Purchasing;
using Lager.Domain.Stock;

namespace Lager.Application.Inbound;

public class InboundService
{
    private readonly IInboundRepository _inbound;
    private readonly IArticleRepository _articles;
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IWarehouseRepository _warehouse;
    private readonly IPurchaseOrderRepository _purchaseOrders;
    private readonly IUnitOfWork _uow;

    public InboundService(IInboundRepository inbound, IArticleRepository articles, IStockRepository stock,
        IStockMovementRepository movements, IWarehouseRepository warehouse, IPurchaseOrderRepository purchaseOrders,
        IUnitOfWork uow)
    {
        _inbound = inbound;
        _articles = articles;
        _stock = stock;
        _movements = movements;
        _warehouse = warehouse;
        _purchaseOrders = purchaseOrders;
        _uow = uow;
    }

    public async Task<IReadOnlyList<InboundShipmentDto>> ListAsync(CancellationToken ct = default)
    {
        var items = await _inbound.ListAsync(ct);
        return await MapAsync(items, ct);
    }

    public async Task<InboundShipmentDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var s = await _inbound.GetWithLinesAsync(id, ct);
        if (s is null) return null;
        return (await MapAsync(new[] { s }, ct))[0];
    }

    public async Task<InboundShipmentDto> CreateAsync(CreateInboundShipmentRequest request, CancellationToken ct = default)
    {
        // Nummer vor dem Nachschlagen normalisieren (getrimmt): die Domain speichert sie getrimmt.
        var number = (request.ShipmentNumber ?? string.Empty).Trim();
        if (number.Length == 0)
            throw StockErrors.Invalid("shipment_number_required", "Lieferschein-Nummer erforderlich", nameof(request));
        var existing = await _inbound.GetByNumberAsync(number, ct);
        if (existing is not null)
            throw StockErrors.Conflict("shipment_number_exists", $"Lieferschein '{number}' existiert bereits");

        var shipment = new InboundShipment(number, request.SupplierReference, request.Notes);

        // Zeilen im Request: erst ALLE prüfen und an die (noch nicht gespeicherte) Lieferung hängen, dann Kopf und Zeilen
        // mit EINEM SaveChanges anlegen - eine fehlerhafte Zeile lässt keine halbe Lieferung und keine stillen Verluste zurück.
        if (request.Lines is { Count: > 0 } lines)
            await AddLinesAsync(shipment, lines, ct);

        await _inbound.AddAsync(shipment, ct);
        await _uow.SaveChangesAsync(ct);

        return (await MapAsync(new[] { shipment }, ct))[0];
    }

    /// <summary>Höchstzahl Zeilen beim Anlegen (wie die Listengrenze der Validatoren für Vorgänge).</summary>
    private const int MaxCreateLines = 500;

    /// <summary>
    /// Prüft die Zeilen einer neuen Lieferung wie <see cref="AddLineAsync"/> (Artikel/Lagerplatz vorhanden, Domain-Regeln von
    /// <see cref="InboundShipment.AddLine"/>) und hängt sie an. Fehlermeldungen nennen die Zeilennummer (1-basiert).
    /// </summary>
    private async Task AddLinesAsync(InboundShipment shipment, IReadOnlyList<AddInboundLineRequest> lines, CancellationToken ct)
    {
        if (lines.Count > MaxCreateLines)
            throw StockErrors.Invalid("too_many_lines", $"Höchstens {MaxCreateLines} Zeilen je Lieferung", nameof(lines));
        var empty = lines.Select((l, i) => (Line: l, No: i + 1)).FirstOrDefault(x => x.Line is null);
        if (empty.No > 0)
            throw StockErrors.Invalid("line_invalid", $"Zeile {empty.No}: leer", nameof(lines));

        var articleMap = await _articles.GetManyAsync(lines.Select(l => l.ArticleId).Distinct().ToList(), ct);
        var binMap = await _warehouse.GetStorageLocationsAsync(lines.Select(l => l.TargetBinId).Distinct().ToList(), ct);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var no = i + 1;
            if (line.PurchaseOrderLineId is not null)
                throw StockErrors.Conflict("inbound_without_po",
                    $"Zeile {no}: Die Lieferung gehört zu keiner Bestellung - eine Bestellzeile kann nicht zugeordnet werden");
            if (line.ExpiryDate is DateTime expiry && expiry.Year < 2000)
                throw StockErrors.Invalid("expiry_invalid", $"Zeile {no}: MHD muss nach dem Jahr 2000 liegen", nameof(lines));

            var article = articleMap.GetValueOrDefault(line.ArticleId)
                ?? throw StockErrors.NotFound("article_not_found", $"Zeile {no}: Artikel nicht gefunden");
            var bin = binMap.GetValueOrDefault(line.TargetBinId)
                ?? throw StockErrors.NotFound("bin_not_found", $"Zeile {no}: Lagerplatz nicht gefunden");

            try
            {
                shipment.AddLine(article.Id, bin.Id, line.Quantity, line.LotNumber, line.ExpiryDate, null, line.UnitCostCents);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Die Domain nennt die Zeile nicht: Meldung um die Zeilennummer ergänzen, Typ und Code bleiben (400/409).
                throw WithLinePrefix(ex, no);
            }
        }
    }

    /// <summary>Dieselbe Art von Fehler mit vorangestellter Zeilennummer; der maschinenlesbare Code bleibt erhalten.</summary>
    private static Exception WithLinePrefix(Exception ex, int lineNumber)
    {
        // ArgumentException hängt " (Parameter 'x')" an die Message; die Abbildung der API schneidet das ab.
        var message = $"Zeile {lineNumber}: {ex.Message}";
        Exception wrapped = ex is ArgumentException
            ? new ArgumentException(message)
            : new InvalidOperationException(message);
        foreach (System.Collections.DictionaryEntry entry in ex.Data) wrapped.Data[entry.Key] = entry.Value;
        return wrapped;
    }

    /// <summary>
    /// Legt aus den offenen Mengen einer versendeten (oder teilweise gelieferten) Bestellung einen Wareneingang im
    /// Entwurf an: je Bestellzeile mit Restmenge eine Zeile (Menge = Restmenge, Preis = Bestellpreis) mit Verweis auf
    /// die Bestellzeile, alle im Ziel-Lagerplatz. Charge und MHD ergänzt der Empfänger vor dem Buchen. Pro Bestellung
    /// gibt es höchstens einen offenen Wareneingang - ein zweiter Aufruf (z. B. Doppelklick) wirft einen Konflikt,
    /// statt die offene Menge doppelt einzuplanen. Bestellung nicht gefunden = null.
    /// </summary>
    public async Task<InboundShipmentDto?> CreateFromPurchaseOrderAsync(Guid purchaseOrderId,
        CreateInboundFromPurchaseOrderRequest request, CancellationToken ct = default)
    {
        var po = await _purchaseOrders.GetWithLinesAsync(purchaseOrderId, ct);
        if (po is null) return null;

        if (!po.IsOpen)
            throw StockErrors.Conflict("po_not_receivable",
                $"Bestellung {po.PoNumber} erwartet keine Ware (Status {po.Status}) - nur versendete oder teilweise gelieferte Bestellungen haben offene Mengen");

        var draft = await _inbound.FindDraftForPurchaseOrderAsync(po.Id, ct);
        if (draft is not null)
            throw StockErrors.Conflict("inbound_draft_exists",
                $"Zu Bestellung {po.PoNumber} gibt es bereits einen offenen Wareneingang ({draft.ShipmentNumber}) - diesen buchen oder stornieren");

        var bin = await _warehouse.GetStorageLocationAsync(request.TargetBinId, ct)
            ?? throw StockErrors.NotFound("bin_not_found", "Lagerplatz nicht gefunden");

        var open = po.Lines.Where(l => l.OpenQuantity() > 0).ToList();
        if (open.Count == 0)
            throw StockErrors.Conflict("po_nothing_open", $"Bestellung {po.PoNumber} hat keine offenen Mengen");

        var number = await NextShipmentNumberAsync(po.PoNumber, ct);
        var shipment = new InboundShipment(number, po.PoNumber, request.Notes, po.Id);
        foreach (var line in open)
            shipment.AddLine(line.ArticleId, bin.Id, Math.Min(line.OpenQuantity(), InboundShipment.MaxQuantity),
                lotNumber: null, expiryDate: null, purchaseOrderLineId: line.Id, unitCostCents: line.UnitPriceCents);

        await _inbound.AddAsync(shipment, ct);
        await _uow.SaveChangesAsync(ct);
        return (await MapAsync(new[] { shipment }, ct))[0];
    }

    public async Task<InboundShipmentDto?> AddLineAsync(Guid shipmentId, AddInboundLineRequest request, CancellationToken ct = default)
    {
        var shipment = await _inbound.GetWithLinesAsync(shipmentId, ct);
        if (shipment is null) return null;

        var article = await _articles.GetAsync(request.ArticleId, ct)
            ?? throw StockErrors.NotFound("article_not_found", "Artikel nicht gefunden");
        var bin = await _warehouse.GetStorageLocationAsync(request.TargetBinId, ct)
            ?? throw StockErrors.NotFound("bin_not_found", "Lagerplatz nicht gefunden");

        // Bestellzeile: gehört zur Bestellung der Lieferung und zum selben Artikel; der Preis kommt (ohne eigene Angabe) von dort.
        var unitCost = request.UnitCostCents;
        if (request.PurchaseOrderLineId is Guid poLineId)
        {
            if (shipment.PurchaseOrderId is not Guid poId)
                throw StockErrors.Conflict("inbound_without_po", "Die Lieferung gehört zu keiner Bestellung - eine Bestellzeile kann nicht zugeordnet werden");
            var po = await _purchaseOrders.GetWithLinesAsync(poId, ct)
                ?? throw StockErrors.NotFound("po_not_found", "Bestellung nicht gefunden");
            var poLine = po.Lines.FirstOrDefault(l => l.Id == poLineId)
                ?? throw StockErrors.NotFound("po_line_not_found", "Bestellzeile gehört nicht zur Bestellung der Lieferung");
            if (poLine.ArticleId != article.Id)
                throw StockErrors.Conflict("po_line_article_mismatch", $"Die Bestellzeile ist für einen anderen Artikel bestellt als {article.Sku}");
            unitCost ??= poLine.UnitPriceCents;
        }

        shipment.AddLine(article.Id, bin.Id, request.Quantity, request.LotNumber, request.ExpiryDate, request.PurchaseOrderLineId, unitCost);
        await _uow.SaveChangesAsync(ct);

        return await GetAsync(shipmentId, ct);
    }

    public async Task<InboundShipmentDto?> RemoveLineAsync(Guid shipmentId, Guid lineId, CancellationToken ct = default)
    {
        var shipment = await _inbound.GetWithLinesAsync(shipmentId, ct);
        if (shipment is null) return null;
        shipment.RemoveLine(lineId);
        await _uow.SaveChangesAsync(ct);
        return await GetAsync(shipmentId, ct);
    }

    /// <summary>
    /// Bucht die Lieferung: setzt sie auf Received und bucht jede Zeile lot-genau über <see cref="StockBooking"/>
    /// (zwei Chargen im selben Lagerplatz = zwei Bestandszeilen; gleiche Charge und gleiches MHD in mehreren Zeilen =
    /// eine Bestandszeile; dieselbe Charge mit anderem MHD = Fehler lot_expiry_mismatch). Bei einer Lieferung mit
    /// Bestellbezug wird die empfangene Menge in die Bestellzeilen fortgeschrieben (Status Received/PartiallyReceived);
    /// mehr als die offene Menge einer Bestellzeile ist ein Fehler (po_over_receipt). Der Einkaufspreis der Zeile
    /// (bzw. der aktuelle Artikelpreis) ist der Kosten-Snapshot jeder Movement.
    /// Status-Guard: nur eine Lieferung im Entwurf lässt sich buchen - ein zweiter Aufruf (Doppelklick, Retry)
    /// wirft und bucht nichts. Bestand, Movements, Lieferung und Bestellung gehen in EINEM SaveChanges.
    /// </summary>
    public async Task<InboundShipmentDto?> ReceiveAsync(Guid shipmentId, CancellationToken ct = default)
    {
        var shipment = await _inbound.GetWithLinesAsync(shipmentId, ct);
        if (shipment is null) return null;

        shipment.MarkReceived();

        PurchaseOrder? po = null;
        if (shipment.PurchaseOrderId is Guid poId)
        {
            po = await _purchaseOrders.GetWithLinesAsync(poId, ct)
                ?? throw StockErrors.NotFound("po_not_found", "Die zugehörige Bestellung wurde nicht gefunden");
            EnsureWithinOpenQuantities(shipment, po);
        }

        // Artikelpreis einmal vorab: Fallback für Zeilen ohne eigenen Einkaufspreis (Cost-Snapshot je Movement für FIFO).
        var articleMap = await _articles.GetManyAsync(shipment.Lines.Select(l => l.ArticleId).Distinct(), ct);

        foreach (var line in shipment.Lines)
        {
            var costCents = line.UnitCostCents ?? articleMap.GetValueOrDefault(line.ArticleId)?.PurchasePriceCents ?? 0;
            await StockBooking.BookAsync(_stock, _movements,
                line.ArticleId, line.TargetBinId, +line.Quantity, StockMovementReason.Inbound,
                "InboundShipment", shipment.Id, line.LotNumber, line.ExpiryDate, costCents, ct: ct);
        }

        if (po is not null)
        {
            foreach (var line in shipment.Lines.Where(l => l.PurchaseOrderLineId is not null))
                po.ReceiveLine(line.PurchaseOrderLineId!.Value, line.Quantity);
        }

        await _uow.SaveChangesAsync(ct);
        return await GetAsync(shipmentId, ct);
    }

    public async Task<bool> CancelAsync(Guid shipmentId, CancellationToken ct = default)
    {
        var shipment = await _inbound.GetWithLinesAsync(shipmentId, ct);
        if (shipment is null) return false;
        shipment.Cancel();
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Die Zeilen einer Lieferung dürfen die offene Menge ihrer Bestellzeile nicht überschreiten (Summe je Bestellzeile).</summary>
    private static void EnsureWithinOpenQuantities(InboundShipment shipment, PurchaseOrder po)
    {
        foreach (var group in shipment.Lines.Where(l => l.PurchaseOrderLineId is not null).GroupBy(l => l.PurchaseOrderLineId!.Value))
        {
            var poLine = po.Lines.FirstOrDefault(l => l.Id == group.Key)
                ?? throw StockErrors.NotFound("po_line_not_found", "Eine Bestellzeile der Lieferung gehört nicht (mehr) zur Bestellung");
            var received = group.Sum(l => (long)l.Quantity);
            if (received > poLine.OpenQuantity())
                throw StockErrors.Conflict("po_over_receipt",
                    $"Wareneingang {received} für {poLine.ArticleSku} überschreitet die offene Bestellmenge {poLine.OpenQuantity()} (Bestellung {po.PoNumber})");
        }
    }

    /// <summary>WE-{Bestellnummer}, bei Wiederholung (Teillieferungen) mit Zähler: WE-{Bestellnummer}-2 usw.</summary>
    private async Task<string> NextShipmentNumberAsync(string poNumber, CancellationToken ct)
    {
        var baseNumber = $"WE-{poNumber}";
        var candidate = baseNumber;
        for (var n = 2; await _inbound.GetByNumberAsync(candidate, ct) is not null; n++)
            candidate = $"{baseNumber}-{n}";
        return candidate;
    }

    private async Task<IReadOnlyList<InboundShipmentDto>> MapAsync(IEnumerable<InboundShipment> shipments, CancellationToken ct)
    {
        var list = shipments.ToList();
        if (list.Count == 0) return Array.Empty<InboundShipmentDto>();

        var articleIds = list.SelectMany(s => s.Lines).Select(l => l.ArticleId).Distinct();
        var binIds = list.SelectMany(s => s.Lines).Select(l => l.TargetBinId).Distinct();
        var articleMap = await _articles.GetManyAsync(articleIds, ct);
        var binMap = await _warehouse.GetStorageLocationsAsync(binIds, ct);

        return list.Select(s => new InboundShipmentDto(
            s.Id, s.ShipmentNumber, s.SupplierReference, s.Notes, s.Status.ToString(),
            s.ReceivedAt, s.CreatedAt,
            s.Lines.Select(l =>
            {
                var a = articleMap.GetValueOrDefault(l.ArticleId);
                var b = binMap.GetValueOrDefault(l.TargetBinId);
                return new InboundLineDto(
                    l.Id, l.ArticleId, a?.Sku ?? "?", a?.Name ?? "?",
                    l.TargetBinId, b?.Code ?? "?",
                    l.Quantity, l.LotNumber, l.ExpiryDate,
                    l.PurchaseOrderLineId, l.UnitCostCents);
            }).ToList(),
            s.PurchaseOrderId)).ToList();
    }
}
