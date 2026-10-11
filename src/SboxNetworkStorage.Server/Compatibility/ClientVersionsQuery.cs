using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Compatibility;

public sealed record ClientVersionSummary(string LibraryVersion, long? LibraryRevision, string ClientType, long? GameRevision,
    long Requests, long EstimatedPlayers, DateTimeOffset LastSeen, string Status, string? Sunset);
public sealed record ClientVersionReport(int Days, long Requests, long EstimatedPlayers, double OutdatedPercent,
    double AffectedPercent, string? Minimum, long DroppedRequests, IReadOnlyList<ClientVersionSummary> Versions);

public sealed class ClientVersionsQuery(IClientVersionStore store, LibraryCompatibility compatibility, ClientVersionTracker tracker, TimeProvider time)
{
    public async Task<ClientVersionReport> ReadAsync(string projectId, int days, string? minimum, CancellationToken ct)
    {
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days), "Days must be 1–90.");
        var rows = await store.ReadClientVersionsAsync(projectId, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1 - days).DayNumber, ct);
        var allPlayers = new byte[PlayerCountSketch.Size];
        var versions = rows.GroupBy(row => new { row.Key.LibraryVersion, row.Key.LibraryRevision, row.Key.ClientType, row.Key.GameRevision }).Select(group =>
        {
            var sketch = new byte[PlayerCountSketch.Size];
            foreach (var row in group) { PlayerCountSketch.Merge(sketch, row.PlayerSketch); PlayerCountSketch.Merge(allPlayers, row.PlayerSketch); }
            var key = group.Key;
            var status = compatibility.StatusFor(key.LibraryVersion);
            if (status != "blocked" && BelowMinimum(key.LibraryVersion, minimum)) status = "unsupported";
            return new ClientVersionSummary(key.LibraryVersion, key.LibraryRevision, key.ClientType, key.GameRevision,
                group.Sum(row => row.Requests), PlayerCountSketch.Estimate(sketch), DateTimeOffset.FromUnixTimeMilliseconds(group.Max(row => row.LastSeenUnixMs)),
                status, compatibility.SunsetFor(key.LibraryVersion));
        }).OrderByDescending(row => row.Requests).ToArray();
        var total = versions.Sum(row => row.Requests);
        double Share(Func<ClientVersionSummary, bool> predicate) => total == 0 ? 0 : 100d * versions.Where(predicate).Sum(row => row.Requests) / total;
        return new ClientVersionReport(days, total, PlayerCountSketch.Estimate(allPlayers), Share(row => row.Status is "outdated" or "deprecated" or "unsupported" or "blocked"),
            Share(row => row.Status is "unsupported" or "blocked"), minimum, tracker.Dropped, versions);
    }
    public static bool BelowMinimum(string version, string? minimum)
        => minimum is not null && ClientIdentity.TryVersion(minimum, out var floor)
            && (!ClientIdentity.TryVersion(version, out var actual) || actual.CompareTo(floor) < 0);
}
