using Microsoft.Extensions.Caching.Memory;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>Short, bounded per-project library-minimum cache. Owner changes invalidate immediately; external edits age out in 30 seconds.</summary>
public sealed class LibraryPolicyCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    public async Task<string?> MinimumAsync(INetworkStorageProjectService projects, string projectId, CancellationToken ct)
    {
        if (_cache.TryGetValue(projectId, out Policy? cached)) return cached!.Minimum;
        var access = await projects.ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, projectId, ct);
        string? minimum = null;
        if (ClientIdentity.TryParseLibraryVersion(access?.Project.LibraryMinimum, out var version)) minimum = version.ToString();
        _cache.Set(projectId, new Policy(minimum), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });
        return minimum;
    }
    public void Invalidate(string projectId) => _cache.Remove(projectId);
    public void Dispose() => _cache.Dispose();
    private sealed record Policy(string? Minimum);
}
