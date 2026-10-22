using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.Support;

/// <summary>
/// Test-only <see cref="IAuthSessionRevocationStore"/> with the same retention merging and purge semantics as the
/// relational stores. Used by unit tests that do not need a database.
/// </summary>
public sealed class InMemoryAuthSessionRevocationStore : IAuthSessionRevocationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string ProjectId, string SessionId), (long RevokedAt, long PurgeAfter)> _markers = new();

    public int Count
    {
        get { lock (_gate) return _markers.Count; }
    }

    public Task RevokeAsync(string projectId, string sessionId, long revokedAtUnixMs, long purgeAfterUnixMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = (projectId, sessionId);
            if (_markers.TryGetValue(key, out var existing))
                _markers[key] = (Math.Min(existing.RevokedAt, revokedAtUnixMs), Math.Max(existing.PurgeAfter, purgeAfterUnixMs));
            else
                _markers.Add(key, (revokedAtUnixMs, purgeAfterUnixMs));
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsRevokedAsync(string projectId, string sessionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_markers.ContainsKey((projectId, sessionId)));
    }

    public Task<int> PurgeExpiredAsync(long nowUnixMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var expired = _markers.Where(pair => pair.Value.PurgeAfter < nowUnixMs).Select(pair => pair.Key).ToList();
            foreach (var key in expired) _markers.Remove(key);
            return Task.FromResult(expired.Count);
        }
    }

    public Task<IReadOnlyList<AuthSessionRevocation>> ListAsync(string projectId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<AuthSessionRevocation> rows = _markers
                .Where(pair => pair.Key.ProjectId == projectId)
                .OrderBy(pair => pair.Key.SessionId, StringComparer.Ordinal)
                .Select(pair => new AuthSessionRevocation(pair.Key.SessionId, pair.Value.RevokedAt, pair.Value.PurgeAfter))
                .ToList();
            return Task.FromResult(rows);
        }
    }
}
