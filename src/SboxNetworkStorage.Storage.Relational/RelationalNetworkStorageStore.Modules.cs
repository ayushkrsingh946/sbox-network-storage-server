using System.Text.Json;
using SboxNetworkStorage.Storage;
using K = SboxNetworkStorage.Storage.Relational.ColumnKind;

namespace SboxNetworkStorage.Storage.Relational;

// Module-owned data tables (v6 migration): integrity flags, player blocks and
// the player action log. Same conventions as the telemetry tables: named
// @parameters, INSERT … ON CONFLICT upserts, newest-first SELECTs with
// LIMIT @limit, and JsonElement rows shaped by Column arrays.
// (Column kinds are qualified as K.* because the base class's Text()/Int64()
// parameter helpers would otherwise win over the enum members.)
public abstract partial class RelationalNetworkStorageStore : IModuleDataStore
{
    private static readonly Column[] SessionColumns =
    [
        new("project_id", K.Text), new("steam_id", K.Text), new("session_id", K.Text),
        new("started_at_unix_ms", K.LongOrNull), new("last_heartbeat_at_unix_ms", K.LongOrNull),
        new("ended_at_unix_ms", K.LongOrNull), new("last_metrics_json", K.Json), new("summary_json", K.Json),
    ];

    private static readonly Column[] FlagColumns =
    [
        new("project_id", K.Text), new("flag_id", K.Text), new("created_at_unix_ms", K.Long),
        new("steam_id", K.Text), new("detector", K.Text), new("severity", K.Text),
        new("state", K.Text), new("note", K.Text), new("evidence_json", K.Json),
    ];

    private static readonly Column[] BlockColumns =
    [
        new("project_id", K.Text), new("steam_id", K.Text), new("scope", K.Text),
        new("reason", K.Text), new("created_at_unix_ms", K.Long),
        new("expires_at_unix_ms", K.LongOrNull), new("created_by", K.Text),
    ];

    private static readonly Column[] ActionLogColumns =
    [
        new("project_id", K.Text), new("steam_id", K.Text), new("created_at_unix_ms", K.Long),
        new("entry_id", K.Text), new("endpoint_slug", K.Text), new("revision_id", K.LongOrNull),
        new("outcome", K.Text), new("changed_fields_json", K.Json), new("body_json", K.Json),
    ];

    private string T(string table) => $"{TablePrefix}{table}";

    public Task InsertFlagAsync(string projectId, string flagId, long createdAtUnixMs, string steamId,
        string detector, string severity, string state, string? note, JsonElement? evidenceJson, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Id(flagId); StoreValidation.Id(steamId);
        StoreValidation.Id(detector); StoreValidation.Id(severity); StoreValidation.Id(state);
        var evidence = evidenceJson.HasValue ? StoreValidation.Serialize(evidenceJson.Value, "integrity_flags", MaxPayloadBytes) : null;
        return ExecuteAsync(
            $"INSERT INTO {T("integrity_flags")} (project_id, flag_id, created_at_unix_ms, steam_id, detector, severity, state, note, evidence_json) " +
            "VALUES (@project_id, @flag_id, @created_at_unix_ms, @steam_id, @detector, @severity, @state, @note, @evidence_json) " +
            "ON CONFLICT (project_id, flag_id) DO UPDATE SET state = @state, note = @note, evidence_json = @evidence_json",
            ct, Text("project_id", projectId), Text("flag_id", flagId), Int64("created_at_unix_ms", createdAtUnixMs),
            Text("steam_id", steamId), Text("detector", detector), Text("severity", severity),
            Text("state", state), Text("note", note), Text("evidence_json", evidence));
    }

    public Task<IReadOnlyList<JsonElement>> ListFlagsAsync(string projectId, string? state, string? detector,
        string? severity, string? steamId, int limit, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Limit(limit);
        var where = "project_id = @project_id";
        var args = new List<Arg> { Text("project_id", projectId) };
        if (state is not null) { StoreValidation.Id(state); where += " AND state = @state"; args.Add(Text("state", state)); }
        if (detector is not null) { StoreValidation.Id(detector); where += " AND detector = @detector"; args.Add(Text("detector", detector)); }
        if (severity is not null) { StoreValidation.Id(severity); where += " AND severity = @severity"; args.Add(Text("severity", severity)); }
        if (steamId is not null) { StoreValidation.Id(steamId); where += " AND steam_id = @steam_id"; args.Add(Text("steam_id", steamId)); }
        args.Add(Int32("limit", limit));
        return QueryListAsync(
            $"SELECT project_id, flag_id, created_at_unix_ms, steam_id, detector, severity, state, note, evidence_json FROM {T("integrity_flags")} " +
            $"WHERE {where} ORDER BY created_at_unix_ms DESC, flag_id LIMIT @limit",
            FlagColumns, ct, [.. args]);
    }

