using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Compat;

public sealed record ClientVersionSummary(string LibraryVersion, long? LibraryRevision, string ClientType, long? GameRevision,
    long Requests, long EstimatedPlayers, DateTimeOffset LastSeen, string Status, string? Sunset);
/// <param name="Truncated">True when the store held more than <see cref="RelationalNetworkStorageStore.ReadLimit"/> rows: the dashboard shows a partial tail.</param>
public sealed record ClientVersionReport(int Days, long Requests, long EstimatedPlayers, double OutdatedPercent,
    double AffectedPercent, string? Minimum, long DroppedRequests, IReadOnlyList<ClientVersionSummary> Versions, bool Truncated = false);

/// <summary>
/// Reads the Clients report for one project: stored daily aggregates grouped by identity, with the status
/// each group has under the current matrix (the release-feed overlay included, as in the response headers)
/// overridden to <c>unsupported</c> below the project's explicit minimum. Reporting only; refusing a
/// request is <see cref="LibraryPolicyMiddleware"/>, which never uses the overlay. Reads are bounded by
/// <see cref="RelationalNetworkStorageStore.ReadLimit"/> rows (oldest, quietest groups drop first);
/// <see cref="ClientVersionReport.Truncated"/> says the tail was cut.
/// </summary>
public sealed class ClientVersionsQuery(IClientVersionStore store, LibraryCompatProvider compat, ClientVersionTracker tracker, TimeProvider time)
{
    public async Task<ClientVersionReport> ReadAsync(string projectId, int days, string? minimum, CancellationToken ct)
    {
        if (days is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(days), "Days must be 1–90.");
        var stored = await store.ReadClientVersionsAsync(projectId, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1 - days).DayNumber, ct);
        // The store returns one row past its read limit so a truncated tail is detectable. The
        // limit query keeps day/requests ordering, so the dropped rows are the least useful ones.
        var truncated = stored.Count > RelationalNetworkStorageStore.ReadLimit;
        var rows = truncated ? stored.Take(RelationalNetworkStorageStore.ReadLimit).ToArray() : stored;
        var matrix = compat.Current;
        var allPlayers = new byte[PlayerCountSketch.Size];
        var versions = rows.GroupBy(row => new { row.Key.LibraryVersion, row.Key.LibraryRevision, row.Key.ClientType, row.Key.GameRevision }).Select(group =>
        {
            var key = group.Key;
            var sketch = new byte[PlayerCountSketch.Size];
            foreach (var row in group) { PlayerCountSketch.Merge(sketch, row.PlayerSketch); PlayerCountSketch.Merge(allPlayers, row.PlayerSketch); }
            var verdict = matrix.Evaluate(StoredIdentity(key.LibraryVersion, key.LibraryRevision, key.ClientType, key.GameRevision));
            // A legacy row evaluates to Unknown and BelowMinimum is false for it, so it stays Unknown here.
            var status = verdict.Status is LibraryStatus.Blocked ? LibraryStatus.Blocked
                : BelowMinimum(key.LibraryVersion, minimum) ? LibraryStatus.Unsupported
                : verdict.Status;
            return new ClientVersionSummary(key.LibraryVersion, key.LibraryRevision, key.ClientType, key.GameRevision,
                group.Sum(row => row.Requests), PlayerCountSketch.Estimate(sketch), DateTimeOffset.FromUnixTimeMilliseconds(group.Max(row => row.LastSeenUnixMs)),
                status.ToWire(), verdict.Sunset);
        }).OrderByDescending(row => row.Requests).ToArray();
        var total = versions.Sum(row => row.Requests);
        double Share(Func<ClientVersionSummary, bool> predicate) => total == 0 ? 0 : 100d * versions.Where(predicate).Sum(row => row.Requests) / total;
        return new ClientVersionReport(days, total, PlayerCountSketch.Estimate(allPlayers), Share(row => row.Status is "outdated" or "deprecated" or "unsupported" or "blocked"),
            Share(row => row.Status is "unsupported" or "blocked"), minimum, tracker.Dropped, versions, truncated);
    }

    private static ClientIdentity StoredIdentity(string libraryVersion, long? libraryRevision, string clientType, long? gameRevision)
    {
        var library = ClientIdentity.TryParseLibraryVersion(libraryVersion, out var version) ? version : null;
        return new ClientIdentity(
            library?.ToString() ?? ClientIdentity.Legacy,
            library,
            libraryRevision,
            ClientIdentity.ParseClientType(clientType),
            gameRevision,
            null);
    }

    /// <summary>
    /// True when <paramref name="version"/> parses as SemVer below the parsed <paramref name="minimum"/>.
    /// Legacy, missing and unparseable versions are never below a minimum: they stay accepted.
    /// </summary>
    public static bool BelowMinimum(string version, string? minimum)
        => minimum is not null
            && ClientIdentity.TryParseLibraryVersion(minimum, out var floor)
            && ClientIdentity.TryParseLibraryVersion(version, out var actual)
            && actual.CompareTo(floor) < 0;
}
