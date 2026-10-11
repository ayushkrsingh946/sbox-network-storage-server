using SboxNetworkStorage.Server.Compatibility;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class ClientVersionAnalyticsTests
{
    [Fact]
    public void Ten_thousand_requests_batch_by_identity_and_repeated_players_do_not_double_count()
    {
        var tracker = new ClientVersionTracker(TimeProvider.System);
        var identity = new ClientIdentity("1.1.0", 309133, "game", 42);
        for (var i = 0; i < 10000; i++) tracker.Record("proj_example", identity, "76561198000000001");
        var first = Assert.Single(tracker.Drain());
        Assert.Equal(10000, first.Requests);
        Assert.Equal(1, PlayerCountSketch.Estimate(first.PlayerSketch));
        tracker.Record("proj_example", identity, "76561198000000001");
        PlayerCountSketch.Merge(first.PlayerSketch, Assert.Single(tracker.Drain()).PlayerSketch);
        Assert.Equal(1, PlayerCountSketch.Estimate(first.PlayerSketch));
    }
    [Fact]
    public void Cardinality_is_bounded_and_estimates_merge_across_flushes()
    {
        var tracker = new ClientVersionTracker(TimeProvider.System);
        for (var i = 0; i < ClientVersionTracker.MaxGroups + 1; i++)
            tracker.Record("proj_example", new("1.1.0", null, "game", i + 1), null);
        Assert.Equal(1, tracker.Dropped);
        tracker.Drain();
        tracker.Record("proj_example", new("1.1.0", null, "game", ClientVersionTracker.MaxGroups + 1), null);
        Assert.Equal(2, tracker.Dropped); // Flushing must not reset the daily cardinality budget.
        tracker.Record("proj_example", new("1.1.0", null, "game", 1), null);
        Assert.Equal(1, Assert.Single(tracker.Drain()).Requests); // Existing groups remain observable at the bound.
        var merged = new byte[PlayerCountSketch.Size];
        var left = new byte[PlayerCountSketch.Size];
        var right = new byte[PlayerCountSketch.Size];
        for (var i = 0; i < 1000; i++) PlayerCountSketch.Add(left, "proj_example", (76561198000000000L + i).ToString());
        for (var i = 500; i < 1500; i++) PlayerCountSketch.Add(right, "proj_example", (76561198000000000L + i).ToString());
        PlayerCountSketch.Merge(merged, left); PlayerCountSketch.Merge(merged, right);
        Assert.InRange(PlayerCountSketch.Estimate(merged), 1350, 1650);
    }
}
