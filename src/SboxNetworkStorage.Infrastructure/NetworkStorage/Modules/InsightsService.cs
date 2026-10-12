using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>One session sample for overview math.</summary>
public sealed record SessionSample(string SteamId, long StartedAtUnixMs, long? EndedAtUnixMs, long? LastHeartbeatAtUnixMs);

/// <summary>Metrics for one window, with insufficient-data states instead of placeholders.</summary>
public sealed record WindowMetrics(
    int WindowDays,
    long ActivePlayers,
    long NewPlayers,
    long Sessions,
    double? MedianSessionSeconds,
    bool EnoughData);

/// <summary>Player overview across the 1/7/30-day windows plus return rates.</summary>
public sealed record PlayerOverview(
    IReadOnlyList<WindowMetrics> Windows,
    double? Day1ReturnRate,
    double? Day7ReturnRate,
    bool Day7EnoughData,
    long DroppedLogEntries);

/// <summary>
/// Pure overview math over session samples. No store access: the service maps
/// rows to samples, tests feed fixtures directly. Never fabricates figures —
/// windows without sessions report <c>EnoughData: false</c> and null medians.
/// </summary>
public static class InsightsCalculator
{
    public static PlayerOverview Summarize(IReadOnlyList<SessionSample> sessions, long nowUnixMs, long droppedLogEntries)
    {
        var windows = new[] { 1, 7, 30 }
            .Select(days => Window(sessions, nowUnixMs, days)).ToList();
        var (d1, d1Enough) = ReturnRate(sessions, nowUnixMs, 1);
        var (d7, d7Enough) = ReturnRate(sessions, nowUnixMs, 7);
        return new PlayerOverview(windows, d1, d7, d7Enough, droppedLogEntries);
    }

    private static WindowMetrics Window(IReadOnlyList<SessionSample> sessions, long nowUnixMs, int days)
    {
        var cutoff = nowUnixMs - (long)days * 86_400_000;
        var inWindow = sessions.Where(session => session.StartedAtUnixMs >= cutoff).ToList();
        if (inWindow.Count == 0)
            return new WindowMetrics(days, 0, 0, 0, null, false);
        var active = inWindow.Select(session => session.SteamId).Distinct(StringComparer.Ordinal).Count();
        var firstSeen = sessions.GroupBy(session => session.SteamId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(session => session.StartedAtUnixMs), StringComparer.Ordinal);
        var fresh = firstSeen.Count(pair => pair.Value >= cutoff);
        var lengths = inWindow
            .Select(session => SessionLengthSeconds(session, nowUnixMs))
            .Where(length => length.HasValue)
            .Select(length => length!.Value)
            .OrderBy(length => length)
            .ToList();
        double? median = lengths.Count == 0 ? null : lengths[lengths.Count / 2];
        return new WindowMetrics(days, active, fresh, inWindow.Count, median, true);
    }

    private static double? SessionLengthSeconds(SessionSample session, long nowUnixMs)
    {
        var end = session.EndedAtUnixMs ?? session.LastHeartbeatAtUnixMs;
        if (!end.HasValue || end.Value < session.StartedAtUnixMs) return null;
        if (end.Value > nowUnixMs) end = nowUnixMs;
        return (end.Value - session.StartedAtUnixMs) / 1000.0;
    }

    private static (double? Rate, bool EnoughData) ReturnRate(IReadOnlyList<SessionSample> sessions, long nowUnixMs, int daysLater)
    {
        var dayMs = 86_400_000L;
        var firstSeen = sessions.GroupBy(session => session.SteamId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(session => session.StartedAtUnixMs), StringComparer.Ordinal);
        var activeDays = sessions.GroupBy(session => session.SteamId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(session => session.StartedAtUnixMs / dayMs).ToHashSet(), StringComparer.Ordinal);
        var cohort = firstSeen.Where(pair => nowUnixMs - pair.Value >= (daysLater + 1) * dayMs).ToList();
        if (cohort.Count == 0) return (null, false);
        var returned = cohort.Count(pair =>
            activeDays.TryGetValue(pair.Key, out var days) && days.Contains(pair.Value / dayMs + daysLater));
        return ((double)returned / cohort.Count, true);
    }
}

/// <summary>One CSV-ready action log row.</summary>
public sealed record ActionLogRow(
    long CreatedAtUnixMs, string SteamId, string? EndpointSlug, long? RevisionId, string? Outcome, string? ChangedFields);

