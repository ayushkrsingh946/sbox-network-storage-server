using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Application.NetworkStorage.AuthSessions;

/// <summary>
/// HMAC auth-session token service. Byte-for-byte token-compatible with
/// the legacy server <c>services/network-storage-auth-sessions.js</c> implementation:
/// <list type="bullet">
/// <item>token = <c>sbox_sess_</c> + <c>base64url(JSON payload)</c> + <c>"."</c> + <c>base64url(HMAC-SHA256(payload))</c>,</item>
/// <item>validation re-signs the <i>raw payload substring</i> (never re-serializes),
///   so a token minted by either runtime validates under the other given the same secret.</item>
/// </list>
/// Tokens are stateless, but revocation is not: a session family (the payload <c>id</c>, shared by a session and every
/// token refreshed from it) is revoked by a durable marker in <see cref="IAuthSessionRevocationStore"/>. Every check
/// that accepts a token consults the marker, so a revoked family stops validating immediately and stays revoked across
/// restarts. Store failures propagate, so an operation that cannot read or write the marker fails closed.
/// </summary>
public sealed class NetworkStorageAuthSessionService : INetworkStorageAuthSessionService
{
    public const string TokenPrefix = "sbox_sess_";
    public const int DefaultTtlSeconds = 60 * 60;
    public const int MinTtlSeconds = 60;
    public const int MaxTtlSeconds = 24 * 60 * 60;

    /// <summary>
    /// Extra retention beyond the maximum token lifetime to tolerate clock skew. Tokens minted before a revoke
    /// commits expire within MaxTtlSeconds of that revocation; the post-mint check rejects an overlapping refresh.
    /// </summary>
    public const int RevocationGraceSeconds = 60 * 60;

    private static readonly JsonSerializerOptions PayloadJson = new();

    private readonly IAuthSessionSecretProvider _secretProvider;
    private readonly IAuthSessionRevocationStore _revocations;
    private readonly TimeProvider _time;

