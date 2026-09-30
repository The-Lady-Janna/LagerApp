using Lager.Domain.PickLists;
using Lager.Domain.Stock;

namespace Lager.Tests.WP07;

/// <summary>Domain-Unit-Tests (ohne Host) für die Status-Guards von Pickliste, Position, Welle und Bestand.</summary>
public class PickDomainGuardTests
{
    private static PickItem Item(int quantity = 5) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quantity);

    private static PickList List(PickListStatus status)
    {
        var pl = new PickList("PL-TEST", new[] { Item() }, 0);
        switch (status)
        {
            case PickListStatus.Pending: break;
            case PickListStatus.InProgress: pl.Assign("anna"); break;
            case PickListStatus.Picked: pl.MarkPickingComplete(); break;
            case PickListStatus.Completed: pl.MarkPacked(); break;
            case PickListStatus.Cancelled: pl.Cancel(); break;
        }
        Assert.Equal(status, pl.Status);
        return pl;
    }

    // ---- PickList -------------------------------------------------------

    [Theory]
    [InlineData(PickListStatus.Pending, true)]
    [InlineData(PickListStatus.InProgress, true)]
    [InlineData(PickListStatus.Picked, true)]
    [InlineData(PickListStatus.Completed, false)]
    [InlineData(PickListStatus.Cancelled, false)]
    public void MarkPacked_is_only_allowed_before_completion(PickListStatus from, bool allowed)
    {
        var pl = List(from);

        if (allowed)
        {
            pl.MarkPacked();
            Assert.Equal(PickListStatus.Completed, pl.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => pl.MarkPacked());
            Assert.Equal(from, pl.Status);
        }
    }

    [Theory]
    [InlineData(PickListStatus.Pending, true)]
    [InlineData(PickListStatus.InProgress, true)]
    [InlineData(PickListStatus.Picked, false)]
    [InlineData(PickListStatus.Completed, false)]
    [InlineData(PickListStatus.Cancelled, false)]
    public void Assign_and_ReplaceItems_are_only_allowed_while_the_list_is_open(PickListStatus from, bool allowed)
    {
        var pl = List(from);
        var fresh = new[] { Item(2), Item(3) };

        if (allowed)
        {
            pl.Assign("berta");
            pl.ReplaceItems(fresh);
            Assert.Equal(2, pl.Items.Count);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => pl.Assign("berta"));
            Assert.Throws<InvalidOperationException>(() => pl.ReplaceItems(fresh));
            Assert.Throws<InvalidOperationException>(() => pl.UpdateRouteSummary(1, Array.Empty<Lager.Domain.Warehouse.Position>()));
            Assert.Single(pl.Items);                               // die alten Items sind unangetastet
        }
    }

    [Theory]
    [InlineData(PickListStatus.Pending, true)]
    [InlineData(PickListStatus.InProgress, true)]
    [InlineData(PickListStatus.Picked, true)]
    [InlineData(PickListStatus.Completed, false)]
    [InlineData(PickListStatus.Cancelled, false)]
    public void Cancel_is_only_allowed_without_stock_effect(PickListStatus from, bool allowed)
    {
        var pl = List(from);

        if (allowed)
        {
            pl.Cancel();
            Assert.Equal(PickListStatus.Cancelled, pl.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => pl.Cancel());
            Assert.Equal(from, pl.Status);
        }
    }

    [Theory]
    [InlineData(PickListStatus.Completed)]
    [InlineData(PickListStatus.Cancelled)]
    public void MarkPickingComplete_is_rejected_for_final_lists(PickListStatus from)
    {
        var pl = List(from);
        Assert.Throws<InvalidOperationException>(() => pl.MarkPickingComplete());
        Assert.Equal(from, pl.Status);
    }

    [Fact]
    public void ReplaceItems_keeps_the_old_items_when_the_new_set_is_empty()
    {
        var pl = List(PickListStatus.Pending);

        Assert.Throws<ArgumentException>(() => pl.ReplaceItems(Array.Empty<PickItem>()));
        Assert.Single(pl.Items);
    }

    // ---- PickItem -------------------------------------------------------

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]     // Obergrenze: mehr als geplant gibt es nicht
    [InlineData(-1, false)]
    public void ConfirmPacked_only_accepts_zero_up_to_the_planned_quantity(int actual, bool ok)
    {
        var item = Item(5);

        if (ok)
        {
            item.ConfirmPacked(actual);
            Assert.Equal(actual, item.ConfirmedQuantity);
            Assert.True(item.IsConfirmed);
            Assert.True(item.Picked);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => item.ConfirmPacked(actual));
            Assert.Null(item.ConfirmedQuantity);
            Assert.False(item.IsConfirmed);
        }
    }

    [Fact]
    public void ConfirmPacked_cannot_be_repeated()
    {
        var item = Item(5);
        item.ConfirmPacked(4);

        Assert.Throws<InvalidOperationException>(() => item.ConfirmPacked(5));
        Assert.Equal(4, item.ConfirmedQuantity);
    }

    // ---- PickWave -------------------------------------------------------

    private static PickWave Wave(PickWaveStatus status)
    {
        var wave = new PickWave("W-TEST", null, null);
        wave.AddOrders(new[] { Guid.NewGuid() });
        switch (status)
        {
            case PickWaveStatus.Open: break;
            case PickWaveStatus.Released: wave.MarkReleased(new[] { Guid.NewGuid() }); break;
            case PickWaveStatus.Completed: wave.MarkReleased(new[] { Guid.NewGuid() }); wave.MarkCompleted(); break;
            case PickWaveStatus.Cancelled: wave.Cancel(); break;
        }
        Assert.Equal(status, wave.Status);
        return wave;
    }

    [Theory]
    [InlineData(PickWaveStatus.Open, true)]
    [InlineData(PickWaveStatus.Released, true)]
    [InlineData(PickWaveStatus.Completed, false)]
    [InlineData(PickWaveStatus.Cancelled, false)]
    public void Wave_Cancel_is_rejected_for_finished_waves(PickWaveStatus from, bool allowed)
    {
        var wave = Wave(from);

        if (allowed)
        {
            wave.Cancel();
            Assert.Equal(PickWaveStatus.Cancelled, wave.Status);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => wave.Cancel());
            Assert.Equal(from, wave.Status);
        }
    }

    [Fact]
    public void Wave_completes_only_from_Released_and_is_idempotent_once_completed()
    {
        Assert.Throws<InvalidOperationException>(() => Wave(PickWaveStatus.Open).MarkCompleted());
        Assert.Throws<InvalidOperationException>(() => Wave(PickWaveStatus.Cancelled).MarkCompleted());

        var wave = Wave(PickWaveStatus.Released);
        wave.MarkCompleted();
        Assert.Equal(PickWaveStatus.Completed, wave.Status);
        Assert.NotNull(wave.CompletedAt);

        var completedAt = wave.CompletedAt;
        wave.MarkCompleted();
        Assert.Equal(completedAt, wave.CompletedAt);
    }

    // ---- StockItem ------------------------------------------------------

    [Fact]
    public void StockItem_normalizes_the_lot_and_detects_expiry_by_date()
    {
        Assert.Null(new StockItem(Guid.NewGuid(), Guid.NewGuid(), 1, "   ").LotNumber);
        Assert.Null(new StockItem(Guid.NewGuid(), Guid.NewGuid(), 1, "").LotNumber);
        Assert.Equal("L-1", new StockItem(Guid.NewGuid(), Guid.NewGuid(), 1, " L-1 ").LotNumber);

        var today = new DateTime(2026, 9, 30, 12, 0, 0);
        Assert.False(new StockItem(Guid.NewGuid(), Guid.NewGuid(), 1).IsExpired(today));
        Assert.False(new StockItem(Guid.NewGuid(), Guid.NewGuid(), 1, null, new DateTime(2026, 9, 30)).IsExpired(today));
        Assert.True(new StockItem(Guid.NewGuid(), Guid.NewGuid(), 1, null, new DateTime(2026, 9, 29)).IsExpired(today));
    }

    [Fact]
    public void StockItem_Add_refuses_to_overflow()
    {
        var stock = new StockItem(Guid.NewGuid(), Guid.NewGuid(), int.MaxValue - 1);

        Assert.Throws<InvalidOperationException>(() => stock.Add(2));
        Assert.Equal(int.MaxValue - 1, stock.Quantity);

        stock.Add(1);
        Assert.Equal(int.MaxValue, stock.Quantity);
    }
}
