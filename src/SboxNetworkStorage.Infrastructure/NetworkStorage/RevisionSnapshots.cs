using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// One retained revision entry in <c>revisions/index.json</c>.
/// Grace is anchored at the actual revision change (<see cref="GraceStartUtc"/>),
/// never at first-ever publish.
/// </summary>
public sealed record RevisionIndexEntry(
    long RevisionId,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset GraceStartUtc,
    bool GraceEnded = false,
    DateTimeOffset? GraceExtendedUntilUtc = null);

/// <summary>
/// The retained-revision index (<c>revisions/index.json</c>). Missing entries
/// (pre-snapshot revisions) fall back to live behavior, never to a block.
/// </summary>
public sealed record RevisionIndex(
    IReadOnlyList<RevisionIndexEntry> Revisions,
    long? LiveRevisionId = null,
    DateTimeOffset? LivePublishedAtUtc = null)
{
    public static RevisionIndex Empty => new([], null, null);

    public RevisionIndexEntry? Find(long revisionId)
        => Revisions.FirstOrDefault(e => e.RevisionId == revisionId);
}

/// <summary>
/// Immutable retained-revision snapshots (D8). On a revision-changing package
/// sync, the full merged definition sets (endpoints, workflows, collections,
/// queries, game values) plus the publish time are written once to
/// <c>revisions/{revisionId}.json</c>; <c>revisions/index.json</c> tracks the
/// retained ids, grace starts and owner overrides. Snapshots are write-once:
/// later live edits never rewrite them (byte-stable). Module-owned
/// definitions ride the live lists verbatim — no ownership filtering here.
/// </summary>
public static class RevisionSnapshots
{
    public const string IndexPath = "revisions/index.json";
    public static string SnapshotPath(long revisionId) => $"revisions/{revisionId}.json";

    /// <summary>Snapshot document shape stored at <c>revisions/{id}.json</c>.</summary>
    public sealed record SnapshotDocument(
        long RevisionId,
        DateTimeOffset PublishedAtUtc,
        IReadOnlyList<JsonElement> Endpoints,
        IReadOnlyList<JsonElement> Workflows,
        IReadOnlyList<JsonElement> Collections,
        IReadOnlyList<JsonElement> Queries,
        JsonElement? GameValues);

