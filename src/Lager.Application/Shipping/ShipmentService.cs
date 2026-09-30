using Lager.Application.Abstractions;
using Lager.Contracts.Shipping;
using Lager.Domain.Customers;
using Lager.Domain.Orders;
using Lager.Domain.Shipping;

namespace Lager.Application.Shipping;

/// <summary>
/// Versand. Fachliche Fehler: <see cref="InvalidOperationException"/> = Regelverstoß (Code in <c>Data["code"]</c>),
/// <see cref="ArgumentException"/> = ungültige Eingabe.
///
/// Kopplung an die Bestellung: Sendungen gibt es nur für gepackte Bestellungen (Status Packed); sobald keine Sendung
/// der Bestellung mehr offen (Ready/Labeled) ist und eine versendet wurde, geht die Bestellung auf Shipped. Eine
/// stornierte Sendung setzt die Bestellung nicht zurück - sie bleibt Packed und bekommt bei Bedarf eine neue Sendung.
/// Empfänger einer Sendung ist die Lieferadresse der Bestellung (Kunde und verknüpfte Adresse); sie wird bei Bedarf
/// nachgeschlagen (Anzeige, Carrier-Label), nicht an der Sendung gespeichert.
/// </summary>
public class ShipmentService
{
    private readonly IShipmentRepository _repo;
    private readonly IOrderRepository _orders;
    private readonly ICustomerRepository _customers;
    private readonly ICarrierRegistry _carriers;
    private readonly IUnitOfWork _uow;

    public ShipmentService(
        IShipmentRepository repo, IOrderRepository orders, ICustomerRepository customers,
        ICarrierRegistry carriers, IUnitOfWork uow)
    {
        _repo = repo;
        _orders = orders;
        _customers = customers;
        _carriers = carriers;
        _uow = uow;
    }

    /// <summary>Alle bekannten Carrier; nur die konfigurierten (<c>IsConfigured</c>) lassen sich für neue Sendungen wählen.</summary>
    public IReadOnlyList<CarrierDto> ListCarriers() =>
        _carriers.All
            .Select(c => new CarrierDto(c.CarrierCode, c.DisplayName, c.IsConfigured, c.IsConfigured ? null : c.UnavailableReason))
            .ToList();

    public async Task<IReadOnlyList<ShipmentDto>> ListAsync(CancellationToken ct = default)
    {
        var shipments = await _repo.ListAsync(ct);
        var orderIds = shipments.Select(s => s.OrderId).Distinct().ToList();
        var orderMap = (await _orders.GetManyAsync(orderIds, ct)).ToDictionary(o => o.Id);
        var customerIds = orderMap.Values.Where(o => o.CustomerId.HasValue).Select(o => o.CustomerId!.Value).Distinct().ToList();
        var customers = customerIds.Count == 0 ? new Dictionary<Guid, Customer>() : await _customers.GetManyAsync(customerIds, ct);
        return shipments.Select(s => ToDto(s, orderMap.GetValueOrDefault(s.OrderId), customers)).ToList();
    }

