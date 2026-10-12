using System.Text.Json;

namespace SboxNetworkStorage.Storage;

/// <summary>
/// Module-owned data tables (integrity flags, player blocks, player action log).
/// Implemented by the relational drivers (SQLite, PostgreSQL); an in-memory
/// fallback backs tests and non-relational configurations. Rows are returned as
/// detached <see cref="JsonElement"/> objects keyed by column name, mirroring
/// <see cref="INetworkStorageStore"/> row shapes. Every method validates inputs
/// before any statement runs.
/// </summary>
public interface IModuleDataStore
{
    // ── integrity_flags ─────────────────────────────────────────────
    Task InsertFlagAsync(string projectId, string flagId, long createdAtUnixMs, string steamId,
        string detector, string severity, string state, string? note, JsonElement? evidenceJson, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListFlagsAsync(string projectId, string? state, string? detector,
        string? severity, string? steamId, int limit, CancellationToken ct);
    Task<bool> UpdateFlagStateAsync(string projectId, string flagId, string state, string? note, CancellationToken ct);
    Task<long> CountOpenFlagsAsync(string projectId, CancellationToken ct);

    // ── player_blocks ───────────────────────────────────────────────
    Task UpsertBlockAsync(string projectId, string steamId, string scope, string? reason,
        long createdAtUnixMs, long? expiresAtUnixMs, string? createdBy, CancellationToken ct);
    Task<JsonElement?> ReadBlockAsync(string projectId, string steamId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListBlocksAsync(string projectId, CancellationToken ct);
    Task DeleteBlockAsync(string projectId, string steamId, CancellationToken ct);

    // ── player_sessions (read-only for insights; writes stay on the analytics path) ──
    /// <summary>Lists sessions newest first, optionally for one player, limited to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<JsonElement>> ListSessionsAsync(string projectId, string? steamId, int limit, CancellationToken ct);

    // ── player_action_log ───────────────────────────────────────────
    Task InsertActionLogAsync(string projectId, string steamId, long createdAtUnixMs, string entryId,
        string? endpointSlug, long? revisionId, string? outcome, JsonElement? changedFieldsJson,
        JsonElement? bodyJson, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListActionLogAsync(string projectId, string? steamId, string? endpointSlug,
        string? outcome, long? fromUnixMs, long? toUnixMs, int limit, CancellationToken ct);
    Task<long> PurgeActionLogBeforeAsync(long beforeUnixMs, CancellationToken ct);
    /// <summary>Deletes one project's action log rows older than <paramref name="beforeUnixMs"/>. Returns rows deleted.</summary>
    Task<long> PurgeProjectActionLogAsync(string projectId, long beforeUnixMs, CancellationToken ct);
}
