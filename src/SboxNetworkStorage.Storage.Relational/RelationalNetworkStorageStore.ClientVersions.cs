using System.Data;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>Daily client-version aggregates (schema v5): buffered analytics flushes merge here, never game data.</summary>
public abstract partial class RelationalNetworkStorageStore : IClientVersionStore
{
    public async Task WriteClientVersionsAsync(IReadOnlyList<ClientVersionDelta> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (var delta in batch)
        {
            StoreValidation.Id(delta.Key.ProjectId);
            if (delta.Requests <= 0) throw new ArgumentOutOfRangeException(nameof(batch));
            var sketch = new byte[PlayerCountSketch.Size];
            PlayerCountSketch.Merge(sketch, delta.PlayerSketch);
            await using (var read = Command(connection,
                $"SELECT player_sketch FROM {TablePrefix}client_versions_daily WHERE project_id = @project AND day = @day AND group_key = @group",
                [Text("project", delta.Key.ProjectId), Int32("day", delta.Key.Day), Text("group", delta.Key.GroupKey)], transaction))
            {
                if (await read.ExecuteScalarAsync(ct) is string existing) PlayerCountSketch.Merge(sketch, Convert.FromBase64String(existing));
            }
            await using var write = Command(connection, $"""
                INSERT INTO {TablePrefix}client_versions_daily
                    (project_id, day, group_key, library_version, library_revision, client_type, game_revision, requests, last_seen_unix_ms, player_sketch)
                VALUES (@project, @day, @group, @version, @revision, @type, @game, @requests, @seen, @sketch)
                ON CONFLICT (project_id, day, group_key) DO UPDATE SET
                    requests = {TablePrefix}client_versions_daily.requests + excluded.requests,
                    last_seen_unix_ms = CASE WHEN {TablePrefix}client_versions_daily.last_seen_unix_ms > excluded.last_seen_unix_ms THEN {TablePrefix}client_versions_daily.last_seen_unix_ms ELSE excluded.last_seen_unix_ms END,
                    player_sketch = excluded.player_sketch
                """, [Text("project", delta.Key.ProjectId), Int32("day", delta.Key.Day), Text("group", delta.Key.GroupKey),
                    Text("version", delta.Key.LibraryVersion), Int64("revision", delta.Key.LibraryRevision), Text("type", delta.Key.ClientType),
                    Int64("game", delta.Key.GameRevision), Int64("requests", delta.Requests), Int64("seen", delta.LastSeenUnixMs), Text("sketch", Convert.ToBase64String(sketch))], transaction);
            await write.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ClientVersionRow>> ReadClientVersionsAsync(string projectId, int sinceDay, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = Command(connection,
            $"SELECT day, library_version, library_revision, client_type, game_revision, requests, last_seen_unix_ms, player_sketch FROM {TablePrefix}client_versions_daily WHERE project_id = @project AND day >= @day ORDER BY day DESC, requests DESC",
            [Text("project", projectId), Int32("day", sinceDay)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ClientVersionRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ClientVersionRow(new ClientVersionKey(projectId, reader.GetInt32(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)),
                reader.GetInt64(5), reader.GetInt64(6), Convert.FromBase64String(reader.GetString(7))));
        return rows;
    }

    public Task PurgeClientVersionsAsync(int beforeDay, CancellationToken ct)
        => ExecuteAsync($"DELETE FROM {TablePrefix}client_versions_daily WHERE day < @day", ct, Int32("day", beforeDay));
}
