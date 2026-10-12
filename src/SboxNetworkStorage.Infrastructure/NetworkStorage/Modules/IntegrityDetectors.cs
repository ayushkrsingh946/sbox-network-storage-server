using System.Collections.Concurrent;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>One detector firing: what raised it and the evidence to store.</summary>
public sealed record DetectorFiring(string Detector, string Severity, Dictionary<string, object?> Evidence);

/// <summary>
/// Sliding-window integrity detectors over hook events. Pure logic over
/// per-(project, player) windows; bounded (at most <see cref="MaxTrackedKeys"/>
/// keys per detector, oldest evicted first). Secret-key (dedicated-server)
/// traffic never raises flags. Thresholds come from the caller so the same
/// code serves defaults, per-project settings and unit tests.
/// </summary>
public sealed class IntegrityDetectors
{
    public const int MaxTrackedKeys = 10_000;

    public const string ValueDelta = "value-delta";
    public const string CallRate = "call-rate";
    public const string RevisionTampering = "revision-tampering";
    public const string AntiRollbackConflict = "anti-rollback-conflict";
    public const string DirectWriteAttempt = "direct-write-attempt";

    public sealed record DetectorThresholds(
        double ValueDeltaPerMinute,
        int MaxCallsPerMinute,
        IReadOnlySet<string> WatchedFields,
        int ConflictRepeatCount = 5,
        TimeSpan ConflictWindow = default);

    public static DetectorThresholds Defaults { get; } = new(
        5000, 120, new HashSet<string>(["gold", "xp", "score"], StringComparer.Ordinal),
        5, TimeSpan.FromMinutes(10));

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public IntegrityDetectors(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    private sealed class Window
    {
        public long WindowStartUnixMs;
        public double Sum;
        public int Count;
        public long TouchedUnixMs;
    }

    /// <summary>Observes one hook event; returns a firing when a detector trips.</summary>
    public DetectorFiring? Observe(ModuleHookEvent hookEvent, DetectorThresholds thresholds)
    {
        if (hookEvent.HasSecretKey) return null;
        var player = hookEvent.SteamId;
        if (string.IsNullOrWhiteSpace(player)) return null;
        return hookEvent.Hook switch
        {
            "ledger.delta" => ObserveLedgerDelta(hookEvent, player, thresholds),
            "endpoint.executed" => ObserveCallRate(hookEvent, player, thresholds),
            "revision.reported" => ObserveRevision(hookEvent, player),
            "request.rejected" => ObserveRejection(hookEvent, player, thresholds),
            _ => null,
        };
    }

    private DetectorFiring? ObserveLedgerDelta(ModuleHookEvent hookEvent, string player, DetectorThresholds thresholds)
    {
        if (!hookEvent.Data.TryGetValue("field", out var fieldObj) || fieldObj is not string field
            || !thresholds.WatchedFields.Contains(field))
            return null;
        if (!hookEvent.Data.TryGetValue("delta", out var deltaObj) || !TryNumber(deltaObj, out var delta))
            return null;
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var window = MinuteWindow($"{hookEvent.ProjectId}\0{player}\0{field}", now);
        window.Sum += Math.Abs(delta);
        if (window.Sum <= thresholds.ValueDeltaPerMinute) return null;
        var observed = Math.Round(window.Sum, 3);
        window.Sum = 0;
        return new DetectorFiring(ValueDelta, "high", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["field"] = field,
            ["observedDelta"] = observed,
            ["limitPerMinute"] = thresholds.ValueDeltaPerMinute,
            ["endpoint"] = hookEvent.EndpointSlug,
        });
    }

