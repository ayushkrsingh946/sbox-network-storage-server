namespace SboxNetworkStorage.Storage;

/// <summary>Daily compatibility telemetry, separate from game data and portable project snapshots.</summary>
public interface IClientVersionStore
{
    Task WriteClientVersionsAsync(IReadOnlyList<ClientVersionDelta> batch, CancellationToken ct);
    Task<IReadOnlyList<ClientVersionRow>> ReadClientVersionsAsync(string projectId, int sinceDay, CancellationToken ct);
    Task PurgeClientVersionsAsync(int beforeDay, CancellationToken ct);
}

public sealed record ClientVersionKey(string ProjectId, int Day, string LibraryVersion, long? LibraryRevision, string ClientType, long? GameRevision)
{
    public string GroupKey => $"{LibraryVersion}|{LibraryRevision}|{ClientType}|{GameRevision}";
}
public sealed record ClientVersionDelta(ClientVersionKey Key, long Requests, long LastSeenUnixMs, byte[] PlayerSketch);
public sealed record ClientVersionRow(ClientVersionKey Key, long Requests, long LastSeenUnixMs, byte[] PlayerSketch)
{
    public long EstimatedPlayers => PlayerCountSketch.Estimate(PlayerSketch);
}