    public async Task<bool> UpdateFlagStateAsync(string projectId, string flagId, string state, string? note, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Id(flagId); StoreValidation.Id(state);
        return await ExecuteCountAsync(
            $"UPDATE {T("integrity_flags")} SET state = @state, note = @note WHERE project_id = @project_id AND flag_id = @flag_id",
            ct, Text("state", state), Text("note", note), Text("project_id", projectId), Text("flag_id", flagId)) == 1;
    }

    public Task<long> CountOpenFlagsAsync(string projectId, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        return QueryInt64ScalarAsync(
            $"SELECT COUNT(*) FROM {T("integrity_flags")} WHERE project_id = @project_id AND state = @state",
            ct, Text("project_id", projectId), Text("state", "open"));
    }

    public Task UpsertBlockAsync(string projectId, string steamId, string scope, string? reason,
        long createdAtUnixMs, long? expiresAtUnixMs, string? createdBy, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Id(steamId); StoreValidation.Id(scope);
        return ExecuteAsync(
            $"INSERT INTO {T("player_blocks")} (project_id, steam_id, scope, reason, created_at_unix_ms, expires_at_unix_ms, created_by) " +
            "VALUES (@project_id, @steam_id, @scope, @reason, @created_at_unix_ms, @expires_at_unix_ms, @created_by) " +
            "ON CONFLICT (project_id, steam_id) DO UPDATE SET scope = @scope, reason = @reason, created_at_unix_ms = @created_at_unix_ms, expires_at_unix_ms = @expires_at_unix_ms, created_by = @created_by",
            ct, Text("project_id", projectId), Text("steam_id", steamId), Text("scope", scope),
            Text("reason", reason), Int64("created_at_unix_ms", createdAtUnixMs),
            Int64("expires_at_unix_ms", expiresAtUnixMs), Text("created_by", createdBy));
    }

    public Task<JsonElement?> ReadBlockAsync(string projectId, string steamId, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Id(steamId);
        return QuerySingleAsync(
            $"SELECT project_id, steam_id, scope, reason, created_at_unix_ms, expires_at_unix_ms, created_by FROM {T("player_blocks")} " +
            "WHERE project_id = @project_id AND steam_id = @steam_id",
            BlockColumns, ct, Text("project_id", projectId), Text("steam_id", steamId));
    }

    public Task<IReadOnlyList<JsonElement>> ListBlocksAsync(string projectId, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        return QueryListAsync(
            $"SELECT project_id, steam_id, scope, reason, created_at_unix_ms, expires_at_unix_ms, created_by FROM {T("player_blocks")} " +
            "WHERE project_id = @project_id ORDER BY steam_id",
            BlockColumns, ct, Text("project_id", projectId));
    }

    public Task DeleteBlockAsync(string projectId, string steamId, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Id(steamId);
        return ExecuteAsync($"DELETE FROM {T("player_blocks")} WHERE project_id = @project_id AND steam_id = @steam_id",
            ct, Text("project_id", projectId), Text("steam_id", steamId));
    }

