namespace SboxNetworkStorage.Storage;

/// <summary>
/// Durable revocation markers for auth-session families (schema v12). A marker names a session family by its id,
/// never a raw token. Auth-session tokens are stateless and verified by signature, so a marker is the only state that
/// makes a revoked token stop validating, including after a restart.
/// </summary>
public interface IAuthSessionRevocationStore
{
    /// <summary>
    /// Records that a session family is revoked, retaining the earliest revocation and latest purge time.
    /// </summary>
    Task RevokeAsync(string projectId, string sessionId, long revokedAtUnixMs, long purgeAfterUnixMs, CancellationToken ct);

    /// <summary>True when a revocation marker exists for the session family.</summary>
    Task<bool> IsRevokedAsync(string projectId, string sessionId, CancellationToken ct);

    /// <summary>
    /// Deletes markers whose purge time has passed. A marker is only needed while every token it covers can still be
    /// unexpired, so a purged marker can no longer revoke anything that would still validate.
    /// </summary>
    Task<int> PurgeExpiredAsync(long nowUnixMs, CancellationToken ct);

    /// <summary>Every revocation marker of a project, ordered by session id. Export archives them.</summary>
    Task<IReadOnlyList<AuthSessionRevocation>> ListAsync(string projectId, CancellationToken ct);
}

/// <summary>A revocation marker as exported to and restored from an archive.</summary>
public sealed record AuthSessionRevocation(string SessionId, long RevokedAtUnixMs, long PurgeAfterUnixMs);
