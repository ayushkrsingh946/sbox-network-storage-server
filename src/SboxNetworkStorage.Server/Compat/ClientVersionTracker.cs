using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>Bounded, best-effort telemetry. Database access is never performed by request threads.</summary>
public sealed class ClientVersionTracker(TimeProvider time)
{
    /// <summary>
    /// Fair share of distinct groups per project per day. Public keys can mint groups by rotating
    /// the revision headers, so one project can never take more than this: every other project
    /// keeps its own share instead of starving behind a global first-come cap.
    /// </summary>
    public const int MaxGroupsPerProject = 500;
    /// <summary>Global bound on buffered groups across every project: the memory ceiling.</summary>
    public const int MaxTotalGroups = 2000;
    /// <summary>
    /// Most groups one <see cref="Drain"/> returns. The writer flushes once a minute, so a full
    /// buffer drains over several ticks and no single flush holds the store write lock for long.
    /// </summary>
    public const int MaxFlushGroups = 200;

    private readonly Lock _gate = new();
    private Dictionary<ClientVersionKey, Counter> _pending = new();
    private readonly Dictionary<string, int> _pendingByProject = new(StringComparer.Ordinal);
    private readonly HashSet<ClientVersionKey> _seenToday = new();
    private readonly Dictionary<string, int> _seenTodayByProject = new(StringComparer.Ordinal);
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
            if (_day != key.Day) { _seenToday.Clear(); _seenTodayByProject.Clear(); _day = key.Day; }
            if (!_pending.TryGetValue(key, out var counter))
            {
                _pendingByProject.TryGetValue(projectId, out var projectPending);
                _seenTodayByProject.TryGetValue(projectId, out var projectSeen);
                if (_pending.Count >= MaxTotalGroups || projectPending >= MaxGroupsPerProject
                    || (!_seenToday.Contains(key) && projectSeen >= MaxGroupsPerProject))
                { Interlocked.Increment(ref _dropped); return; }
                if (_seenToday.Add(key)) _seenTodayByProject[projectId] = projectSeen + 1;
                _pending[key] = counter = new Counter();
                _pendingByProject[projectId] = projectPending + 1;
            }
            counter.Requests++;
            counter.LastSeen = now.ToUnixTimeMilliseconds();
            if (playerId is not null) PlayerCountSketch.Add(counter.Sketch, projectId, playerId);
        }
    }
    /// <summary>
    /// Removes and returns up to <see cref="MaxFlushGroups"/> pending groups, oldest first. Any
    /// remainder stays queued for the next tick. The daily cardinality budget is untouched by
    /// flushing: groups already seen today keep their slot, so draining never re-opens the cap.
    /// </summary>
    public IReadOnlyList<ClientVersionDelta> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count <= MaxFlushGroups)
            {
                var flushed = _pending.Select(pair => new ClientVersionDelta(pair.Key, pair.Value.Requests, pair.Value.LastSeen, pair.Value.Sketch)).ToArray();
                _pending = new();
                _pendingByProject.Clear();
                return flushed;
            }
            var batch = new List<ClientVersionDelta>(MaxFlushGroups);
            var remaining = new Dictionary<ClientVersionKey, Counter>(_pending.Count - MaxFlushGroups);
            var remainingByProject = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in _pending)
            {
                if (batch.Count < MaxFlushGroups)
                {
                    batch.Add(new ClientVersionDelta(pair.Key, pair.Value.Requests, pair.Value.LastSeen, pair.Value.Sketch));
                }
                else
                {
                    remaining.Add(pair.Key, pair.Value);
                    remainingByProject.TryGetValue(pair.Key.ProjectId, out var left);
                    remainingByProject[pair.Key.ProjectId] = left + 1;
                }
            }
            _pending = remaining;
            _pendingByProject.Clear();
            foreach (var pair in remainingByProject) _pendingByProject.Add(pair.Key, pair.Value);
            return batch;
        }
    }
    private sealed class Counter
    {
        public long Requests;
        public long LastSeen;
        public readonly byte[] Sketch = new byte[PlayerCountSketch.Size];
    }
}
