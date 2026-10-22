using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Endpoints;

/// <summary>
/// Outcome of <see cref="PlayerIdentity.ResolveAsync"/>: the verified player, or the
/// rejection (status, code, message) the caller renders in its own wire shape.
/// </summary>
internal readonly record struct PlayerIdentityResult(string? SteamId, int Status, string? Code, string? Message)
{
    public bool Ok => SteamId is not null;

    public static PlayerIdentityResult Verified(string steamId) => new(steamId, StatusCodes.Status200OK, null, null);

    public static PlayerIdentityResult Reject(string code, string message, int status = StatusCodes.Status401Unauthorized)
        => new(null, status, code, message);
}

/// <summary>
/// Player identity for public-key (game client) requests: auth-session tokens,
/// s&amp;box tokens (direct and host-proxied) and the claimed Steam ID. Shared by
/// endpoint execution and the direct collection document API so both verify
/// players identically.
/// </summary>
internal static class PlayerIdentity
{
    /// <summary>The Steam ID the caller claims, from headers, query or the JSON body (trimmed; first supplied wins).</summary>
    internal static string ClaimedSteamId(HttpRequest request, JsonElement body) => ClaimValues(request, body, includeHostHeader: true).FirstOrDefault() ?? "";

    /// <summary>The s&amp;box token presented with the request (header, query or body), or null.</summary>
    internal static string? ReadHostToken(HttpRequest request, JsonElement body) => FirstNonEmpty(
        request.Headers["x-sbox-token"].FirstOrDefault(),
        request.Headers["x-sbox-auth-token"].FirstOrDefault(),
        request.Query["token"].FirstOrDefault(), ReadBodyString(body, "token"));

    /// <summary>True when every Steam ID the request supplies, from any source, equals <paramref name="expectedClaim"/>.</summary>
    internal static bool SuppliedClaimsMatch(HttpRequest request, JsonElement body, string expectedClaim)
        => ClaimValues(request, body, includeHostHeader: true).All(claim => string.Equals(claim, expectedClaim, StringComparison.Ordinal));

    /// <summary>Every non-blank, trimmed Steam ID claim, in precedence order. The host header is included only when asked.</summary>
    private static IEnumerable<string> ClaimValues(HttpRequest request, JsonElement body, bool includeHostHeader)
    {
        var sources = new[]
        {
            request.Headers["x-steam-id"].FirstOrDefault(),
            includeHostHeader ? request.Headers["x-sbox-steam-id"].FirstOrDefault() : null,
            request.Query["steamId"].FirstOrDefault(),
            request.Query["steamid"].FirstOrDefault(),
            ReadBodyString(body, "steamId"),
        };
        foreach (var source in sources)
            if (source?.Trim() is { Length: > 0 } claim) yield return claim;
    }

    /// <summary>The Steam ID the caller names as the requested player (trimmed; first supplied wins). The proxy host's x-sbox-steam-id is not a player claim.</summary>
    internal static string RequestedSteamId(HttpRequest request, JsonElement body) => ClaimValues(request, body, includeHostHeader: false).FirstOrDefault() ?? "";