/// <summary>
/// Player insights built from data the server already records: sessions and
/// profiles for the overview, store reads for search and profile, and the
/// module action-log table for the per-player log. No new client events.
/// </summary>
public sealed class InsightsService(
    INetworkStorageStore store,
    IModuleDataStore modules,
    PlayerActionLogWriter actionLog,
    TimeProvider time)
{
    public const int MaxSessionsScanned = 5000;
    public const int MaxProfilesScanned = 2000;
    public const int MaxSearchResults = 50;

    public async Task<PlayerOverview?> OverviewAsync(string projectId, CancellationToken ct)
    {
        var sessions = await LoadSamplesAsync(projectId, steamId: null, ct);
        if (sessions.Count == 0) return null;
        return InsightsCalculator.Summarize(sessions, time.GetUtcNow().ToUnixTimeMilliseconds(), actionLog.DroppedFor(projectId));
    }

    public async Task<IReadOnlyList<JsonElement>> SearchAsync(string projectId, string query, CancellationToken ct)
    {
        var profiles = await store.ReadProjectProfilesAsync(projectId, ct);
        var trimmed = query.Trim();
        var matches = profiles
            .Where(row => row.ValueKind == JsonValueKind.Object
                && (string.Equals(Text(row, "steam_id"), trimmed, StringComparison.Ordinal)
                    || (Text(row, "player_name") ?? string.Empty).Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxSearchResults)
            .ToList();
        return matches;
    }

    public sealed record PlayerProfileResult(
        JsonElement? Profile,
        IReadOnlyList<JsonElement> Sessions,
        IReadOnlyDictionary<string, JsonElement> Records,
        IReadOnlyList<JsonElement> Ledger,
        IReadOnlyList<JsonElement> Flags,
        JsonElement? Block,
        long? LastRevisionId);

    public async Task<PlayerProfileResult?> ProfileAsync(string projectId, string steamId, CancellationToken ct)
    {
        var profile = await store.ReadPlayerProfileAsync(projectId, steamId, ct);
        var sessions = await modules.ListSessionsAsync(projectId, steamId, 50, ct);
        if (!profile.HasValue && sessions.Count == 0)
        {
            var flags = await modules.ListFlagsAsync(projectId, state: null, detector: null, severity: null, steamId, 10, ct);
            var logs = await modules.ListActionLogAsync(projectId, steamId, endpointSlug: null, outcome: null,
                fromUnixMs: null, toUnixMs: null, 1, ct);
            if (flags.Count == 0 && logs.Count == 0) return null;
        }
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var ledger = new List<JsonElement>();
        foreach (var collection in (await store.ListCollectionsAsync(projectId, ct)).Take(50))
        {
            if (collection.ValueKind != JsonValueKind.Object) continue;
            var collectionId = Text(collection, "collection_id");
            if (string.IsNullOrEmpty(collectionId)) continue;
            var record = await store.ReadRecordAsync(projectId, collectionId, steamId, ct);
            if (!record.HasValue || record.Value.ValueKind != JsonValueKind.Object) continue;
            records[collectionId] = record.Value;
            foreach (var entry in await store.ListLedgerEntriesAsync(projectId, collectionId, steamId, ct))
                ledger.Add(entry);
        }
        var playerFlags = await modules.ListFlagsAsync(projectId, state: null, detector: null, severity: null, steamId, 100, ct);
        var block = await modules.ReadBlockAsync(projectId, steamId, ct);
        var latestLog = await modules.ListActionLogAsync(projectId, steamId, endpointSlug: null, outcome: null,
            fromUnixMs: null, toUnixMs: null, 1, ct);
        long? lastRevision = null;
        if (latestLog.Count > 0 && latestLog[0].TryGetProperty("revision_id", out var revision)
            && revision.ValueKind == JsonValueKind.Number)
            lastRevision = revision.GetInt64();
        return new PlayerProfileResult(profile, sessions, records, ledger.OrderByDescending(LedgerTime).Take(100).ToList(),
            playerFlags, block, lastRevision);
    }

    public Task<IReadOnlyList<JsonElement>> ActionLogAsync(string projectId, string? steamId, string? endpointSlug,
        string? outcome, long? fromUnixMs, long? toUnixMs, int limit, CancellationToken ct)
        => modules.ListActionLogAsync(projectId, steamId, endpointSlug, outcome, fromUnixMs, toUnixMs,
            Math.Clamp(limit, 1, 1000), ct);

    public static string ToCsv(IEnumerable<ActionLogRow> rows)
    {
        var output = new StringBuilder();
        output.AppendLine("created_at,steam_id,endpoint_slug,revision_id,outcome,changed_fields");
        foreach (var row in rows)
        {
            output.Append(DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUnixMs).ToString("o"));
            output.Append(',');
            output.Append(Escape(row.SteamId));
            output.Append(',');
            output.Append(Escape(row.EndpointSlug ?? string.Empty));
            output.Append(',');
            output.Append(row.RevisionId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            output.Append(',');
            output.Append(Escape(row.Outcome ?? string.Empty));
            output.Append(',');
            output.Append(Escape(row.ChangedFields ?? string.Empty));
            output.AppendLine();
        }
        return output.ToString();
    }

    public static ActionLogRow ToRow(JsonElement row) => new(
        row.TryGetProperty("created_at_unix_ms", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0,
        Text(row, "steam_id") ?? string.Empty,
        Text(row, "endpoint_slug"),
        row.TryGetProperty("revision_id", out var revision) && revision.ValueKind == JsonValueKind.Number ? revision.GetInt64() : null,
        Text(row, "outcome"),
        row.TryGetProperty("changed_fields_json", out var changed) && changed.ValueKind != JsonValueKind.Null
            ? changed.ValueKind == JsonValueKind.String ? changed.GetString() : changed.GetRawText() : null);

    /// <summary>
    /// CSV-escapes one cell. Quoting handles separators; a leading single
    /// quote additionally neutralizes spreadsheet formula injection
    /// (cells starting with =, +, -, @, tab or carriage return).
    /// </summary>
    private static string Escape(string value)
    {
        if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'))
            value = "'" + value;
        if (value.IndexOfAny(['"', ',', '\n', '\r']) < 0) return value;
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static long LedgerTime(JsonElement entry)
        => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("created_at_unix_ms", out var at)
            && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0;

    private async Task<IReadOnlyList<SessionSample>> LoadSamplesAsync(string projectId, string? steamId, CancellationToken ct)
    {
        var samples = new List<SessionSample>();
        foreach (var row in await modules.ListSessionsAsync(projectId, steamId, MaxSessionsScanned, ct))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var started = NullableNumber(row, "started_at_unix_ms");
            if (!started.HasValue) continue;
            samples.Add(new SessionSample(
                Text(row, "steam_id") ?? string.Empty,
                started.Value,
                NullableNumber(row, "ended_at_unix_ms"),
                NullableNumber(row, "last_heartbeat_at_unix_ms")));
        }
        return samples;
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? NullableNumber(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : null;
}
