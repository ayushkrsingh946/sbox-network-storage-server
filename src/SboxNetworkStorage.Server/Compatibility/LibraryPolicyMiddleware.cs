using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Middleware;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Compatibility;

/// <summary>Version metadata is not security attestation. Enforcement is compatibility policy, never authentication.</summary>
public sealed class LibraryPolicyMiddleware(RequestDelegate next, LibraryCompatibility compatibility, LibraryPolicyCache policies)
{
    public async Task InvokeAsync(HttpContext context, IStorageApiKeyResolver keys, INetworkStorageStore store)
    {
        if (HttpMethods.IsOptions(context.Request.Method) || context.GetRouteValue("projectId") is not string projectId)
        {
            await next(context);
            return;
        }
        var path = context.Request.Path.Value ?? "";
        var management = path.StartsWith("/v3/manage/", StringComparison.Ordinal);
        var push = management && (HttpMethods.IsPut(context.Request.Method) && path.EndsWith("/sync", StringComparison.Ordinal)
            || HttpMethods.IsPost(context.Request.Method) && (path.EndsWith("/sync/preflight", StringComparison.Ordinal)
                || path.EndsWith("/package-sync", StringComparison.Ordinal)));
        // Keep management diagnostics/recovery (Pull, Test Live, revision-init) available even when runtime policy blocks.
        if (management && !push) { await next(context); return; }
        var apiKey = context.Request.Headers["x-api-key"].FirstOrDefault()
            ?? context.Request.Query["apiKey"].FirstOrDefault() ?? context.Request.Headers["x-public-key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey) || await keys.ResolveApiKeyAsync(apiKey, projectId, context.RequestAborted) is not { Enabled: true } auth)
        {
            await next(context); // Existing route keeps its own auth/error contract.
            return;
        }
        var identity = ClientIdentity.Get(context);
        var block = compatibility.BlockFor(identity.LibraryVersion);
        var minimum = await policies.MinimumAsync(store, projectId, context.RequestAborted);
        var unsupported = ClientVersionsQuery.BelowMinimum(identity.LibraryVersion, minimum)
            || push && compatibility.StatusFor(identity.LibraryVersion) == "unsupported";
        if (block is null && !unsupported) { await next(context); return; }
        if (push && (string.IsNullOrWhiteSpace(context.Request.Headers["x-api-key"])
            || !string.Equals(auth.KeyType, "secret", StringComparison.OrdinalIgnoreCase) || !PackageSyncHandler.HasWritePermissions(auth)))
        {
            await next(context); // Do not replace the original management 401/403 with a version rejection.
            return;
        }
        NetworkStorageUsageContext.SetAuthenticated(context, projectId);
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        var code = block is null ? "LIBRARY_UNSUPPORTED" : "LIBRARY_BLOCKED";
        context.Items[typeof(LibraryCompatibility)] = block is null ? "unsupported" : "blocked";
        var message = block is null ? $"This project's owner requires library {minimum ?? compatibility.Matrix.Minimum} or newer. Update the library and republish the game."
            : $"{block.Reason} Advisory: {block.Advisory}. Update the library and republish the game.";
        await context.Response.WriteAsJsonAsync(new { ok = false, error = new { code, message } }, context.RequestAborted);
    }
}
