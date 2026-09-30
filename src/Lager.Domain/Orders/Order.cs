using Lager.Domain.Common;

namespace Lager.Domain.Orders;

public class Order : Entity
{
    public string OrderNumber { get; private set; } = string.Empty;
    public string? CustomerReference { get; private set; }
    /// <summary>
    /// Optional Verknüpfung zum Customer-Stammsatz. Backward-Compat: bestehende
    /// Orders ohne CustomerId verwenden weiterhin nur den Freitext.
    /// </summary>
    public Guid? CustomerId { get; private set; }
    public Guid? ShippingAddressId { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.New;
    public OrderSource Source { get; private set; }

    /// <summary>Höchste Priorität (0 = normal ... 3 = dringend).</summary>
    public const int MaxPriority = 3;

    /// <summary>Maximale Länge von <see cref="ExternalReference"/> (auch die Spaltenbreite).</summary>
    public const int MaxExternalReferenceLength = 128;

    /// <summary>
    /// Dringlichkeit 0..<see cref="MaxPriority"/> (höher = früher kommissionieren). Bestimmt zusammen mit
    /// <see cref="DueDate"/> die Reihenfolge beim Wagen-Füllen und in der Bestandsampel.
    /// </summary>
    public int Priority { get; private set; }

    /// <summary>Gewünschter Liefer-/Fertigstellungstermin (UTC), optional. Früher fällige Bestellungen kommen zuerst.</summary>
    public DateTime? DueDate { get; private set; }

    /// <summary>
    /// Kennung der Bestellung im Quellsystem (z. B. Shop-Bestellnummer bzw. Idempotency-Key der externen Bestell-API).
    /// Eindeutig, wenn gesetzt: dieselbe Referenz liefert dieselbe Bestellung statt eines Duplikats.
    /// </summary>
    public string? ExternalReference { get; private set; }

    private readonly List<OrderLine> _lines = new();
    public IReadOnlyCollection<OrderLine> Lines => _lines.AsReadOnly();

    private Order() { }

    public Order(string orderNumber, OrderSource source, string? customerReference, IEnumerable<OrderLine> lines)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new ArgumentException("Order number is required", nameof(orderNumber));
        OrderNumber = orderNumber.Trim();
        Source = source;
        CustomerReference = customerReference;

        foreach (var line in lines)
        {
            line.AttachToOrder(Id);
            _lines.Add(line);
        }

        if (_lines.Count == 0) throw new ArgumentException("Order must have at least one line", nameof(lines));
    }

    /// <summary>
    /// Erlaubte Statusübergänge der Bestellung. Shipped und Cancelled sind endgültig. Der Rückweg
    /// Picking -> New ist bewusst NICHT hier, sondern nur über <see cref="ReleaseFromPicking"/> möglich.
    /// </summary>
    private static readonly Dictionary<OrderStatus, OrderStatus[]> AllowedTransitions = new()
    {
        [OrderStatus.New] = new[] { OrderStatus.Picking, OrderStatus.Cancelled },
        [OrderStatus.Picking] = new[] { OrderStatus.Picked, OrderStatus.Cancelled },
        [OrderStatus.Picked] = new[] { OrderStatus.Packed },
        [OrderStatus.Packed] = new[] { OrderStatus.Shipped },
        [OrderStatus.Shipped] = Array.Empty<OrderStatus>(),
        [OrderStatus.Cancelled] = Array.Empty<OrderStatus>(),
    };