    public Task InsertActionLogAsync(string projectId, string steamId, long createdAtUnixMs, string entryId,
        string? endpointSlug, long? revisionId, string? outcome, JsonElement? changedFieldsJson,
        JsonElement? bodyJson, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Id(steamId); StoreValidation.Id(entryId);
        var changed = changedFieldsJson.HasValue ? StoreValidation.Serialize(changedFieldsJson.Value, "player_action_log", MaxPayloadBytes) : null;
        var body = bodyJson.HasValue ? StoreValidation.Serialize(bodyJson.Value, "player_action_log", MaxPayloadBytes) : null;
        return ExecuteAsync(
            $"INSERT INTO {T("player_action_log")} (project_id, steam_id, created_at_unix_ms, entry_id, endpoint_slug, revision_id, outcome, changed_fields_json, body_json) " +
            "VALUES (@project_id, @steam_id, @created_at_unix_ms, @entry_id, @endpoint_slug, @revision_id, @outcome, @changed_fields_json, @body_json) " +
            "ON CONFLICT (project_id, steam_id, created_at_unix_ms, entry_id) DO NOTHING",
            ct, Text("project_id", projectId), Text("steam_id", steamId), Int64("created_at_unix_ms", createdAtUnixMs),
            Text("entry_id", entryId), Text("endpoint_slug", endpointSlug), Int64("revision_id", revisionId),
            Text("outcome", outcome), Text("changed_fields_json", changed), Text("body_json", body));
    }

    public Task<IReadOnlyList<JsonElement>> ListActionLogAsync(string projectId, string? steamId, string? endpointSlug,
        string? outcome, long? fromUnixMs, long? toUnixMs, int limit, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Limit(limit);
        var where = "project_id = @project_id";
        var args = new List<Arg> { Text("project_id", projectId) };
        if (steamId is not null) { StoreValidation.Id(steamId); where += " AND steam_id = @steam_id"; args.Add(Text("steam_id", steamId)); }
        if (endpointSlug is not null) { where += " AND endpoint_slug = @endpoint_slug"; args.Add(Text("endpoint_slug", endpointSlug)); }
        if (outcome is not null) { where += " AND outcome = @outcome"; args.Add(Text("outcome", outcome)); }
        if (fromUnixMs is not null) { where += " AND created_at_unix_ms >= @from_unix_ms"; args.Add(Int64("from_unix_ms", fromUnixMs)); }
        if (toUnixMs is not null) { where += " AND created_at_unix_ms <= @to_unix_ms"; args.Add(Int64("to_unix_ms", toUnixMs)); }
        args.Add(Int32("limit", limit));
        return QueryListAsync(
            $"SELECT project_id, steam_id, created_at_unix_ms, entry_id, endpoint_slug, revision_id, outcome, changed_fields_json, body_json FROM {T("player_action_log")} " +
            $"WHERE {where} ORDER BY created_at_unix_ms DESC, entry_id LIMIT @limit",
            ActionLogColumns, ct, [.. args]);
    }

    public async Task<long> PurgeActionLogBeforeAsync(long beforeUnixMs, CancellationToken ct)
        => await ExecuteCountAsync($"DELETE FROM {T("player_action_log")} WHERE created_at_unix_ms < @before_unix_ms",
            ct, Int64("before_unix_ms", beforeUnixMs));

    public async Task<long> PurgeProjectActionLogAsync(string projectId, long beforeUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        return await ExecuteCountAsync(
            $"DELETE FROM {T("player_action_log")} WHERE project_id = @project_id AND created_at_unix_ms < @before_unix_ms",
            ct, Text("project_id", projectId), Int64("before_unix_ms", beforeUnixMs));
    }

    public Task<IReadOnlyList<JsonElement>> ListSessionsAsync(string projectId, string? steamId, int limit, CancellationToken ct)
    {
        StoreValidation.Id(projectId); StoreValidation.Limit(limit);
        if (steamId is null)
            return QueryListAsync(
                $"SELECT project_id, steam_id, session_id, started_at_unix_ms, last_heartbeat_at_unix_ms, ended_at_unix_ms, last_metrics_json, summary_json FROM {T("player_sessions")} " +
                "WHERE project_id = @project_id ORDER BY started_at_unix_ms DESC, session_id DESC LIMIT @limit",
                SessionColumns, ct, Text("project_id", projectId), Int32("limit", limit));
        StoreValidation.Id(steamId);
        return QueryListAsync(
            $"SELECT project_id, steam_id, session_id, started_at_unix_ms, last_heartbeat_at_unix_ms, ended_at_unix_ms, last_metrics_json, summary_json FROM {T("player_sessions")} " +
            "WHERE project_id = @project_id AND steam_id = @steam_id ORDER BY started_at_unix_ms DESC, session_id DESC LIMIT @limit",
            SessionColumns, ct, Text("project_id", projectId), Text("steam_id", steamId), Int32("limit", limit));
    }
}
