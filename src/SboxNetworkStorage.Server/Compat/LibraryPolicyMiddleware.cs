using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Middleware;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// Opt-in library enforcement. A request is refused with HTTP 426 only when (a) the project's owner set an
/// explicit library minimum and the client reports a parsed version below it, or (b) the client reports a
/// parsed version in a <c>blocked</c> range of the embedded matrix (<see cref="LibraryCompatProvider.Embedded"/>).
/// The release-feed overlay never refuses anything: it stays report-only in the status headers.
/// Requests with no, legacy or unparseable library version are never refused here. Version metadata is
/// self-reported, never authentication: missing or unauthorized keys keep their original 401/403 contract,
/// and management diagnostics and recovery (Pull, Test Live, revision-init) stay reachable.
/// </summary>
public sealed class LibraryPolicyMiddleware(RequestDelegate next, LibraryCompatProvider compat, LibraryPolicyCache policies)
{
    public async Task InvokeAsync(HttpContext context, IStorageApiKeyResolver keys, INetworkStorageProjectService projects)
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
        var identity = ClientIdentityContext.Get(context);
        var verdict = compat.Embedded.Evaluate(identity);
        var block = verdict.Status is LibraryStatus.Blocked ? verdict : null;
        var minimum = await policies.MinimumAsync(projects, projectId, context.RequestAborted);
        var unsupported = ClientVersionsQuery.BelowMinimum(identity.LibraryVersion, minimum);
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
        context.Items[typeof(LibraryPolicyMiddleware)] = block is null ? "unsupported" : "blocked";
        var message = block is null
            ? $"This project's owner requires library {minimum ?? compat.Embedded.Minimum} or newer. Update the library and republish the game."
            : string.Join(" ", new[] { block.Reason, block.Advisory is null ? null : $"Advisory: {block.Advisory}.",
                "Update the library and republish the game." }.Where(part => !string.IsNullOrWhiteSpace(part)));
        await context.Response.WriteAsJsonAsync(new { ok = false, error = new { code, message } }, context.RequestAborted);
    }
}
