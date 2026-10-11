using Microsoft.Extensions.Caching.Memory;

namespace SboxNetworkStorage.Server.Compatibility;

/// <summary>Short, bounded policy cache. Owner changes invalidate immediately; external edits age out in 30 seconds.</summary>
public sealed class LibraryPolicyCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    public async Task<string?> MinimumAsync(INetworkStorageStore store, string projectId, CancellationToken ct)
    {
        if (_cache.TryGetValue(projectId, out Policy? cached)) return cached!.Minimum;
        var row = await store.ReadProjectAsync(projectId, ct);
        string? minimum = null;
        if (row is { ValueKind: System.Text.Json.JsonValueKind.Object } project && project.TryGetProperty("libraryMinimum", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String && ClientIdentity.TryVersion(value.GetString(), out var version)) minimum = version.ToString();
        _cache.Set(projectId, new Policy(minimum), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });
        return minimum;
    }
    public void Invalidate(string projectId) => _cache.Remove(projectId);
    public void Dispose() => _cache.Dispose();
    private sealed record Policy(string? Minimum);
}
