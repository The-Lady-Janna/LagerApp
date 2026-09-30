using Lager.Domain.Inventory;

namespace Lager.Tests.WP06;

/// <summary>Domain-Unit-Tests (ohne Host) für die Statusmaschine von <see cref="InventoryCount"/>.</summary>
public class InventoryCountStateTests
{
    private static (InventoryCount Count, Guid LineId) InState(InventoryStatus status)
    {
        var c = new InventoryCount("Jahresinventur");
        var line = c.AddSnapshotLine(Guid.NewGuid(), "A-01", Guid.NewGuid(), "SKU-1", 10);
        switch (status)
        {
            case InventoryStatus.Reconciled:
                c.SetCount(line.Id, 8, "Schwund");
                c.Reconcile();
                break;
            case InventoryStatus.Cancelled:
                c.Cancel();
                break;
        }
        return (c, line.Id);
    }

    [Theory]
    [InlineData(InventoryStatus.Open, true)]
    [InlineData(InventoryStatus.Reconciled, false)]
    [InlineData(InventoryStatus.Cancelled, false)]
    public void Cancel_ist_nur_aus_Open_erlaubt(InventoryStatus from, bool allowed)
    {
        var (c, _) = InState(from);
        var tokenBefore = c.ConcurrencyToken;

        if (allowed)
        {
            c.Cancel();
            Assert.Equal(InventoryStatus.Cancelled, c.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => c.Cancel());
            Assert.Equal(from, c.Status);
            Assert.Equal(tokenBefore, c.ConcurrencyToken);
        }
    }

    [Fact]
    public void Abgeglichene_Inventur_bleibt_nach_Stornierungsversuch_Reconciled()
    {
        var (c, _) = InState(InventoryStatus.Reconciled);

        Assert.Throws<InvalidOperationException>(() => c.Cancel());

        Assert.Equal(InventoryStatus.Reconciled, c.Status);
        Assert.NotNull(c.ReconciledAt);
    }

    [Theory]
    [InlineData(InventoryStatus.Reconciled)]
    [InlineData(InventoryStatus.Cancelled)]
    public void Abgeschlossene_oder_stornierte_Inventur_ist_nicht_mehr_aenderbar(InventoryStatus from)
    {
        var (c, lineId) = InState(from);

        Assert.Throws<InvalidOperationException>(() => c.SetCount(lineId, 5, null));
        Assert.Throws<InvalidOperationException>(() => c.AddSnapshotLine(Guid.NewGuid(), "A-02", Guid.NewGuid(), "SKU-2", 1));
        Assert.Throws<InvalidOperationException>(() => c.Reconcile());
        Assert.Equal(from, c.Status);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(InventoryCount.MaxQuantity + 1)]
    [InlineData(int.MaxValue)]
    public void SetCount_lehnt_Mengen_ausserhalb_0_bis_1_Million_ab(int qty)
    {
        var (c, lineId) = InState(InventoryStatus.Open);

        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetCount(lineId, qty, null));

        Assert.Null(c.Lines.Single().CountedQty);
    }

    [Theory]
    [InlineData(0)] // leer gezählt ist eine gültige Zählung
    [InlineData(InventoryCount.MaxQuantity)]
    public void SetCount_akzeptiert_die_Grenzwerte(int qty)
    {
        var (c, lineId) = InState(InventoryStatus.Open);

        c.SetCount(lineId, qty, null);

        Assert.Equal(qty, c.Lines.Single().CountedQty);
    }
}
