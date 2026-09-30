using Lager.Contracts.PickLists;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP07;

/// <summary>Wellen: Release genau einmal, Abschluss, Abbruch.</summary>
public class PickWaveFlowTests : IClassFixture<PickApiFixture>
{
    private readonly PickWorld _w;

    public PickWaveFlowTests(PickApiFixture fixture) => _w = fixture.World;

    /// <summary>Zwei Bestellungen auf demselben Artikel mit genug Bestand, dazu eine offene Welle mit beiden.</summary>
    private async Task<(Guid Article, Guid O1, Guid O2, PickWaveDto Wave)> WaveAsync()
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 100);
        var o1 = await _w.AddOrderAsync((article, 5));
        var o2 = await _w.AddOrderAsync((article, 7));
        var wave = await _w.WavesAsync(s => s.CreateAsync(new CreatePickWaveRequest("Test", null, new[] { o1, o2 })));
        return (article, o1, o2, wave);
    }

    private Task<PickWaveDto?> ReleaseAsync(Guid waveId) =>
        _w.WavesAsync(s => s.ReleaseAsync(waveId, new ReleaseWaveRequest()));

    private Task<int> ListCountForAsync(params Guid[] orderIds) =>
        _w.DbAsync(db => db.PickItems.Where(i => orderIds.Contains(i.OrderId)).Select(i => i.PickListId).Distinct().CountAsync());

    [Fact]
    public async Task Release_creates_exactly_one_pick_list_and_a_second_release_is_rejected()
    {
        var (_, o1, o2, wave) = await WaveAsync();

        var released = await ReleaseAsync(wave.Id);

        Assert.Equal("Released", released!.Status);
        var listId = Assert.Single(released.PickListIds);
        Assert.Equal(1, await ListCountForAsync(o1, o2));
        Assert.Equal(OrderStatus.Picking, await _w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.Picking, await _w.OrderStatusAsync(o2));

        // Doppelklick / Retry: abgelehnt, KEINE zweite Pickliste (früher: erst committen, dann werfen)
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReleaseAsync(wave.Id));
        Assert.Equal(1, await ListCountForAsync(o1, o2));
        var reread = await _w.WavesAsync(s => s.GetAsync(wave.Id));
        Assert.Equal(new[] { listId }, reread!.PickListIds);
    }

    [Fact]
    public async Task Release_of_a_cancelled_wave_creates_nothing()
    {
        var (_, o1, o2, wave) = await WaveAsync();
        Assert.True(await _w.WavesAsync(s => s.CancelAsync(wave.Id)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReleaseAsync(wave.Id));

        Assert.Equal(0, await ListCountForAsync(o1, o2));
        Assert.Equal(OrderStatus.New, await _w.OrderStatusAsync(o1));
    }

    [Fact]
    public async Task Failed_release_leaves_the_wave_open_and_the_orders_untouched()
    {
        // Bestand reicht nur für eine der beiden Bestellungen: das ganze Release scheitert atomar.
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 10);
        var o1 = await _w.AddOrderAsync((article, 8));
        var o2 = await _w.AddOrderAsync((article, 8));
        var wave = await _w.WavesAsync(s => s.CreateAsync(new CreatePickWaveRequest(null, null, new[] { o1, o2 })));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReleaseAsync(wave.Id));

        var reread = await _w.WavesAsync(s => s.GetAsync(wave.Id));
        Assert.Equal("Open", reread!.Status);
        Assert.Empty(reread.PickListIds);
        Assert.Equal(OrderStatus.New, await _w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.New, await _w.OrderStatusAsync(o2));
    }

    [Fact]
    public async Task Wave_becomes_Completed_when_its_pick_list_is_packed()
    {
        var (_, _, _, wave) = await WaveAsync();
        var released = await ReleaseAsync(wave.Id);
        var listId = released!.PickListIds.Single();
        var list = await _w.PickListsAsync(s => s.GetAsync(listId));

        // noch nicht verpackt: Welle bleibt Released
        await _w.MarkPickedAsync(listId);
        Assert.Equal("Released", (await _w.WavesAsync(s => s.GetAsync(wave.Id)))!.Status);

        await _w.PackAllAsync(list!);

        var completed = await _w.WavesAsync(s => s.GetAsync(wave.Id));
        Assert.Equal("Completed", completed!.Status);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task Cancel_of_a_released_wave_cancels_the_unstarted_list_and_frees_the_orders()
    {
        var (_, o1, o2, wave) = await WaveAsync();
        var released = await ReleaseAsync(wave.Id);
        var listId = released!.PickListIds.Single();

        Assert.True(await _w.WavesAsync(s => s.CancelAsync(wave.Id)));

        Assert.Equal("Cancelled", (await _w.WavesAsync(s => s.GetAsync(wave.Id)))!.Status);
        Assert.Equal(PickListStatus.Cancelled, await _w.PickListStatusAsync(listId));
        Assert.Equal(OrderStatus.New, await _w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.New, await _w.OrderStatusAsync(o2));

        // ein zweiter Abbruch ist ein Fehler, keine stille Wiederholung
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.WavesAsync(s => s.CancelAsync(wave.Id)));
    }

    [Fact]
    public async Task Cancel_is_rejected_once_a_pick_list_of_the_wave_has_been_started()
    {
        var (_, o1, _, wave) = await WaveAsync();
        var released = await ReleaseAsync(wave.Id);
        var listId = released!.PickListIds.Single();
        await _w.MarkPickedAsync(listId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.WavesAsync(s => s.CancelAsync(wave.Id)));

        Assert.Equal("Released", (await _w.WavesAsync(s => s.GetAsync(wave.Id)))!.Status);
        Assert.Equal(PickListStatus.Picked, await _w.PickListStatusAsync(listId));
        Assert.Equal(OrderStatus.Picked, await _w.OrderStatusAsync(o1));
    }

    [Fact]
    public async Task Cancel_of_an_open_wave_is_allowed_and_a_completed_wave_cannot_be_cancelled()
    {
        var (_, _, _, open) = await WaveAsync();
        Assert.True(await _w.WavesAsync(s => s.CancelAsync(open.Id)));

        var (_, _, _, wave) = await WaveAsync();
        var listId = (await ReleaseAsync(wave.Id))!.PickListIds.Single();
        var list = await _w.PickListsAsync(s => s.GetAsync(listId));
        await _w.PackAllAsync(list!);
        Assert.Equal("Completed", (await _w.WavesAsync(s => s.GetAsync(wave.Id)))!.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.WavesAsync(s => s.CancelAsync(wave.Id)));
    }

    [Fact]
    public async Task Wave_numbers_are_unique_and_increasing()
    {
        var (_, _, _, a) = await WaveAsync();
        var (_, _, _, b) = await WaveAsync();

        Assert.NotEqual(a.WaveNumber, b.WaveNumber);
        Assert.True(string.CompareOrdinal(a.WaveNumber, b.WaveNumber) < 0);
    }
}