    /// <summary>Prüft, ob <see cref="Transition"/> von <paramref name="from"/> nach <paramref name="to"/> erlaubt ist.</summary>
    public static bool CanTransition(OrderStatus from, OrderStatus to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>
    /// Validierter Statuswechsel: New -> Picking | Cancelled, Picking -> Picked | Cancelled,
    /// Picked -> Packed, Packed -> Shipped. Alles andere wirft <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Transition(OrderStatus next)
    {
        if (!CanTransition(Status, next))
            throw Violation("invalid_order_transition",
                $"Ungültiger Statuswechsel der Bestellung {OrderNumber}: {Status} -> {next}");
        Status = next;
        Touch();
    }

    /// <summary>Regelverstoß als <see cref="InvalidOperationException"/> mit maschinenlesbarem Code in <c>Data["code"]</c>.</summary>
    private static InvalidOperationException Violation(string code, string message)
    {
        var ex = new InvalidOperationException(message);
        ex.Data["code"] = code;
        return ex;
    }

    /// <summary>Pickliste angelegt: New -> Picking.</summary>
    public void MarkPicking() => Transition(OrderStatus.Picking);

    /// <summary>Picken abgeschlossen: Picking -> Picked.</summary>
    public void MarkPicked() => Transition(OrderStatus.Picked);

    /// <summary>Verpackt (Bestand gebucht): Picked -> Packed.</summary>
    public void MarkPacked() => Transition(OrderStatus.Packed);

    /// <summary>Versendet (Sendung an den Carrier übergeben): Packed -> Shipped.</summary>
    public void MarkShipped() => Transition(OrderStatus.Shipped);

    /// <summary>
    /// Storniert die Bestellung: erlaubt aus New, Picking und Picked - dort ist noch nichts gebucht (der Bestand
    /// wird erst beim Verpacken abgebucht), der Aufrufer nimmt die Positionen aus den Picklisten. Aus Packed und
    /// Shipped (Bestand gebucht bzw. Ware unterwegs, dafür gibt es die Retoure) und aus Cancelled wirft die
    /// Methode <see cref="InvalidOperationException"/> mit dem Code <c>order_not_cancellable</c>.
    /// Bewusst NICHT über <see cref="Transition"/>: Picked -> Cancelled steht nicht in der allgemeinen Übergangstabelle.
    /// </summary>
    public void Cancel()
    {
        if (Status is not (OrderStatus.New or OrderStatus.Picking or OrderStatus.Picked))
            throw Violation("order_not_cancellable",
                Status == OrderStatus.Cancelled
                    ? $"Bestellung {OrderNumber} ist bereits storniert"
                    : $"Bestellung {OrderNumber} ({Status}) lässt sich nicht mehr stornieren: sie ist bereits verpackt bzw. versendet");
        Status = OrderStatus.Cancelled;
        Touch();
    }

    /// <summary>
    /// Nimmt die Bestellung aus dem Kommissionierprozess zurück (Pickliste gelöscht bzw. storniert, noch
    /// nichts gebucht): Picking | Picked -> New. Aus allen anderen Zuständen - vor allem Packed, Shipped und
    /// Cancelled - wirft die Methode, weil dort schon Bestand gebucht bzw. der Vorgang beendet ist.
    /// </summary>
    public void ReleaseFromPicking()
    {
        if (Status != OrderStatus.Picking && Status != OrderStatus.Picked)
            throw new InvalidOperationException(
                $"Bestellung {OrderNumber} ({Status}) kann nicht aus der Kommissionierung zurückgenommen werden");
        Status = OrderStatus.New;
        Touch();
    }

    public void LinkCustomer(Guid? customerId, Guid? shippingAddressId)
    {
        if (customerId is null && shippingAddressId is not null)
            throw new ArgumentException("Eine Lieferadresse setzt einen Kunden voraus", nameof(shippingAddressId));
        CustomerId = customerId;
        ShippingAddressId = shippingAddressId;
        Touch();
    }

    /// <summary>Setzt Priorität (0..<see cref="MaxPriority"/>) und Fälligkeit (UTC, optional).</summary>
    public void SetPlanning(int priority, DateTime? dueDate)
    {
        if (priority < 0 || priority > MaxPriority)
            throw new ArgumentOutOfRangeException(nameof(priority), priority, $"Die Priorität muss zwischen 0 und {MaxPriority} liegen");
        Priority = priority;
        DueDate = dueDate;
        Touch();
    }

    /// <summary>Setzt die Kennung aus dem Quellsystem (getrimmt, leer = keine).</summary>
    public void SetExternalReference(string? externalReference)
    {
        var value = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim();
        if (value is { Length: > MaxExternalReferenceLength })
            throw new ArgumentException($"Die externe Referenz darf höchstens {MaxExternalReferenceLength} Zeichen lang sein", nameof(externalReference));
        ExternalReference = value;
        Touch();
    }
}
