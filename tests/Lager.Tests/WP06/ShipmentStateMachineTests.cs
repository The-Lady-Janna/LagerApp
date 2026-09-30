using Lager.Domain.Shipping;

namespace Lager.Tests.WP06;

/// <summary>
/// Domain-Unit-Tests (ohne Host) für die Shipment-Statusmaschine. Die Matrix spiegelt die
/// Tabelle im Klassenkommentar von <see cref="Shipment"/> wider.
/// </summary>
public class ShipmentStateMachineTests
{
    private static Shipment InState(ShipmentStatus status)
    {
        var s = new Shipment("SH-TEST-1", Guid.NewGuid(), null, "dhl");
        if (status == ShipmentStatus.Ready) return s;
        if (status == ShipmentStatus.Cancelled) { s.Cancel(); return s; }

        s.AssignTracking("TRACK-1", null, 500);
        if (status == ShipmentStatus.Labeled) return s;
        s.MarkShipped();
        if (status == ShipmentStatus.Shipped) return s;
        s.MarkDelivered();
        return s;
    }

    private static void Run(Shipment s, string action)
    {
        switch (action)
        {
            case nameof(Shipment.SetDimensions): s.SetDimensions(300, 200, 100, 1500); break;
            case nameof(Shipment.AssignTracking): s.AssignTracking("TRACK-2", null, 0); break;
            case nameof(Shipment.MarkShipped): s.MarkShipped(); break;
            case nameof(Shipment.MarkDelivered): s.MarkDelivered(); break;
            case nameof(Shipment.Cancel): s.Cancel(); break;
            default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    // (Ausgangsstatus, Aktion, erlaubt, Status danach)
    public static TheoryData<ShipmentStatus, string, bool, ShipmentStatus> Matrix()
    {
        const ShipmentStatus R = ShipmentStatus.Ready, L = ShipmentStatus.Labeled, S = ShipmentStatus.Shipped,
            D = ShipmentStatus.Delivered, C = ShipmentStatus.Cancelled;
        return new()
        {
            { R, nameof(Shipment.SetDimensions), true, R },
            { R, nameof(Shipment.AssignTracking), true, L },
            { R, nameof(Shipment.MarkShipped), false, R },
            { R, nameof(Shipment.MarkDelivered), false, R },
            { R, nameof(Shipment.Cancel), true, C },

            { L, nameof(Shipment.SetDimensions), true, L },
            { L, nameof(Shipment.AssignTracking), true, L },
            { L, nameof(Shipment.MarkShipped), true, S },
            { L, nameof(Shipment.MarkDelivered), false, L },
            { L, nameof(Shipment.Cancel), true, C },

            { S, nameof(Shipment.SetDimensions), false, S },
            { S, nameof(Shipment.AssignTracking), false, S },
            { S, nameof(Shipment.MarkShipped), false, S },
            { S, nameof(Shipment.MarkDelivered), true, D },
            { S, nameof(Shipment.Cancel), false, S },

            { D, nameof(Shipment.SetDimensions), false, D },
            { D, nameof(Shipment.AssignTracking), false, D },
            { D, nameof(Shipment.MarkShipped), false, D },
            { D, nameof(Shipment.MarkDelivered), false, D },
            { D, nameof(Shipment.Cancel), false, D },

            { C, nameof(Shipment.SetDimensions), false, C },
            { C, nameof(Shipment.AssignTracking), false, C },
            { C, nameof(Shipment.MarkShipped), false, C },
            { C, nameof(Shipment.MarkDelivered), false, C },
            { C, nameof(Shipment.Cancel), false, C },
        };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Uebergaenge_entsprechen_der_dokumentierten_Statustabelle(
        ShipmentStatus from, string action, bool allowed, ShipmentStatus expected)
    {
        var s = InState(from);
        var tokenBefore = s.ConcurrencyToken;

        if (allowed)
        {
            Run(s, action);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Run(s, action));
            // Abgelehnte Übergänge dürfen nichts verändern (kein Touch, kein Teil-Update).
            Assert.Equal(tokenBefore, s.ConcurrencyToken);
        }

        Assert.Equal(expected, s.Status);
    }

    [Fact]
    public void Stornierte_Sendung_wird_durch_Tracking_nicht_wiederbelebt()
    {
        var s = InState(ShipmentStatus.Cancelled);

        Assert.Throws<InvalidOperationException>(() => s.AssignTracking("TRACK-9", "https://t.example/9", 100));
        Assert.Throws<InvalidOperationException>(() => s.MarkShipped());

        Assert.Equal(ShipmentStatus.Cancelled, s.Status);
        Assert.Null(s.TrackingNumber);
        Assert.Null(s.LabeledAt);
        Assert.Null(s.ShippedAt);
    }

    [Fact]
    public void Versendete_Sendung_bleibt_nach_Stornierungsversuch_unveraendert_versendet()
    {
        var s = InState(ShipmentStatus.Shipped);

        Assert.Throws<InvalidOperationException>(() => s.Cancel());

        Assert.Equal(ShipmentStatus.Shipped, s.Status);
        Assert.Equal("TRACK-1", s.TrackingNumber);
        Assert.NotNull(s.ShippedAt);
    }

    [Fact]
    public void Tracking_kann_aus_Labeled_neu_zugewiesen_werden_und_Status_bleibt_Labeled()
    {
        var s = InState(ShipmentStatus.Labeled);

        s.AssignTracking("  TRACK-NEU  ", "https://t.example/neu", 750);

        Assert.Equal(ShipmentStatus.Labeled, s.Status);
        Assert.Equal("TRACK-NEU", s.TrackingNumber);
        Assert.Equal(750, s.CostCents);
    }

    [Fact]
    public void Regulaerer_Ablauf_Ready_Labeled_Shipped_Delivered()
    {
        var s = InState(ShipmentStatus.Ready);

        s.SetDimensions(400, 300, 200, 2500);
        s.AssignTracking("TRACK-1", null, 490);
        s.MarkShipped();
        s.MarkDelivered();

        Assert.Equal(ShipmentStatus.Delivered, s.Status);
        Assert.NotNull(s.LabeledAt);
        Assert.NotNull(s.ShippedAt);
        Assert.NotNull(s.DeliveredAt);
        Assert.Equal(2500, s.WeightGrams);
    }
}