    private DetectorFiring? ObserveCallRate(ModuleHookEvent hookEvent, string player, DetectorThresholds thresholds)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var window = MinuteWindow($"{hookEvent.ProjectId}\0{player}", now);
        window.Count++;
        if (window.Count <= thresholds.MaxCallsPerMinute) return null;
        window.Count = 0;
        return new DetectorFiring(CallRate, "medium", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["callsPerMinute"] = thresholds.MaxCallsPerMinute + 1,
            ["limitPerMinute"] = thresholds.MaxCallsPerMinute,
            ["endpoint"] = hookEvent.EndpointSlug,
        });
    }

    private static DetectorFiring? ObserveRevision(ModuleHookEvent hookEvent, string player)
    {
        if (!hookEvent.Data.TryGetValue("known", out var knownObj) || knownObj is true) return null;
        return new DetectorFiring(RevisionTampering, "high", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["revisionId"] = hookEvent.RevisionId,
            ["reason"] = "The client reported a revision id that was never published.",
        });
    }

    private DetectorFiring? ObserveRejection(ModuleHookEvent hookEvent, string player, DetectorThresholds thresholds)
    {
        if (!hookEvent.Data.TryGetValue("reason", out var reasonObj) || reasonObj is not string reason)
            return null;
        var windowMinutes = thresholds.ConflictWindow == default ? TimeSpan.FromMinutes(10) : thresholds.ConflictWindow;
        if (reason.Contains("rollback", StringComparison.OrdinalIgnoreCase) || reason.Contains("conflict", StringComparison.OrdinalIgnoreCase))
            return ObserveRepeat(hookEvent, player, AntiRollbackConflict, "medium", reason, "Repeated version conflicts against rollback-protected writes.", thresholds.ConflictRepeatCount, windowMinutes);
        if (reason.Contains("endpoint-only", StringComparison.OrdinalIgnoreCase) || reason.Contains("direct-write", StringComparison.OrdinalIgnoreCase))
            return ObserveRepeat(hookEvent, player, DirectWriteAttempt, "medium", reason, "Repeated direct writes to endpoint-only collections.", thresholds.ConflictRepeatCount, windowMinutes);
        return null;
    }

    private DetectorFiring? ObserveRepeat(ModuleHookEvent hookEvent, string player, string detector,
        string severity, string reason, string description, int repeatCount, TimeSpan window)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var key = $"{hookEvent.ProjectId}\0{player}\0{detector}";
        var entry = _windows.GetOrAdd(key, _ => new Window { WindowStartUnixMs = now, TouchedUnixMs = now });
        lock (entry)
        {
            if (now - entry.WindowStartUnixMs > window.TotalMilliseconds)
            {
                entry.WindowStartUnixMs = now;
                entry.Count = 0;
            }
            entry.Count++;
            entry.TouchedUnixMs = now;
            if (entry.Count < repeatCount) return null;
            entry.Count = 0;
            entry.WindowStartUnixMs = now;
        }
        return new DetectorFiring(detector, severity, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["description"] = description,
            ["occurrences"] = repeatCount,
            ["windowMinutes"] = window.TotalMinutes,
            ["endpoint"] = hookEvent.EndpointSlug,
        });
    }

    private Window MinuteWindow(string key, long now)
    {
        EvictIfFull();
        var window = _windows.GetOrAdd(key, _ => new Window { WindowStartUnixMs = now, TouchedUnixMs = now });
        lock (window)
        {
            if (now - window.WindowStartUnixMs >= 60_000)
            {
                window.WindowStartUnixMs = now;
                window.Sum = 0;
                window.Count = 0;
            }
            window.TouchedUnixMs = now;
        }
        return window;
    }

    private void EvictIfFull()
    {
        if (_windows.Count < MaxTrackedKeys * 2) return;
        var cutoff = _windows.Values.Select(window => window.TouchedUnixMs).OrderBy(touched => touched)
            .ElementAtOrDefault(_windows.Count - MaxTrackedKeys);
        foreach (var key in _windows.Where(pair => pair.Value.TouchedUnixMs <= cutoff).Select(pair => pair.Key).Take(_windows.Count - MaxTrackedKeys).ToList())
            _windows.TryRemove(key, out _);
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case double d: number = d; return true;
            case float f: number = f; return true;
            case long l: number = l; return true;
            case int i: number = i; return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out number): return true;
            default: number = 0; return false;
        }
    }
}
