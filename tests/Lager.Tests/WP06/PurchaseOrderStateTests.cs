using Lager.Domain.Purchasing;

namespace Lager.Tests.WP06;

/// <summary>Domain-Unit-Tests (ohne Host) für die Statusmaschine von <see cref="PurchaseOrder"/>.</summary>
public class PurchaseOrderStateTests
{
    private const int Ordered = 2_000_000;

    private static (PurchaseOrder Po, Guid LineId) InState(PurchaseOrderStatus status)
    {
        var po = new PurchaseOrder("PO-1", Guid.NewGuid(), "EUR");
        var line = po.AddLine(Guid.NewGuid(), "SKU-1", Ordered, 250);
        switch (status)
        {
            case PurchaseOrderStatus.Sent:
                po.MarkSent();
                break;
            case PurchaseOrderStatus.PartiallyReceived:
                po.MarkSent();
                po.ReceiveLine(line.Id, 10);
                break;
            case PurchaseOrderStatus.Received:
                po.MarkSent();
                po.ReceiveLine(line.Id, PurchaseOrder.MaxQuantity);
                po.ReceiveLine(line.Id, Ordered - PurchaseOrder.MaxQuantity);
                break;
            case PurchaseOrderStatus.Cancelled:
                po.Cancel();
                break;
        }
        return (po, line.Id);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, true)]
    [InlineData(PurchaseOrderStatus.Sent, true)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, true)]
    [InlineData(PurchaseOrderStatus.Received, false)]
    [InlineData(PurchaseOrderStatus.Cancelled, false)]
    public void Cancel_ist_nur_aus_offenen_Zustaenden_erlaubt(PurchaseOrderStatus from, bool allowed)
    {
        var (po, _) = InState(from);
        var tokenBefore = po.ConcurrencyToken;

        if (allowed)
        {
            po.Cancel();
            Assert.Equal(PurchaseOrderStatus.Cancelled, po.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => po.Cancel());
            Assert.Equal(from, po.Status);
            Assert.Equal(tokenBefore, po.ConcurrencyToken);
        }
    }

    [Fact]
    public void Nach_vollstaendigem_Wareneingang_ist_Cancel_nicht_moeglich()
    {
        var (po, _) = InState(PurchaseOrderStatus.Received);

        Assert.Throws<InvalidOperationException>(() => po.Cancel());

        Assert.Equal(PurchaseOrderStatus.Received, po.Status);
        Assert.NotNull(po.ReceivedAt);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Sent)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    public void Zeilen_und_Versand_sind_nur_aus_Draft_erlaubt(PurchaseOrderStatus from)
    {
        var (po, lineId) = InState(from);

        Assert.Throws<InvalidOperationException>(() => po.AddLine(Guid.NewGuid(), "SKU-2", 1, 100));
        Assert.Throws<InvalidOperationException>(() => po.RemoveLine(lineId));
        Assert.Throws<InvalidOperationException>(() => po.MarkSent());

        Assert.Equal(from, po.Status);
        Assert.Single(po.Lines);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    public void ReceiveLine_ist_nur_aus_Sent_oder_PartiallyReceived_erlaubt(PurchaseOrderStatus from)
    {
        var (po, lineId) = InState(from);

        Assert.Throws<InvalidOperationException>(() => po.ReceiveLine(lineId, 1));

        Assert.Equal(from, po.Status);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(PurchaseOrder.MaxQuantity + 1)]
    [InlineData(int.MaxValue)]
    public void ReceiveLine_lehnt_Mengen_ausserhalb_1_bis_1_Million_ab(int qty)
    {
        var (po, lineId) = InState(PurchaseOrderStatus.Sent);

        Assert.Throws<ArgumentOutOfRangeException>(() => po.ReceiveLine(lineId, qty));

        Assert.Equal(PurchaseOrderStatus.Sent, po.Status);
        Assert.Equal(0, po.Lines.Single().ReceivedQty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PurchaseOrder.MaxQuantity)]
    public void ReceiveLine_akzeptiert_die_Grenzwerte_und_meldet_Teilempfang(int qty)
    {
        var (po, lineId) = InState(PurchaseOrderStatus.Sent);

        po.ReceiveLine(lineId, qty);

        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, po.Status);
        Assert.Equal(qty, po.Lines.Single().ReceivedQty);
    }

    [Fact]
    public void ReceiveLine_ueber_bestellte_Menge_hinaus_bleibt_abgelehnt()
    {
        var po = new PurchaseOrder("PO-2", Guid.NewGuid(), "EUR");
        var line = po.AddLine(Guid.NewGuid(), "SKU-1", 5, 100);
        po.MarkSent();

        Assert.Throws<InvalidOperationException>(() => po.ReceiveLine(line.Id, 6));

        Assert.Equal(PurchaseOrderStatus.Sent, po.Status);
        Assert.Equal(0, line.ReceivedQty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PurchaseOrderLine_lehnt_Bestellmengen_bis_0_ab(int orderedQty)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PurchaseOrderLine(Guid.NewGuid(), "SKU-1", orderedQty, 100));
    }
}
