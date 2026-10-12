using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Integrity detectors: one fire and one no-fire case per detector, plus exemptions and bounds.</summary>
public sealed class IntegrityDetectorTests
{
    private sealed class ManualTime(TimeProvider existing) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    private static (IntegrityDetectors Detectors, ManualTime Time) Setup()
    {
        var time = new ManualTime(TimeProvider.System);
        return (new IntegrityDetectors(time), time);
    }

    private static IntegrityDetectors.DetectorThresholds Limits(
        double delta = 5000, int calls = 120, int repeats = 5)
        => new(delta, calls, new HashSet<string>(["gold", "xp", "score"], StringComparer.Ordinal),
            repeats, TimeSpan.FromMinutes(10));

    private static ModuleHookEvent Ledger(string player, string field, double amount, ManualTime time, bool secret = false)
        => ModuleHookDispatcher.Describe("ledger.delta", "game", time.GetUtcNow(), steamId: player,
            hasSecretKey: secret, data: new Dictionary<string, object?> { ["field"] = field, ["delta"] = amount });

    private static ModuleHookEvent Call(string player, string endpoint, ManualTime time)
        => ModuleHookDispatcher.Describe("endpoint.executed", "game", time.GetUtcNow(),
            steamId: player, endpointSlug: endpoint);

    [Fact]
    public void Value_delta_fires_over_the_limit_and_stays_quiet_below_it()
    {
        var (detectors, time) = Setup();
        Assert.Null(detectors.Observe(Ledger("p1", "gold", 4000, time), Limits()));
        var firing = detectors.Observe(Ledger("p1", "gold", 2000, time), Limits());
        Assert.NotNull(firing);
        Assert.Equal(IntegrityDetectors.ValueDelta, firing.Detector);
        Assert.Equal("gold", firing.Evidence["field"]);
        Assert.True(firing.Evidence.ContainsKey("observedDelta"));
    }

    [Fact]
    public void Value_delta_ignores_unwatched_fields_secret_traffic_and_other_players()
    {
        var (detectors, time) = Setup();
        Assert.Null(detectors.Observe(Ledger("p1", "cosmetics", 999999, time), Limits()));
        Assert.Null(detectors.Observe(Ledger("p1", "gold", 999999, time, secret: true), Limits()));
        Assert.Null(detectors.Observe(Ledger("p2", "gold", 100, time), Limits()));
    }

    [Fact]
    public void Call_rate_fires_above_threshold_per_player_per_minute()
    {
        var (detectors, time) = Setup();
        DetectorFiring? firing = null;
        for (var i = 0; i < 4; i++)
            firing = detectors.Observe(Call("p1", "economy_earn", time), Limits(calls: 3));
        Assert.NotNull(firing);
        Assert.Equal(IntegrityDetectors.CallRate, firing.Detector);
    }

    [Fact]
    public void Call_rate_window_resets_each_minute()
    {
        var (detectors, time) = Setup();
        for (var i = 0; i < 3; i++)
            Assert.Null(detectors.Observe(Call("p1", "economy_earn", time), Limits(calls: 3)));
        time.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 3; i++)
            Assert.Null(detectors.Observe(Call("p1", "economy_earn", time), Limits(calls: 3)));
    }

    [Fact]
    public void Forged_revision_fires_while_known_revisions_stay_quiet()
    {
        var (detectors, time) = Setup();
        var forged = ModuleHookDispatcher.Describe("revision.reported", "game", time.GetUtcNow(),
            steamId: "p1", revisionId: 999,
            data: new Dictionary<string, object?> { ["known"] = false });
        var known = ModuleHookDispatcher.Describe("revision.reported", "game", time.GetUtcNow(),
            steamId: "p1", revisionId: 41,
            data: new Dictionary<string, object?> { ["known"] = true });
        var firing = detectors.Observe(forged, Limits());
        Assert.NotNull(firing);
        Assert.Equal(IntegrityDetectors.RevisionTampering, firing.Detector);
        Assert.Null(detectors.Observe(known, Limits()));
    }

    [Fact]
    public void Repeated_conflicts_and_direct_writes_fire_only_at_the_repeat_count()
    {
        var (detectors, time) = Setup();
        DetectorFiring? conflict = null;
        for (var i = 0; i < 5; i++)
            conflict = detectors.Observe(Rejected("p1", "version conflict on rollback-protected write", time), Limits());
        Assert.NotNull(conflict);
        Assert.Equal(IntegrityDetectors.AntiRollbackConflict, conflict.Detector);

        DetectorFiring? direct = null;
        for (var i = 0; i < 4; i++)
            direct = detectors.Observe(Rejected("p2", "direct-write to endpoint-only collection", time), Limits());
        Assert.Null(direct);
        direct = detectors.Observe(Rejected("p2", "direct-write to endpoint-only collection", time), Limits());
        Assert.NotNull(direct);
        Assert.Equal(IntegrityDetectors.DirectWriteAttempt, direct.Detector);
    }

    [Fact]
    public void Unrelated_rejections_never_fire()
    {
        var (detectors, time) = Setup();
        for (var i = 0; i < 10; i++)
            Assert.Null(detectors.Observe(Rejected("p1", "unauthorized: missing key", time), Limits()));
    }

    private static ModuleHookEvent Rejected(string player, string reason, ManualTime time)
        => ModuleHookDispatcher.Describe("request.rejected", "game", time.GetUtcNow(),
            steamId: player, data: new Dictionary<string, object?> { ["reason"] = reason });
}
