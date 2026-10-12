using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>
/// Cached module enablement for the request path. The endpoint executor's
/// access gate needs a synchronous lookup, so the handler resolves it once
/// per request through this 30-second per-project cache. Fail-closed: when
/// module state cannot be read, every module reads as disabled (module
/// endpoints deny; ordinary definitions are unaffected).
/// </summary>
public sealed class ModuleStateCache(
    IMemoryCache cache,
    IServiceScopeFactory scopes,
    ModuleOwnerOptions owner,
    ILogger<ModuleStateCache> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>Module id → disabled. Never throws: load failure denies all modules.</summary>
    public async Task<Func<string, bool>> GetDisabledLookupAsync(string projectId, CancellationToken ct)
    {
        var cacheKey = $"module-disabled:{projectId}";
        if (cache.TryGetValue(cacheKey, out Func<string, bool>? cached) && cached is not null)
            return cached;
        Func<string, bool> lookup = _ => true;
        try
        {
            using var scope = scopes.CreateScope();
            var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
            var state = await states.LoadAsync(owner.OwnerUserId, projectId, ct);
            lookup = ModuleStateStore.DisabledLookup(state);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Module state unreadable for project {ProjectId}; treating all modules as disabled.", projectId);
        }
        cache.Set(cacheKey, lookup, Ttl);
        return lookup;
    }

    public void Invalidate(string projectId) => cache.Remove($"module-disabled:{projectId}");
}
