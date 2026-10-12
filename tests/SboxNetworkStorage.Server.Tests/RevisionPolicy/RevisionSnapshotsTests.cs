using System.Text.Json;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Phase 4 (game-version-policy, 8.1): snapshots are written once per
/// revision-changing publish, retained per project (default 3, 0-20),
/// byte-stable after later live edits, and pruned only outside support
/// windows.
/// </summary>
public sealed class RevisionSnapshotsTests
{
    private sealed class MemoryWorkspace : IWorkspaceStore
    {
        public readonly Dictionary<string, JsonElement> Resources = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<WorkspaceProject>> GetUserProjectsAsync(long userId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkspaceProject>>([]);
        public Task<WorkspaceProjectUsage?> GetProjectUsageAsync(long userId, string projectId, string monthKey, CancellationToken cancellationToken)
            => Task.FromResult<WorkspaceProjectUsage?>(null);
        public Task SaveUserProjectsAsync(long userId, IReadOnlyList<WorkspaceProject> projects, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<T?> GetProjectResourceAsync<T>(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
        {
            if (Resources.TryGetValue(resourcePath, out var element))
                return Task.FromResult(JsonSerializer.Deserialize<T>(element.GetRawText()));
            return Task.FromResult<T?>(default);
        }
        public Task<string?> GetProjectResourceTextAsync(long userId, string projectId, string resourcePath, CancellationToken cancellationToken)
            => Task.FromResult(Resources.TryGetValue(resourcePath, out var element) ? element.GetRawText() : null);
        public Task PutProjectResourceAsync<T>(long userId, string projectId, string resourcePath, T data, CancellationToken cancellationToken)
        {
            Resources[resourcePath] = JsonSerializer.SerializeToElement(data);
            return Task.CompletedTask;
        }
        public Task<T?> GetRawAsync<T>(string absolutePath, CancellationToken cancellationToken) => Task.FromResult<T?>(default);
        public Task PutRawAsync<T>(string absolutePath, T data, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteRawAsync(string absolutePath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static RevisionSnapshots.SnapshotDocument Snapshot(long id, string marker) => new(
        id, DateTimeOffset.UtcNow,
        [JsonDocument.Parse($"{{\"slug\":\"ep\",\"marker\":\"{marker}\"}}").RootElement.Clone()],
        [], [], [],
        JsonDocument.Parse("{\"payload_json\":{\"items\":[]}}").RootElement.Clone());

    [Fact]
    public async Task ThreePublishes_KeepThreeSnapshots()
    {
        var workspace = new MemoryWorkspace();
        foreach (var id in new[] { 40L, 41L, 42L })
        {
            Assert.True(await RevisionSnapshots.TryWriteSnapshotAsync(workspace, 1, "proj", Snapshot(id, $"v{id}"), CancellationToken.None));
            await RevisionSnapshots.RecordPublishAsync(workspace, 1, "proj", id, DateTimeOffset.UtcNow, 3, _ => false, CancellationToken.None);
        }
        var index = await RevisionSnapshots.ReadIndexAsync(workspace, 1, "proj", CancellationToken.None);
        Assert.Equal([40L, 41L, 42L], index.Revisions.Select(e => e.RevisionId).OrderBy(id => id).ToArray());
        Assert.Equal(42, index.LiveRevisionId);
        foreach (var id in new[] { 40L, 41L, 42L })
            Assert.NotNull(await RevisionSnapshots.ReadSnapshotAsync(workspace, 1, "proj", id, CancellationToken.None));
    }

    [Fact]
    public async Task Retention1_PrunesOldestOutsideSupportWindow()
    {
        var workspace = new MemoryWorkspace();
        foreach (var id in new[] { 41L, 42L, 43L })
            await RevisionSnapshots.RecordPublishAsync(workspace, 1, "proj", id, DateTimeOffset.UtcNow, 1, _ => false, CancellationToken.None);
        var index = await RevisionSnapshots.ReadIndexAsync(workspace, 1, "proj", CancellationToken.None);
        Assert.DoesNotContain(41L, index.Revisions.Select(e => e.RevisionId));
        Assert.Contains(42L, index.Revisions.Select(e => e.RevisionId));
        Assert.Contains(43L, index.Revisions.Select(e => e.RevisionId));
    }

    [Fact]
    public async Task SupportedGraceWindow_SurvivesPruning()
    {
        var workspace = new MemoryWorkspace();
        foreach (var id in new[] { 41L, 42L, 43L })
            await RevisionSnapshots.RecordPublishAsync(workspace, 1, "proj", id, DateTimeOffset.UtcNow, 1, _ => true, CancellationToken.None);
        var index = await RevisionSnapshots.ReadIndexAsync(workspace, 1, "proj", CancellationToken.None);
        Assert.Equal(3, index.Revisions.Count);
    }

    [Fact]
    public async Task SnapshotWrite_IsWriteOnce_ByteStableAfterLiveEdits()
    {
        var workspace = new MemoryWorkspace();
        Assert.True(await RevisionSnapshots.TryWriteSnapshotAsync(workspace, 1, "proj", Snapshot(41, "original"), CancellationToken.None));
        Assert.False(await RevisionSnapshots.TryWriteSnapshotAsync(workspace, 1, "proj", Snapshot(41, "edited"), CancellationToken.None));
        var reread = await RevisionSnapshots.ReadSnapshotAsync(workspace, 1, "proj", 41, CancellationToken.None);
        Assert.Contains("original", reread!.Endpoints.Single().GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndAndExtendGrace_RoundTrip()
    {
        var workspace = new MemoryWorkspace();
        var now = DateTimeOffset.UtcNow;
        await RevisionSnapshots.RecordPublishAsync(workspace, 1, "proj", 41, now, 3, _ => false, CancellationToken.None);
        await RevisionSnapshots.RecordPublishAsync(workspace, 1, "proj", 42, now, 3, _ => false, CancellationToken.None);
        Assert.True(await RevisionSnapshots.EndGraceAsync(workspace, 1, "proj", 41, CancellationToken.None));
        var ended = await RevisionSnapshots.ReadIndexAsync(workspace, 1, "proj", CancellationToken.None);
        Assert.True(ended.Find(41)!.GraceEnded);
        var until = now.AddHours(5);
        Assert.True(await RevisionSnapshots.ExtendGraceAsync(workspace, 1, "proj", 41, until, CancellationToken.None));
        var extended = await RevisionSnapshots.ReadIndexAsync(workspace, 1, "proj", CancellationToken.None);
        Assert.False(extended.Find(41)!.GraceEnded);
        Assert.Equal(until, extended.Find(41)!.GraceExtendedUntilUtc);
        Assert.False(await RevisionSnapshots.EndGraceAsync(workspace, 1, "proj", 999, CancellationToken.None));
    }

    [Fact]
    public async Task MissingIndex_ReadsEmpty()
    {
        var workspace = new MemoryWorkspace();
        var index = await RevisionSnapshots.ReadIndexAsync(workspace, 1, "proj", CancellationToken.None);
        Assert.Empty(index.Revisions);
        Assert.Null(index.LiveRevisionId);
        Assert.Null(await RevisionSnapshots.ReadSnapshotAsync(workspace, 1, "proj", 41, CancellationToken.None));
    }
}