    public NetworkStorageAuthSessionService(IAuthSessionSecretProvider secretProvider, IAuthSessionRevocationStore revocations, TimeProvider time)
    {
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        _revocations = revocations ?? throw new ArgumentNullException(nameof(revocations));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Mirror of legacy server <c>normalizeAuthSessionTtlSeconds</c>: clamp to [min, max].</summary>
    public static int NormalizeTtl(int value) => Math.Clamp(value, MinTtlSeconds, MaxTtlSeconds);

    public AuthSessionOutcome Create(long userId, string projectId, string steamId, int ttlSeconds, IReadOnlyDictionary<string, object?>? metadata = null)
        => Mint(userId, projectId, steamId, ttlSeconds, metadata, familyId: null);

    public async Task<AuthSessionOutcome> ValidateAsync(string projectId, string token, string steamId = "", CancellationToken ct = default)
    {
        var verified = Verify(projectId, token, steamId);
        if (!verified.Ok) return verified;
        if (await _revocations.IsRevokedAsync(projectId, verified.Session!.Id, ct)) return RevokedFailure();
        return verified;
    }

    public async Task<AuthSessionOutcome> RefreshAsync(string projectId, string token, int ttlSeconds, CancellationToken ct = default)
    {
        var valid = await ValidateAsync(projectId, token, ct: ct);
        if (!valid.Ok) return valid;

        var session = valid.Session!;
        var minted = Mint(session.UserId, session.ProjectId, session.SteamId, ttlSeconds,
            new Dictionary<string, object?> { ["refreshedFrom"] = session.Id }, familyId: session.Id);
        // Checked again after minting: a revoke that completed while the token was being minted must still win.
        if (await _revocations.IsRevokedAsync(projectId, session.Id, ct)) return RevokedFailure();
        return minted;
    }

    public async Task<AuthSessionOutcome> RevokeAsync(string projectId, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
            return AuthSessionOutcome.Failure("AUTH_SESSION_INVALID", "Auth session token is required.");

        // Signature, project and expiry are checked; the revocation marker is not, so revoking is idempotent.
        var verified = Verify(projectId, token, steamId: "");
        if (!verified.Ok) return verified;

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var purgeAfter = now + (MaxTtlSeconds + RevocationGraceSeconds) * 1000L;
        await _revocations.PurgeExpiredAsync(now, ct);
        await _revocations.RevokeAsync(projectId, verified.Session!.Id, now, purgeAfter, ct);

        return new AuthSessionOutcome(true, null, null, null, 0, verified.Session! with { RevokedAt = IsoFromMs(now) });
    }

    private AuthSessionOutcome Mint(long userId, string projectId, string steamId, int ttlSeconds, IReadOnlyDictionary<string, object?>? metadata, string? familyId)
    {
        var ttl = NormalizeTtl(ttlSeconds);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAt = now + (long)ttl * 1000L;
        var id = familyId ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var payloadObject = new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["id"] = id,
            ["userId"] = userId,
            ["projectId"] = projectId,
            ["steamId"] = steamId,
            ["createdAt"] = now,
            ["expiresAt"] = expiresAt,
            ["metadata"] = metadata ?? new Dictionary<string, object?>(),
        };

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payloadObject, PayloadJson));
        var token = $"{TokenPrefix}{payload}.{Sign(payload)}";
        var session = new AuthSessionView(id, projectId, userId, steamId, IsoFromMs(now), IsoFromMs(expiresAt), null);
        return new AuthSessionOutcome(true, null, null, token, ttl, session);
    }

    /// <summary>Checks the token's prefix, signature, payload, project, expiry and Steam ID. Does not consult revocation.</summary>
    private AuthSessionOutcome Verify(string projectId, string token, string steamId)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return AuthSessionOutcome.Failure("AUTH_SESSION_INVALID", "Auth session token is missing or invalid.");

        var packed = token[TokenPrefix.Length..];
        var dot = packed.LastIndexOf('.');
        if (dot <= 0)
            return AuthSessionOutcome.Failure("AUTH_SESSION_INVALID", "Auth session token is malformed.");

        var payload = packed[..dot];
        var signature = packed[(dot + 1)..];
        if (!FixedTimeEquals(signature, Sign(payload)))
            return AuthSessionOutcome.Failure("AUTH_SESSION_INVALID", "Auth session signature is invalid.");

        Dictionary<string, JsonElement>? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlDecode(payload));
        }
        catch
        {
            return AuthSessionOutcome.Failure("AUTH_SESSION_INVALID", "Auth session payload is invalid.");
        }
        if (decoded is null)
            return AuthSessionOutcome.Failure("AUTH_SESSION_INVALID", "Auth session payload is invalid.");

        if (ReadString(decoded, "projectId") != (projectId ?? string.Empty))
            return AuthSessionOutcome.Failure("AUTH_SESSION_INVALID", "Auth session project does not match this request.");

        if (ReadLong(decoded, "expiresAt") <= _time.GetUtcNow().ToUnixTimeMilliseconds())
            return AuthSessionOutcome.Failure("AUTH_SESSION_EXPIRED", "Auth session has expired. Create a new session.");

        var sessionSteamId = ReadString(decoded, "steamId");
        if (!string.IsNullOrEmpty(steamId) && steamId != sessionSteamId)
            return AuthSessionOutcome.Failure("AUTH_SESSION_STEAMID_MISMATCH", "Auth session does not belong to the supplied Steam ID.");

        return new AuthSessionOutcome(true, null, null, null, 0, SessionFromDecoded(decoded));
    }

    private static AuthSessionOutcome RevokedFailure()
        => AuthSessionOutcome.Failure("AUTH_SESSION_REVOKED", "Auth session has been revoked. Create a new session.");

    private string Sign(string payload)
    {
        var secret = _secretProvider.GetSecret();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static AuthSessionView SessionFromDecoded(Dictionary<string, JsonElement> decoded)
        => new(
            ReadString(decoded, "id"),
            ReadString(decoded, "projectId"),
            ReadLong(decoded, "userId"),
            ReadString(decoded, "steamId"),
            IsoFromMs(ReadLong(decoded, "createdAt")),
            IsoFromMs(ReadLong(decoded, "expiresAt")),
            null);

    private static string ReadString(Dictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty,
        };
    }

    private static long ReadLong(Dictionary<string, JsonElement> map, string key)
        => map.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0L;

    private static string IsoFromMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