    public static async Task<RevisionIndex> ReadIndexAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId, CancellationToken ct)
    {
        try
        {
            var raw = await workspace.GetProjectResourceAsync<JsonElement>(ownerUserId, projectId, IndexPath, ct);
            return ParseIndex(raw);
        }
        catch (OperationCanceledException) { throw; }
        catch { return RevisionIndex.Empty; }
    }

    public static RevisionIndex ParseIndex(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } root) return RevisionIndex.Empty;
        long? live = root.TryGetProperty("liveRevisionId", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt64(out var lv) ? lv : null;
        DateTimeOffset? liveAt = ReadTime(root, "livePublishedAt");
        var entries = new List<RevisionIndexEntry>();
        if (root.TryGetProperty("revisions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt64(out var id)) continue;
                var published = ReadTime(item, "publishedAt") ?? ReadTime(item, "publishedAtUtc") ?? DateTimeOffset.UtcNow;
                var graceStart = ReadTime(item, "graceStart") ?? ReadTime(item, "graceStartUtc") ?? published;
                var ended = item.TryGetProperty("graceEnded", out var e) && e.ValueKind == JsonValueKind.True;
                DateTimeOffset? extended = ReadTime(item, "graceExtendedUntil") ?? ReadTime(item, "graceExtendedUntilUtc");
                entries.Add(new RevisionIndexEntry(id, published, graceStart, ended, extended));
            }
        }
        return new RevisionIndex(entries, live, liveAt);
    }

    /// <summary>
    /// Records a publish in the index (write-once per revision id) and prunes
    /// beyond retention. Revisions still inside a support window are preserved
    /// even past the retention count; only uncovered revisions are pruned.
    /// Returns the pruned revision ids.
    /// </summary>
    public static async Task<IReadOnlyList<long>> RecordPublishAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId,
        long revisionId, DateTimeOffset publishedAtUtc, int retention,
        Func<long, bool> stillSupported,
        CancellationToken ct)
    {
        var index = await ReadIndexAsync(workspace, ownerUserId, projectId, ct);
        var entries = index.Revisions.ToList();
        if (!entries.Any(e => e.RevisionId == revisionId))
            entries.Add(new RevisionIndexEntry(revisionId, publishedAtUtc, publishedAtUtc));
        entries.Sort((a, b) => a.RevisionId.CompareTo(b.RevisionId));

        var keep = new HashSet<long> { revisionId };
        var earlier = entries.Where(e => e.RevisionId != revisionId).OrderByDescending(e => e.RevisionId).ToList();
        foreach (var e in earlier.Take(Math.Max(0, retention)))
            keep.Add(e.RevisionId);
        // Preserve supported grace windows past the retention count.
        foreach (var e in earlier)
            if (stillSupported(e.RevisionId))
                keep.Add(e.RevisionId);

        var pruned = entries.Where(e => !keep.Contains(e.RevisionId)).Select(e => e.RevisionId).ToList();
        var kept = entries.Where(e => keep.Contains(e.RevisionId)).ToList();
        await WriteIndexAsync(workspace, ownerUserId, projectId,
            new RevisionIndex(kept, revisionId, publishedAtUtc), ct);
        return pruned;
    }

    public static async Task<bool> EndGraceAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId, long revisionId, CancellationToken ct)
    {
        var index = await ReadIndexAsync(workspace, ownerUserId, projectId, ct);
        if (index.Find(revisionId) is null) return false;
        var entries = index.Revisions.Select(e => e.RevisionId == revisionId ? e with { GraceEnded = true } : e).ToList();
        await WriteIndexAsync(workspace, ownerUserId, projectId, index with { Revisions = entries }, ct);
        return true;
    }

    public static async Task<bool> ExtendGraceAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId, long revisionId,
        DateTimeOffset untilUtc, CancellationToken ct)
    {
        var index = await ReadIndexAsync(workspace, ownerUserId, projectId, ct);
        if (index.Find(revisionId) is null) return false;
        var entries = index.Revisions.Select(e => e.RevisionId == revisionId
            ? e with { GraceEnded = false, GraceExtendedUntilUtc = untilUtc } : e).ToList();
        await WriteIndexAsync(workspace, ownerUserId, projectId, index with { Revisions = entries }, ct);
        return true;
    }

    /// <summary>
    /// Writes the snapshot once. When one already exists for this revision it
    /// is left untouched (immutable, byte-stable after later live edits).
    /// </summary>
    public static async Task<bool> TryWriteSnapshotAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId,
        SnapshotDocument snapshot, CancellationToken ct)
    {
        try
        {
            var existing = await workspace.GetProjectResourceAsync<JsonElement>(
                ownerUserId, projectId, SnapshotPath(snapshot.RevisionId), ct);
            if (existing.ValueKind == JsonValueKind.Object && existing.TryGetProperty("revisionId", out _))
                return false;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* absent → write */ }

        await workspace.PutProjectResourceAsync(ownerUserId, projectId,
            SnapshotPath(snapshot.RevisionId), ToJson(snapshot), ct);
        return true;
    }

    public static async Task<SnapshotDocument?> ReadSnapshotAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId, long revisionId, CancellationToken ct)
    {
        try
        {
            var raw = await workspace.GetProjectResourceAsync<JsonElement>(
                ownerUserId, projectId, SnapshotPath(revisionId), ct);
            return ParseSnapshot(raw);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static SnapshotDocument? ParseSnapshot(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } root) return null;
        if (!root.TryGetProperty("revisionId", out var idProp) || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt64(out var id))
            return null;
        var published = ReadTime(root, "publishedAt") ?? DateTimeOffset.UtcNow;
        static IReadOnlyList<JsonElement> Arr(JsonElement o, string name)
            => o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(e => e.Clone()).ToList() : [];
        JsonElement? gv = root.TryGetProperty("gameValues", out var g) && g.ValueKind != JsonValueKind.Null ? g.Clone() : null;
        return new SnapshotDocument(id, published,
            Arr(root, "endpoints"), Arr(root, "workflows"), Arr(root, "collections"), Arr(root, "queries"), gv);
    }

    private static Dictionary<string, object?> ToJson(SnapshotDocument snapshot) => new(StringComparer.Ordinal)
    {
        ["revisionId"] = snapshot.RevisionId,
        ["publishedAt"] = snapshot.PublishedAtUtc.ToString("o"),
        ["endpoints"] = snapshot.Endpoints.Select(e => (object?)JsonNode.Parse(e.GetRawText())).ToList(),
        ["workflows"] = snapshot.Workflows.Select(e => (object?)JsonNode.Parse(e.GetRawText())).ToList(),
        ["collections"] = snapshot.Collections.Select(e => (object?)JsonNode.Parse(e.GetRawText())).ToList(),
        ["queries"] = snapshot.Queries.Select(e => (object?)JsonNode.Parse(e.GetRawText())).ToList(),
        ["gameValues"] = snapshot.GameValues.HasValue ? JsonNode.Parse(snapshot.GameValues.Value.GetRawText()) : null,
    };

    private static async Task WriteIndexAsync(
        IWorkspaceStore workspace, long ownerUserId, string projectId, RevisionIndex index, CancellationToken ct)
    {
        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["liveRevisionId"] = index.LiveRevisionId,
            ["livePublishedAt"] = index.LivePublishedAtUtc?.ToString("o"),
            ["revisions"] = index.Revisions.OrderBy(e => e.RevisionId).Select(e => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = e.RevisionId,
                ["publishedAt"] = e.PublishedAtUtc.ToString("o"),
                ["graceStart"] = e.GraceStartUtc.ToString("o"),
                ["graceEnded"] = e.GraceEnded,
                ["graceExtendedUntil"] = e.GraceExtendedUntilUtc?.ToString("o"),
            }).ToList(),
        };
        await workspace.PutProjectResourceAsync(ownerUserId, projectId, IndexPath, doc, ct);
    }

    private static DateTimeOffset? ReadTime(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var unix))
            return unix > 1_000_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
        if (p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var dto))
            return dto.ToUniversalTime();
        return null;
    }
}
