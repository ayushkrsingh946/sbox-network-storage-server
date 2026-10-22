namespace SboxNetworkStorage.Storage.Relational;

/// <summary>Auth-session revocation markers (schema v12). Stores session ids only, never tokens.</summary>
public abstract partial class RelationalNetworkStorageStore : IAuthSessionRevocationStore
{
    public Task RevokeAsync(string projectId, string sessionId, long revokedAtUnixMs, long purgeAfterUnixMs, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        // A restored marker must never shorten the retention required by either copy of this family.
        return ExecuteAsync($"""
            INSERT INTO {T("auth_session_revocations")} AS t (project_id, session_id, revoked_at_unix_ms, purge_after_unix_ms)
            VALUES (@project, @session, @revoked, @purge)
            ON CONFLICT (project_id, session_id) DO UPDATE SET
                revoked_at_unix_ms = CASE WHEN t.revoked_at_unix_ms < excluded.revoked_at_unix_ms THEN t.revoked_at_unix_ms ELSE excluded.revoked_at_unix_ms END,
                purge_after_unix_ms = CASE WHEN t.purge_after_unix_ms > excluded.purge_after_unix_ms THEN t.purge_after_unix_ms ELSE excluded.purge_after_unix_ms END
            """, ct, Text("project", projectId), Text("session", sessionId), Int64("revoked", revokedAtUnixMs), Int64("purge", purgeAfterUnixMs));
    }

    public async Task<bool> IsRevokedAsync(string projectId, string sessionId, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        var count = await QueryInt64ScalarAsync($"""
            SELECT COUNT(*) FROM {T("auth_session_revocations")} WHERE project_id = @project AND session_id = @session
            """, ct, Text("project", projectId), Text("session", sessionId));
        return count > 0;
    }

    public Task<int> PurgeExpiredAsync(long nowUnixMs, CancellationToken ct)
        => ExecuteCountAsync($"""
            DELETE FROM {T("auth_session_revocations")} WHERE purge_after_unix_ms < @now
            """, ct, Int64("now", nowUnixMs));

    public async Task<IReadOnlyList<AuthSessionRevocation>> ListAsync(string projectId, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            SELECT session_id, revoked_at_unix_ms, purge_after_unix_ms FROM {T("auth_session_revocations")}
            WHERE project_id = @project ORDER BY session_id
            """, [Text("project", projectId)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AuthSessionRevocation>();
        while (await reader.ReadAsync(ct))
            rows.Add(new AuthSessionRevocation(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }
}