    /// <summary>
    /// True when every Steam ID the caller names as the requested player equals <paramref name="verifiedSteamId"/>.
    /// Used by the auth-session routes after the s&amp;box check succeeded. In a delegated request that check proved the
    /// host token against <see cref="ClaimedSteamId"/>, so that value is the host's verified identity, not a player
    /// claim: legacy hosts send it as x-steam-id, newer ones as x-sbox-steam-id.
    /// </summary>
    internal static bool RequestedClaimsMatch(HttpRequest request, JsonElement body, string verifiedSteamId)
    {
        var hostSteamId = string.IsNullOrEmpty(request.Headers["x-on-behalf-of"].FirstOrDefault()) ? null : ClaimedSteamId(request, body);
        return ClaimValues(request, body, includeHostHeader: false).All(claim =>
            string.Equals(claim, verifiedSteamId, StringComparison.Ordinal) || string.Equals(claim, hostSteamId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves the player for a public-key request. When <paramref name="required"/>
    /// is false and no auth-session token is presented, the claimed identity passes
    /// through unverified (auth-disabled projects). Otherwise the identity must be
    /// proven by a valid auth session and/or s&amp;box token, and every supplied
    /// Steam ID claim must match it. <paramref name="body"/> may be default when the
    /// request body is not an auth envelope (e.g. a stored document).
    /// </summary>
    internal static async Task<PlayerIdentityResult> ResolveAsync(
        HttpContext context, JsonElement body, string projectId, string endpointSlug, string apiKey,
        long ownerUserId, bool required, bool sessionsEnabled, string claimedSteamId)
    {
        var request = context.Request;
        var sessionToken = ReadSessionToken(request, body);
        var token = ReadHostToken(request, body);
        var clientSteamId = request.Headers["x-on-behalf-of"].FirstOrDefault();
        var clientToken = request.Headers["x-on-behalf-of-token"].FirstOrDefault();
        var proxySignature = request.Headers["x-proxy-signature"].FirstOrDefault();
        var hasSboxCredentials = !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(clientSteamId)
            || !string.IsNullOrEmpty(clientToken) || !string.IsNullOrEmpty(proxySignature);
        // Auth-disabled projects match legacy server: s&box tokens are ignored and the
        // claimed identity (or the proxied player, already plausibility-checked
        // by the caller) passes through. Presented auth-session tokens are still
        // validated (they are our own issuance). Required projects fall through
        // to strict verification below.
        if (!required && sessionToken is null)
            return PlayerIdentityResult.Verified(FirstNonEmpty(clientSteamId, claimedSteamId) ?? "anonymous");

        string? verifiedSteamId = null;
        if (sessionToken is not null)
        {
            if (!sessionsEnabled)
                return PlayerIdentityResult.Reject("AUTH_SESSION_DISABLED", "Auth sessions are not enabled for this project.", StatusCodes.Status403Forbidden);
            var sessions = context.RequestServices.GetRequiredService<INetworkStorageAuthSessionService>();
            var valid = await sessions.ValidateAsync(projectId, sessionToken, ct: context.RequestAborted);
            if (!valid.Ok)
                return PlayerIdentityResult.Reject(valid.Code!, valid.Message!);
            if (valid.Session!.UserId != ownerUserId || string.IsNullOrEmpty(valid.Session.SteamId))
                return PlayerIdentityResult.Reject("AUTH_SESSION_INVALID", "Auth session does not belong to this project owner.");
            verifiedSteamId = valid.Session.SteamId;
        }

        if (hasSboxCredentials || sessionToken is null)
        {
            var verifier = context.RequestServices.GetRequiredService<ISboxAuthVerifier>();
            var check = new SboxAuthCheck(token ?? "", claimedSteamId, clientSteamId, clientToken,
                proxySignature, apiKey, projectId, endpointSlug, ClientAddress.Resolve(context));
            var result = await verifier.CheckAsync(check, context.RequestAborted);
            if (!result.Ok || string.IsNullOrEmpty(result.SteamId))
                return PlayerIdentityResult.Reject("SBOX_AUTH_FAILED", result.Error ?? "Player identity could not be verified.");

            if (verifiedSteamId is not null && !string.Equals(verifiedSteamId, result.SteamId, StringComparison.Ordinal))
                return PlayerIdentityResult.Reject("AUTH_SESSION_STEAMID_MISMATCH", "Auth session and s&box token identify different players.", StatusCodes.Status403Forbidden);
            verifiedSteamId = result.SteamId;
        }

        // Check every supported claim, not just the highest-precedence one. Proxy
        // requests carry the verified host in these fields and the client separately.
        var expectedClaim = string.IsNullOrEmpty(clientSteamId) ? verifiedSteamId! : claimedSteamId;
        if (!SuppliedClaimsMatch(request, body, expectedClaim))
            return PlayerIdentityResult.Reject("AUTH_SESSION_STEAMID_MISMATCH", "Supplied Steam ID does not match the verified identity.", StatusCodes.Status403Forbidden);

        return PlayerIdentityResult.Verified(verifiedSteamId!);
    }

    /// <summary>The auth-session token presented with the request (header, bearer, query, or body), or null.</summary>
    internal static string? ReadSessionToken(HttpRequest request, JsonElement body)
    {
        var authorization = request.Headers.Authorization.FirstOrDefault() ?? "";
        return FirstNonEmpty(
            request.Headers["x-auth-session"].FirstOrDefault(),
            request.Headers["x-auth-session-token"].FirstOrDefault(),
            authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null,
            request.Query["authSessionToken"].FirstOrDefault(),
            request.Query["sessionToken"].FirstOrDefault(),
            ReadBodyString(body, "authSessionToken"), ReadBodyString(body, "sessionToken"));
    }

    internal static string? ReadBodyString(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when name == "steamId" => value.GetRawText(),
            _ => null
        };
    }

    internal static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value)) return value;
        return null;
    }
}
