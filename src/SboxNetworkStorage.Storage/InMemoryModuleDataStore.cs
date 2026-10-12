using System.Text.Json;

namespace SboxNetworkStorage.Storage;

/// <summary>
/// In-memory <see cref="IModuleDataStore"/> for tests and configurations without
/// a relational driver. Same validation and row shapes as the relational
/// implementation; unbounded (tests own their data volume).
/// </summary>
public sealed class InMemoryModuleDataStore : IModuleDataStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _flags = new(StringComparer.Ordinal);
    private readonly List<JsonElement> _sessions = [];
    private readonly Dictionary<string, JsonElement> _blocks = new(StringComparer.Ordinal);
    private readonly List<JsonElement> _actions = [];

    private static void Id(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }

    private static JsonElement Row(params (string Key, object? Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in fields)
            {
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    public Task InsertFlagAsync(string projectId, string flagId, long createdAtUnixMs, string steamId,
        string detector, string severity, string state, string? note, JsonElement? evidenceJson, CancellationToken ct)
    {
        Id(projectId, nameof(projectId)); Id(flagId, nameof(flagId)); Id(steamId, nameof(steamId));
        Id(detector, nameof(detector)); Id(severity, nameof(severity)); Id(state, nameof(state));
        lock (_gate) _flags[$"{projectId}\0{flagId}"] = Row(
            ("project_id", (object?)projectId), ("flag_id", flagId), ("created_at_unix_ms", createdAtUnixMs),
            ("steam_id", steamId), ("detector", detector), ("severity", severity),
            ("state", state), ("note", note), ("evidence_json", evidenceJson?.Clone()));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JsonElement>> ListFlagsAsync(string projectId, string? state, string? detector,
        string? severity, string? steamId, int limit, CancellationToken ct)
    {
        Id(projectId, nameof(projectId));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            var rows = _flags.Values
                .Where(row => Text(row, "project_id") == projectId
                    && (state is null || Text(row, "state") == state)
                    && (detector is null || Text(row, "detector") == detector)
                    && (severity is null || Text(row, "severity") == severity)
                    && (steamId is null || Text(row, "steam_id") == steamId))
                .OrderByDescending(row => Number(row, "created_at_unix_ms"))
                .ThenBy(row => Text(row, "flag_id"), StringComparer.Ordinal)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<JsonElement>>(rows);
        }
    }

    public Task<bool> UpdateFlagStateAsync(string projectId, string flagId, string state, string? note, CancellationToken ct)
    {
        Id(projectId, nameof(projectId)); Id(flagId, nameof(flagId)); Id(state, nameof(state));
        lock (_gate)
        {
            if (!_flags.TryGetValue($"{projectId}\0{flagId}", out var row)) return Task.FromResult(false);
            _flags[$"{projectId}\0{flagId}"] = Row(
                ("project_id", (object?)projectId), ("flag_id", Text(row, "flag_id")),
                ("created_at_unix_ms", Number(row, "created_at_unix_ms")),
                ("steam_id", Text(row, "steam_id")), ("detector", Text(row, "detector")),
                ("severity", Text(row, "severity")), ("state", state), ("note", note),
                ("evidence_json", row.TryGetProperty("evidence_json", out var evidence) ? evidence.Clone() : (JsonElement?)null));
            return Task.FromResult(true);
        }
    }

    public Task<long> CountOpenFlagsAsync(string projectId, CancellationToken ct)
    {
        Id(projectId, nameof(projectId));
        lock (_gate) return Task.FromResult((long)_flags.Values.Count(
            row => Text(row, "project_id") == projectId && Text(row, "state") == "open"));
    }

    public Task UpsertBlockAsync(string projectId, string steamId, string scope, string? reason,
        long createdAtUnixMs, long? expiresAtUnixMs, string? createdBy, CancellationToken ct)
    {
        Id(projectId, nameof(projectId)); Id(steamId, nameof(steamId)); Id(scope, nameof(scope));
        lock (_gate) _blocks[$"{projectId}\0{steamId}"] = Row(
            ("project_id", (object?)projectId), ("steam_id", steamId), ("scope", scope),
            ("reason", reason), ("created_at_unix_ms", createdAtUnixMs),
            ("expires_at_unix_ms", expiresAtUnixMs), ("created_by", createdBy));
        return Task.CompletedTask;
    }

    public Task<JsonElement?> ReadBlockAsync(string projectId, string steamId, CancellationToken ct)
    {
        Id(projectId, nameof(projectId)); Id(steamId, nameof(steamId));
        lock (_gate) return Task.FromResult(_blocks.TryGetValue($"{projectId}\0{steamId}", out var row) ? row : (JsonElement?)null);
    }

    public Task<IReadOnlyList<JsonElement>> ListBlocksAsync(string projectId, CancellationToken ct)
    {
        Id(projectId, nameof(projectId));
        lock (_gate)
        {
            var rows = _blocks.Values
                .Where(row => Text(row, "project_id") == projectId)
                .OrderBy(row => Text(row, "steam_id"), StringComparer.Ordinal)
                .ToList();
            return Task.FromResult<IReadOnlyList<JsonElement>>(rows);
        }
    }
    public Task<long> PurgeActionLogBeforeAsync(long beforeUnixMs, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult((long)_actions.RemoveAll(row => Number(row, "created_at_unix_ms") < beforeUnixMs));
    }

    public Task<long> PurgeProjectActionLogAsync(string projectId, long beforeUnixMs, CancellationToken ct)
    {
        Id(projectId, nameof(projectId));
        lock (_gate) return Task.FromResult((long)_actions.RemoveAll(row =>
            Text(row, "project_id") == projectId && Number(row, "created_at_unix_ms") < beforeUnixMs));
    }

    public Task DeleteBlockAsync(string projectId, string steamId, CancellationToken ct)
    {
        Id(projectId, nameof(projectId)); Id(steamId, nameof(steamId));
        lock (_gate) _blocks.Remove($"{projectId}\0{steamId}");
        return Task.CompletedTask;
    }

    public Task InsertActionLogAsync(string projectId, string steamId, long createdAtUnixMs, string entryId,
        string? endpointSlug, long? revisionId, string? outcome, JsonElement? changedFieldsJson,
        JsonElement? bodyJson, CancellationToken ct)
    {
        Id(projectId, nameof(projectId)); Id(steamId, nameof(steamId)); Id(entryId, nameof(entryId));
        lock (_gate) _actions.Add(Row(
            ("project_id", (object?)projectId), ("steam_id", steamId), ("created_at_unix_ms", createdAtUnixMs),
            ("entry_id", entryId), ("endpoint_slug", endpointSlug), ("revision_id", revisionId),
            ("outcome", outcome),
            ("changed_fields_json", changedFieldsJson?.Clone()), ("body_json", bodyJson?.Clone())));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JsonElement>> ListActionLogAsync(string projectId, string? steamId, string? endpointSlug,
        string? outcome, long? fromUnixMs, long? toUnixMs, int limit, CancellationToken ct)
    {
        Id(projectId, nameof(projectId));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            var rows = _actions
                .Where(row => Text(row, "project_id") == projectId
                    && (steamId is null || Text(row, "steam_id") == steamId)
                    && (endpointSlug is null || Text(row, "endpoint_slug") == endpointSlug)
                    && (outcome is null || Text(row, "outcome") == outcome)
                    && (fromUnixMs is null || Number(row, "created_at_unix_ms") >= fromUnixMs)
                    && (toUnixMs is null || Number(row, "created_at_unix_ms") <= toUnixMs))
                .OrderByDescending(row => Number(row, "created_at_unix_ms"))
                .ThenBy(row => Text(row, "entry_id"), StringComparer.Ordinal)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<JsonElement>>(rows);
        }
    }
    public Task<IReadOnlyList<JsonElement>> ListSessionsAsync(string projectId, string? steamId, int limit, CancellationToken ct)
    {
        Id(projectId, nameof(projectId));
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        // The in-memory driver has no analytics write path; sessions only exist on relational drivers.
        return Task.FromResult<IReadOnlyList<JsonElement>>([]);
    }
}