    public async Task<ShipmentDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var s = await _repo.GetAsync(id, ct);
        return s is null ? null : await ToDtoAsync(s, await _orders.GetAsync(s.OrderId, ct), ct);
    }

    /// <summary>
    /// Legt eine Sendung an. Voraussetzungen: der Carrier ist bekannt und konfiguriert, die Bestellung existiert und ist
    /// gepackt (Packed). Länge, Breite, Höhe und Gewicht sind Pflicht (größer 0).
    /// </summary>
    public async Task<ShipmentDto> CreateAsync(CreateShipmentRequest req, CancellationToken ct = default)
    {
        var carrier = _carriers.Get(req.CarrierCode?.Trim() ?? string.Empty)
            ?? throw Rule("unknown_carrier", $"Unbekannter Carrier: {req.CarrierCode}");
        if (!carrier.IsConfigured)
            throw Rule("carrier_not_available",
                $"Carrier {carrier.DisplayName} ist nicht verfügbar: {carrier.UnavailableReason ?? "nicht konfiguriert"}");

        var order = await _orders.GetAsync(req.OrderId, ct)
            ?? throw Invalid("unknown_order", "Bestellung nicht gefunden");
        if (order.Status != OrderStatus.Packed)
            throw Rule("order_not_packed",
                $"Für Bestellung {order.OrderNumber} lässt sich keine Sendung anlegen: sie hat den Status {order.Status}, " +
                "Sendungen gibt es nur für gepackte Bestellungen (Status Packed)");

        var seq = await _repo.NextSequenceAsync(ct);
        var num = $"SH-{DateTime.UtcNow:yyyyMMdd}-{seq:D5}";
        var ship = new Shipment(num, order.Id, req.PickListId, carrier.CarrierCode);
        ship.SetDimensions(req.LengthMm, req.WidthMm, req.HeightMm, req.WeightGrams);
        if (!string.IsNullOrWhiteSpace(req.Notes)) ship.SetNotes(req.Notes);
        await _repo.AddAsync(ship, ct);
        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(ship, order, ct);
    }

    /// <summary>
    /// Weist das Tracking zu. Ist der Carrier der Sendung konfiguriert, erzeugt sein Adapter das Label
    /// (<see cref="ICarrierAdapter.CreateLabelAsync"/>, mit Maßen und der Lieferadresse der Bestellung als Empfänger):
    /// liefert er eine Nummer, gilt sie; meldet er "manuell" (<see cref="CarrierLabelResult.IsManual"/>), ist die
    /// Tracking-Nr der Anfrage Pflicht. Für Carrier ohne Anbindung (auch Altbestand) bleibt es bei der manuellen Eingabe.
    /// </summary>
    public async Task<ShipmentDto?> AssignTrackingAsync(Guid id, AssignTrackingRequest req, CancellationToken ct = default)
    {
        var ship = await _repo.GetAsync(id, ct);
        if (ship is null) return null;
        // Zuerst prüfen, dann den Carrier fragen: bei einer stornierten oder schon versendeten Sendung darf kein
        // Label entstehen (bei einer echten Carrier-API wäre es bezahlt, und die Zuweisung scheiterte danach doch).
        ship.EnsureTrackingAssignable();
        var order = await _orders.GetAsync(ship.OrderId, ct);

        var trackingNumber = req.TrackingNumber;
        var trackingUrl = req.TrackingUrl;
        var costCents = req.CostCents;

        var adapter = _carriers.Get(ship.CarrierCode);
        if (adapter is { IsConfigured: true })
        {
            var to = await RecipientAsync(order, ct);
            var label = await adapter.CreateLabelAsync(new CreateLabelRequest(
                ship.Id, ship.ShipmentNumber, ship.WeightGrams, ship.LengthMm, ship.WidthMm, ship.HeightMm,
                to.Name ?? string.Empty, to.Street ?? string.Empty, to.Zip ?? string.Empty,
                to.City ?? string.Empty, to.Country ?? string.Empty, to.Street2), ct);

            if (!label.IsManual)
            {
                if (string.IsNullOrWhiteSpace(label.TrackingNumber))
                    throw Rule("label_failed", $"Carrier {adapter.DisplayName} hat keine Tracking-Nummer geliefert");
                trackingNumber = label.TrackingNumber;
                trackingUrl = label.TrackingUrl;
                costCents = label.CostCents;
            }
        }

        if (string.IsNullOrWhiteSpace(trackingNumber))
            throw Invalid("tracking_number_required", "Die Tracking-Nr ist Pflicht (Carrier ohne Anbindung: manuelle Eingabe)");

        ship.AssignTracking(trackingNumber, trackingUrl, costCents);
        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(ship, order, ct);
    }

    /// <summary>
    /// Versand: die Sendung geht an den Carrier. Sind damit alle Sendungen der Bestellung raus (keine mehr Ready oder
    /// Labeled, stornierte zählen nicht), wechselt die Bestellung von Packed auf Shipped - im selben Commit.
    /// </summary>
    public async Task<ShipmentDto?> MarkShippedAsync(Guid id, CancellationToken ct = default)
    {
        var ship = await _repo.GetAsync(id, ct);
        if (ship is null) return null;
        ship.MarkShipped();

        var order = await _orders.GetAsync(ship.OrderId, ct);
        await AdvanceOrderAsync(order, ship, ct);

        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(ship, order, ct);
    }

    /// <summary>
    /// Die Bestellung gilt als versendet (Packed -> Shipped), sobald mindestens eine ihrer Sendungen raus ist (Shipped
    /// oder Delivered) und keine mehr offen (Ready oder Labeled); stornierte Sendungen zählen nicht. Sonst bleibt sie
    /// Packed - auch dann, wenn die einzige Sendung storniert wird: die Bestellung wird nie zurückgesetzt.
    /// <paramref name="changed"/> ist die soeben (noch ungespeicherte) geänderte Sendung.
    /// </summary>
    private async Task AdvanceOrderAsync(Order? order, Shipment changed, CancellationToken ct)
    {
        if (order is not { Status: OrderStatus.Packed }) return;

        var all = (await _repo.ListByOrderAsync(order.Id, ct)).Where(s => s.Id != changed.Id).Append(changed).ToList();
        var anyOut = all.Any(s => s.Status is ShipmentStatus.Shipped or ShipmentStatus.Delivered);
        var anyOpen = all.Any(s => s.Status is ShipmentStatus.Ready or ShipmentStatus.Labeled);
        if (anyOut && !anyOpen) order.MarkShipped();
    }

    public async Task<ShipmentDto?> MarkDeliveredAsync(Guid id, CancellationToken ct = default)
    {
        var ship = await _repo.GetAsync(id, ct);
        if (ship is null) return null;
        ship.MarkDelivered();
        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(ship, await _orders.GetAsync(ship.OrderId, ct), ct);
    }

    /// <summary>
    /// Storniert eine offene Sendung (Ready/Labeled). Die Bestellung wird dadurch nie zurückgesetzt, sie bleibt Packed
    /// und bekommt bei Bedarf eine neue Sendung; war die stornierte die letzte offene neben bereits versendeten, gilt
    /// die Bestellung nun als versendet.
    /// </summary>
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var ship = await _repo.GetAsync(id, ct);
        if (ship is null) return false;
        ship.Cancel();
        await AdvanceOrderAsync(await _orders.GetAsync(ship.OrderId, ct), ship, ct);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    // ---- Empfänger --------------------------------------------------------

    private sealed record Recipient(string? Name, string? Street, string? Street2, string? Zip, string? City, string? Country);

    private static readonly Recipient NoRecipient = new(null, null, null, null, null, null);

    /// <summary>Lieferadresse der Bestellung: Name des Kunden plus die verknüpfte Adresse (ohne Adresse nur der Name; ohne Kunde leer).</summary>
    private static Recipient RecipientOf(Order? order, IReadOnlyDictionary<Guid, Customer> customers)
    {
        if (order?.CustomerId is not Guid customerId || !customers.TryGetValue(customerId, out var customer)) return NoRecipient;
        var address = customer.Addresses.FirstOrDefault(a => a.Id == order.ShippingAddressId);
        return address is null
            ? new Recipient(customer.Name, null, null, null, null, null)
            : new Recipient(customer.Name, address.Street, address.Street2, address.Zip, address.City, address.Country);
    }

    private async Task<Recipient> RecipientAsync(Order? order, CancellationToken ct)
    {
        if (order?.CustomerId is not Guid customerId) return NoRecipient;
        return RecipientOf(order, await _customers.GetManyAsync(new[] { customerId }, ct));
    }

    // ---- Mapping ----------------------------------------------------------

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

    private async Task<ShipmentDto> ToDtoAsync(Shipment s, Order? order, CancellationToken ct)
    {
        var customers = order?.CustomerId is Guid customerId
            ? await _customers.GetManyAsync(new[] { customerId }, ct)
            : new Dictionary<Guid, Customer>();
        return ToDto(s, order, customers);
    }

    private static ShipmentDto ToDto(Shipment s, Order? order, IReadOnlyDictionary<Guid, Customer> customers)
    {
        var to = RecipientOf(order, customers);
        return new ShipmentDto(
            s.Id, s.ShipmentNumber, s.OrderId, order?.OrderNumber, s.PickListId,
            s.CarrierCode, s.TrackingNumber, s.TrackingUrl,
            s.WeightGrams, s.LengthMm, s.WidthMm, s.HeightMm, s.CostCents, s.Notes,
            s.Status.ToString(), s.CreatedAt, s.LabeledAt, s.ShippedAt, s.DeliveredAt,
            to.Name, to.Street, to.Street2, to.Zip, to.City, to.Country);
    }
}
