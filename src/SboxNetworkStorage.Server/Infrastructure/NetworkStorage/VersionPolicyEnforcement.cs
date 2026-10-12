using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Compat;

namespace SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

 /// <summary>
 /// data-plane entry point after its own authentication: only when the
/// project's server enforcement switch is explicitly on, the request carries
/// a known revision, and the caller used a non-secret key. Secret-key
/// callers (dedicated servers) are always exempt. Fail-open: any lookup
/// failure allows the request (today's behavior).
/// <c>block_writes</c> rejects only mutating requests (non-GET/HEAD/OPTIONS);
/// <c>block_all</c> rejects everything from the expired revision.
/// </summary>
public static class VersionPolicyEnforcement
{
    public sealed record Denial(string Action, string Message, long LatestRevisionId, Dictionary<string, object?> RevisionStatus);

    public static bool IsMutating(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));

    public static async Task<Denial?> CheckAsync(
        WorkspaceProject project,
        long ownerUserId,
        string projectId,
        long? clientRevision,
        bool isSecretCaller,
        bool isMutating,
        IWorkspaceStore workspace,
        RevisionSnapshotCache snapshots,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        if (project.RevisionServerEnforcementEnabled != true) return null;
        if (isSecretCaller) return null;
        if (!clientRevision.HasValue) return null;
        if (!VersionPolicyEvaluator.HasExplicitPolicy(project)) return null;

        try
        {
            var index = await RevisionSnapshots.ReadIndexAsync(workspace, ownerUserId, projectId, ct);
            var live = index.LiveRevisionId;
            if (!live.HasValue) return null;
            if (clientRevision.Value >= live.Value) return null;

            // Unknown revisions fall back to live: never block what the
            // server cannot place.
            if (index.Find(clientRevision.Value) is null)
            {
                var known = await snapshots.GetAsync(workspace, ownerUserId, projectId, clientRevision.Value, ct);
                if (known is null) return null;
            }

            var decision = VersionPolicyEvaluator.Evaluate(project, index, clientRevision, nowUtc, live);
            if (!decision.IsOutdated || !decision.GraceExpired) return null;

            if (decision.Action == "block_writes" && !isMutating) return null;

            var message = decision.Message ?? $"Revision {clientRevision} is no longer supported. Latest is {live}.";
            var status = RevisionStatusInjector.Build(project, decision, clientRevision)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            return new Denial(decision.Action, message, live.Value, status);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static object DenialBody(Denial denial) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["ok"] = false,
        ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "REVISION_BLOCKED",
            ["message"] = denial.Message,
            ["action"] = denial.Action,
            ["latestRevisionId"] = denial.LatestRevisionId,
        },
        ["_revisionStatus"] = denial.RevisionStatus,
    };

    /// <summary>
    /// Shared data-plane entry point: resolves the project best-effort and
    /// denies expired revisions per <see cref="CheckAsync"/>. Returns true
    /// when the denial was written (the caller returns immediately).
    /// Fail-open: anything unresolvable allows the request.
    /// </summary>
    public static async Task<bool> EnforceAsync(HttpContext context, string projectId, StorageApiKeyAuthResult auth, bool isMutating)
    {
        try
        {
            if (string.Equals(auth.KeyType, "secret", StringComparison.OrdinalIgnoreCase)) return false;
            // Requests without a revision (legacy clients, older libraries) are never blocked, so skip the project lookup entirely.
            var clientRevision = ClientIdentityContext.Get(context).RevisionId;
            if (!clientRevision.HasValue) return false;
            var projectService = context.RequestServices.GetService<INetworkStorageProjectService>();
            var workspace = context.RequestServices.GetService<IWorkspaceStore>();
            var memoryCache = context.RequestServices.GetService<IMemoryCache>();
            if (projectService is null || workspace is null || memoryCache is null) return false;
            var access = await projectService.ResolveProjectAccessAsync(auth.UserId, projectId, context.RequestAborted);
            if (access is null || access.Project.RevisionServerEnforcementEnabled != true) return false;
            if (!VersionPolicyEvaluator.HasExplicitPolicy(access.Project)) return false;
            var denial = await CheckAsync(
                access.Project, auth.UserId, projectId, clientRevision,
                isSecretCaller: false, isMutating,
                workspace, new RevisionSnapshotCache(memoryCache), DateTimeOffset.UtcNow, context.RequestAborted);
            if (denial is null) return false;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(DenialBody(denial), context.RequestAborted);
            return true;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return true; }
        catch { return false; }
    }
}
