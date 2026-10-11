using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Storage;
using Xunit;
namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>Client-version telemetry batches by identity; repeated players do not double-count and groups are bounded.</summary>
public sealed class ClientVersionTrackerTests
{
    private static ClientIdentity Identity(string version, long? revision = null, string? clientType = "game", long? gameRevision = 42)
    {
        var library = ClientIdentity.TryParseLibraryVersion(version, out var parsed) ? parsed : null;
        return new ClientIdentity(library?.ToString() ?? ClientIdentity.Legacy, library, revision, clientType, gameRevision, null);
    }

    [Fact]
    public void Ten_thousand_requests_batch_by_identity_and_repeated_players_do_not_double_count()
    {
        var tracker = new ClientVersionTracker(TimeProvider.System);
        var identity = Identity("1.1.0", 309133);
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
            tracker.Record("proj_example", Identity("1.1.0", null, "game", i + 1), null);
        Assert.Equal(1, tracker.Dropped);
        tracker.Drain();
        tracker.Record("proj_example", Identity("1.1.0", null, "game", ClientVersionTracker.MaxGroups + 1), null);
        Assert.Equal(2, tracker.Dropped); // Flushing must not reset the daily cardinality budget.
        tracker.Record("proj_example", Identity("1.1.0", null, "game", 1), null);
        Assert.Equal(1, Assert.Single(tracker.Drain()).Requests); // Existing groups remain observable at the bound.
        var merged = new byte[PlayerCountSketch.Size];
        var left = new byte[PlayerCountSketch.Size];
        var right = new byte[PlayerCountSketch.Size];
        for (var i = 0; i < 1000; i++) PlayerCountSketch.Add(left, "proj_example", (76561198000000000L + i).ToString());
        for (var i = 500; i < 1500; i++) PlayerCountSketch.Add(right, "proj_example", (76561198000000000L + i).ToString());
        PlayerCountSketch.Merge(merged, left); PlayerCountSketch.Merge(merged, right);
        Assert.InRange(PlayerCountSketch.Estimate(merged), 1350, 1650);
    }

    [Fact]
    public void Legacy_and_unparseable_versions_share_the_legacy_bucket()
    {
        var tracker = new ClientVersionTracker(TimeProvider.System);
        tracker.Record("proj_example", Identity("legacy"), null);
        tracker.Record("proj_example", Identity("garbage"), null);
        var single = Assert.Single(tracker.Drain());
        Assert.Equal(2, single.Requests);
        Assert.Equal(ClientIdentity.Legacy, single.Key.LibraryVersion);
    }
}
