using Lager.Domain.Inbound;

namespace Lager.Tests.WP06;

/// <summary>Domain-Unit-Tests (ohne Host) für die Statusmaschine von <see cref="InboundShipment"/>.</summary>
public class InboundShipmentStateTests
{
    private static InboundShipment InState(InboundShipmentStatus status)
    {
        var s = new InboundShipment("LS-1", null, null);
        s.AddLine(Guid.NewGuid(), Guid.NewGuid(), 10, null, null);
        switch (status)
        {
            case InboundShipmentStatus.Received: s.MarkReceived(); break;
            case InboundShipmentStatus.Cancelled: s.Cancel(); break;
        }
        return s;
    }

    [Theory]
    [InlineData(InboundShipmentStatus.Draft, true)]
    [InlineData(InboundShipmentStatus.Received, false)]
    [InlineData(InboundShipmentStatus.Cancelled, false)]
    public void Cancel_ist_nur_aus_Draft_erlaubt(InboundShipmentStatus from, bool allowed)
    {
        var s = InState(from);
        var tokenBefore = s.ConcurrencyToken;

        if (allowed)
        {
            s.Cancel();
            Assert.Equal(InboundShipmentStatus.Cancelled, s.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => s.Cancel());
            Assert.Equal(from, s.Status);
            Assert.Equal(tokenBefore, s.ConcurrencyToken);
        }
    }

    [Theory]
    [InlineData(InboundShipmentStatus.Received)]
    [InlineData(InboundShipmentStatus.Cancelled)]
    public void Abgeschlossene_oder_stornierte_Lieferung_ist_endgueltig(InboundShipmentStatus from)
    {
        var s = InState(from);

        Assert.Throws<InvalidOperationException>(() => s.MarkReceived());
        Assert.Throws<InvalidOperationException>(() => s.AddLine(Guid.NewGuid(), Guid.NewGuid(), 1, null, null));
        Assert.Throws<InvalidOperationException>(() => s.RemoveLine(s.Lines[0].Id));
        Assert.Equal(from, s.Status);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(InboundShipment.MaxQuantity + 1)]
    [InlineData(int.MaxValue)]
    public void AddLine_lehnt_Mengen_ausserhalb_1_bis_1_Million_ab(int qty)
    {
        var s = InState(InboundShipmentStatus.Draft);

        Assert.Throws<ArgumentOutOfRangeException>(() => s.AddLine(Guid.NewGuid(), Guid.NewGuid(), qty, null, null));

        Assert.Single(s.Lines); // nur die Zeile aus dem Setup, keine halb angelegte
    }

    [Theory]
    [InlineData(1)]
    [InlineData(InboundShipment.MaxQuantity)]
    public void AddLine_akzeptiert_die_Grenzwerte(int qty)
    {
        var s = InState(InboundShipmentStatus.Draft);

        var line = s.AddLine(Guid.NewGuid(), Guid.NewGuid(), qty, null, null);

        Assert.Equal(qty, line.Quantity);
        Assert.Equal(2, s.Lines.Count);
    }
}
