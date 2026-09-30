using Lager.Domain.Returns;

namespace Lager.Tests.WP06;

/// <summary>Domain-Unit-Tests (ohne Host) für die Statusmaschine von <see cref="ReturnShipment"/>.</summary>
public class ReturnShipmentStateTests
{
    private static (ReturnShipment Ret, Guid LineId) InState(ReturnStatus status)
    {
        var r = new ReturnShipment("RMA-1", null, null, null);
        var line = r.AddLine(Guid.NewGuid(), "SKU-1", 3);
        switch (status)
        {
            case ReturnStatus.Processed:
                r.SetLineQc(line.Id, QcResult.Defect, null, "Glasbruch");
                r.MarkProcessed();
                break;
            case ReturnStatus.Cancelled:
                r.Cancel();
                break;
        }
        return (r, line.Id);
    }

    [Theory]
    [InlineData(ReturnStatus.Draft, true)]
    [InlineData(ReturnStatus.Processed, false)]
    [InlineData(ReturnStatus.Cancelled, false)]
    public void Cancel_ist_nur_aus_Draft_erlaubt(ReturnStatus from, bool allowed)
    {
        var (r, _) = InState(from);
        var tokenBefore = r.ConcurrencyToken;

        if (allowed)
        {
            r.Cancel();
            Assert.Equal(ReturnStatus.Cancelled, r.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => r.Cancel());
            Assert.Equal(from, r.Status);
            Assert.Equal(tokenBefore, r.ConcurrencyToken);
        }
    }

    [Theory]
    [InlineData(ReturnStatus.Processed)]
    [InlineData(ReturnStatus.Cancelled)]
    public void Abgeschlossene_oder_stornierte_Retoure_ist_endgueltig(ReturnStatus from)
    {
        var (r, lineId) = InState(from);

        Assert.Throws<InvalidOperationException>(() => r.MarkProcessed());
        Assert.Throws<InvalidOperationException>(() => r.AddLine(Guid.NewGuid(), "SKU-2", 1));
        Assert.Throws<InvalidOperationException>(() => r.SetLineQc(lineId, QcResult.Defect, null, null));
        Assert.Equal(from, r.Status);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(ReturnShipment.MaxQuantity + 1)]
    [InlineData(int.MaxValue)]
    public void AddLine_und_ReturnLine_lehnen_Mengen_ausserhalb_1_bis_1_Million_ab(int qty)
    {
        var (r, _) = InState(ReturnStatus.Draft);

        Assert.Throws<ArgumentOutOfRangeException>(() => r.AddLine(Guid.NewGuid(), "SKU-2", qty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReturnLine(Guid.NewGuid(), "SKU-2", qty, null));

        Assert.Single(r.Lines); // nur die Zeile aus dem Setup, keine halb angelegte
    }

    [Theory]
    [InlineData(1)]
    [InlineData(ReturnShipment.MaxQuantity)]
    public void AddLine_akzeptiert_die_Grenzwerte(int qty)
    {
        var (r, _) = InState(ReturnStatus.Draft);

        var line = r.AddLine(Guid.NewGuid(), "SKU-2", qty);

        Assert.Equal(qty, line.Quantity);
        Assert.Equal(2, r.Lines.Count);
    }
}
