using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>Bounded, best-effort telemetry. Database access is never performed by request threads.</summary>
public sealed class ClientVersionTracker(TimeProvider time)
{
    public const int MaxGroups = 2000;
    private readonly Lock _gate = new();
    private Dictionary<ClientVersionKey, Counter> _pending = new();
    private readonly HashSet<ClientVersionKey> _seenToday = new();
    private int _day = -1;
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);
    public void Record(string projectId, ClientIdentity identity, string? playerId)
    {
        if (projectId.Length is < 1 or > 128) return;
        var now = time.GetUtcNow();
        var key = new ClientVersionKey(projectId, DateOnly.FromDateTime(now.UtcDateTime).DayNumber,
            identity.LibraryVersion, identity.LibraryRevision, identity.ClientType ?? "unknown", identity.RevisionId);
        lock (_gate)
        {
            if (_day != key.Day) { _seenToday.Clear(); _day = key.Day; }
            if (!_pending.TryGetValue(key, out var counter))
            {
                if (_pending.Count >= MaxGroups || (!_seenToday.Contains(key) && _seenToday.Count >= MaxGroups))
                { Interlocked.Increment(ref _dropped); return; }
                _seenToday.Add(key);
                _pending[key] = counter = new Counter();
            }
            counter.Requests++;
            counter.LastSeen = now.ToUnixTimeMilliseconds();
            if (playerId is not null) PlayerCountSketch.Add(counter.Sketch, projectId, playerId);
        }
    }
    public IReadOnlyList<ClientVersionDelta> Drain()
    {
        Dictionary<ClientVersionKey, Counter> pending;
        lock (_gate) { pending = _pending; _pending = new(); }
        return pending.Select(pair => new ClientVersionDelta(pair.Key, pair.Value.Requests, pair.Value.LastSeen, pair.Value.Sketch)).ToArray();
    }
    private sealed class Counter
    {
        public long Requests;
        public long LastSeen;
        public readonly byte[] Sketch = new byte[PlayerCountSketch.Size];
    }
}
