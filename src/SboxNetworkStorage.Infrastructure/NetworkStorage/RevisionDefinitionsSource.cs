using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Definitions-source abstraction (8.2): the same read surface served live,
/// through the staged overlay, or from a retained snapshot. Player records
/// are never versioned — every source reads and writes the same live player
/// data; only definitions and values pin.
/// </summary>
public interface IDefinitionsSource
{
    Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct);
    Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct);
    Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct);
    Task<JsonElement?> ReadEndpointRowAsync(string projectId, string slugOrId, CancellationToken ct);
    Task<JsonElement?> ReadQueryRowAsync(string projectId, string queryId, CancellationToken ct);
    Task<JsonElement?> ReadWorkflowRowAsync(string projectId, string workflowId, CancellationToken ct);
    Task<JsonElement?> ReadCollectionRowAsync(string projectId, string collectionId, CancellationToken ct);
}

/// <summary>Live definitions from the store (today's behavior, unchanged).</summary>
public sealed class LiveDefinitionsSource(INetworkStorageStore store) : IDefinitionsSource
{
    public Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct)
        => store.ListEndpointsAsync(projectId, ct);
    public Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct)
        => store.ListWorkflowsAsync(projectId, ct);
    public Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct)
        => store.ListCollectionsAsync(projectId, ct);
    public Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct)
        => store.ListQueriesAsync(projectId, ct);
    public Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct)
        => store.ReadGameValuesAsync(projectId, ct);
    public async Task<JsonElement?> ReadEndpointRowAsync(string projectId, string slugOrId, CancellationToken ct)
    {
        var direct = await store.ReadEndpointAsync(projectId, slugOrId, ct);
        if (direct.HasValue) return direct;
        foreach (var row in await store.ListEndpointsAsync(projectId, ct))
            if (RowString(row, "slug") == slugOrId || RowString(row, "endpoint_id") == slugOrId)
                return row;
        return null;
    }
    public Task<JsonElement?> ReadQueryRowAsync(string projectId, string queryId, CancellationToken ct)
        => store.ReadQueryAsync(projectId, queryId, ct);
    public Task<JsonElement?> ReadWorkflowRowAsync(string projectId, string workflowId, CancellationToken ct)
        => store.ReadWorkflowAsync(projectId, workflowId, ct);
    public Task<JsonElement?> ReadCollectionRowAsync(string projectId, string collectionId, CancellationToken ct)
        => store.ReadCollectionAsync(projectId, collectionId, ct);

    private static string? RowString(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;
}

/// <summary>
/// Snapshot definitions from <c>revisions/{id}.json</c>. Rows keep the live
/// store-row shape captured at publish, so downstream readers need no
/// second code path. Anything absent falls back to live (never a miss
/// caused by the snapshot alone).
/// </summary>
public sealed class SnapshotDefinitionsSource(RevisionSnapshots.SnapshotDocument snapshot, IDefinitionsSource live) : IDefinitionsSource
{
    public RevisionSnapshots.SnapshotDocument Snapshot { get; } = snapshot;

