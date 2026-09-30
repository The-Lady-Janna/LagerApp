using Lager.Domain.Stock;

namespace Lager.Tests.WP06;

/// <summary>Domain-Unit-Tests (ohne Host) für die Statusmaschine von <see cref="ReplenishmentTask"/>.</summary>
public class ReplenishmentTaskStateTests
{
    private static ReplenishmentTask InState(ReplenishmentStatus status)
    {
        var t = new ReplenishmentTask(Guid.NewGuid(), "SKU-1", Guid.NewGuid(), "R-01", Guid.NewGuid(), "H-01", 20);
        switch (status)
        {
            case ReplenishmentStatus.Completed: t.Complete(20); break;
            case ReplenishmentStatus.Cancelled: t.Cancel(); break;
        }
        return t;
    }

    [Theory]
    [InlineData(ReplenishmentStatus.Open, true)]
    [InlineData(ReplenishmentStatus.Completed, false)]
    [InlineData(ReplenishmentStatus.Cancelled, false)]
    public void Cancel_ist_nur_aus_Open_erlaubt(ReplenishmentStatus from, bool allowed)
    {
        var t = InState(from);
        var tokenBefore = t.ConcurrencyToken;

        if (allowed)
        {
            t.Cancel();
            Assert.Equal(ReplenishmentStatus.Cancelled, t.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => t.Cancel());
            Assert.Equal(from, t.Status);
            Assert.Equal(tokenBefore, t.ConcurrencyToken);
        }
    }

    [Fact]
    public void Abgeschlossene_Aufgabe_bleibt_nach_Stornierungsversuch_Completed()
    {
        var t = InState(ReplenishmentStatus.Open);
        t.Complete(15);

        Assert.Throws<InvalidOperationException>(() => t.Cancel());

        Assert.Equal(ReplenishmentStatus.Completed, t.Status);
        Assert.Equal(15, t.CompletedQty);
        Assert.NotNull(t.CompletedAt);
    }

    [Theory]
    [InlineData(ReplenishmentStatus.Completed)]
    [InlineData(ReplenishmentStatus.Cancelled)]
    public void Complete_ist_nur_aus_Open_erlaubt(ReplenishmentStatus from)
    {
        var t = InState(from);

        Assert.Throws<InvalidOperationException>(() => t.Complete(5));
        Assert.Equal(from, t.Status);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(ReplenishmentTask.MaxQuantity + 1)]
    [InlineData(int.MaxValue)]
    public void Complete_lehnt_Mengen_ausserhalb_1_bis_1_Million_ab(int qty)
    {
        var t = InState(ReplenishmentStatus.Open);

        Assert.Throws<ArgumentOutOfRangeException>(() => t.Complete(qty));

        Assert.Equal(ReplenishmentStatus.Open, t.Status);
        Assert.Null(t.CompletedQty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(ReplenishmentTask.MaxQuantity)]
    public void Complete_akzeptiert_die_Grenzwerte(int qty)
    {
        var t = InState(ReplenishmentStatus.Open);

        t.Complete(qty);

        Assert.Equal(ReplenishmentStatus.Completed, t.Status);
        Assert.Equal(qty, t.CompletedQty);
    }
}