    public Task<IReadOnlyList<JsonElement>> ListEndpointsAsync(string projectId, CancellationToken ct)
        => Task.FromResult(Snapshot.Endpoints);
    public Task<IReadOnlyList<JsonElement>> ListWorkflowsAsync(string projectId, CancellationToken ct)
        => Task.FromResult(Snapshot.Workflows);
    public Task<IReadOnlyList<JsonElement>> ListCollectionsAsync(string projectId, CancellationToken ct)
        => Task.FromResult(Snapshot.Collections);
    public Task<IReadOnlyList<JsonElement>> ListQueriesAsync(string projectId, CancellationToken ct)
        => Task.FromResult(Snapshot.Queries);
    public Task<JsonElement?> ReadGameValuesAsync(string projectId, CancellationToken ct)
        => Task.FromResult(Snapshot.GameValues);
    public async Task<JsonElement?> ReadEndpointRowAsync(string projectId, string slugOrId, CancellationToken ct)
    {
        foreach (var row in Snapshot.Endpoints)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if ((row.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == slugOrId)
                || (row.TryGetProperty("endpoint_id", out var e) && e.ValueKind == JsonValueKind.String && e.GetString() == slugOrId))
                return row;
        }
        return await live.ReadEndpointRowAsync(projectId, slugOrId, ct);
    }
    public async Task<JsonElement?> ReadQueryRowAsync(string projectId, string queryId, CancellationToken ct)
    {
        foreach (var row in Snapshot.Queries)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if ((row.TryGetProperty("query_id", out var q) && q.ValueKind == JsonValueKind.String && q.GetString() == queryId)
                || (row.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == queryId)
                || (row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == queryId))
                return row;
        }
        return await live.ReadQueryRowAsync(projectId, queryId, ct);
    }
    public async Task<JsonElement?> ReadWorkflowRowAsync(string projectId, string workflowId, CancellationToken ct)
    {
        foreach (var row in Snapshot.Workflows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if ((row.TryGetProperty("workflow_id", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() == workflowId)
                || (row.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == workflowId)
                || (row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == workflowId))
                return row;
        }
        return await live.ReadWorkflowRowAsync(projectId, workflowId, ct);
    }
    public async Task<JsonElement?> ReadCollectionRowAsync(string projectId, string collectionId, CancellationToken ct)
    {
        foreach (var row in Snapshot.Collections)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if ((row.TryGetProperty("collection_id", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == collectionId)
                || (row.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == collectionId)
                || (row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == collectionId))
                return row;
        }
        return await live.ReadCollectionRowAsync(projectId, collectionId, ct);
    }
}

/// <summary>
/// Snapshot cache keyed by (project, revision): snapshots are immutable so a
/// short cache is safe. Misses and failures return null (callers fall back
/// to live); nothing here ever fails a game request.
/// </summary>
public sealed class RevisionSnapshotCache(IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public async Task<RevisionSnapshots.SnapshotDocument?> GetAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId, long revisionId, CancellationToken ct)
    {
        var key = $"revision-snapshot:{projectId}:{revisionId}";
        if (cache.TryGetValue(key, out RevisionSnapshots.SnapshotDocument? hit) && hit is not null)
            return hit;
        var snapshot = await RevisionSnapshots.ReadSnapshotAsync(workspace, ownerUserId, projectId, revisionId, ct);
        if (snapshot is not null)
            cache.Set(key, snapshot, Lifetime);
        return snapshot;
    }

    public void Invalidate(string projectId, long revisionId)
        => cache.Remove($"revision-snapshot:{projectId}:{revisionId}");
}

/// <summary>
/// Resolves the effective definitions source for a data-plane request.
/// Staged overlay (publishTarget=next) wins when present; otherwise a
/// retained revision inside its support window pins the snapshot; anything
/// without a revision, or with an unknown/expired one, uses live.
/// </summary>
public static class RevisionDefinitionsResolver
{
    public sealed record Resolved(IDefinitionsSource Source, RevisionSnapshots.SnapshotDocument? Snapshot);

    public static async Task<Resolved> ResolveAsync(
        WorkspaceProject project,
        RevisionIndex index,
        long? clientRevision,
        INetworkStorageStore store,
        IWorkspaceStore workspace,
        RevisionSnapshotCache snapshots,
        long ownerUserId,
        string projectId,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var live = new LiveDefinitionsSource(store);
        if (!VersionPolicyEvaluator.HasExplicitPolicy(project) || !clientRevision.HasValue)
            return new Resolved(live, null);
        var liveId = index.LiveRevisionId;
        if (!liveId.HasValue || clientRevision.Value >= liveId.Value)
            return new Resolved(live, null);

        var snapshot = await snapshots.GetAsync(workspace, ownerUserId, projectId, clientRevision.Value, ct);
        if (snapshot is null)
            return new Resolved(live, null);

        // Pin only inside the support window (live or in grace). Expired and
        // below-minimum revisions fall back to live definitions; enforcement
        // (when switched on) rejects them separately.
        var decision = VersionPolicyEvaluator.Evaluate(project, index, clientRevision, nowUtc, liveId);
        if (decision.IsOutdated && (decision.GraceExpired || decision.Action is "block_writes" or "block_all"))
        {
            var inGrace = !decision.GraceExpired;
            if (!inGrace)
                return new Resolved(live, null);
        }
        return new Resolved(new SnapshotDefinitionsSource(snapshot, live), snapshot);
    }
}
