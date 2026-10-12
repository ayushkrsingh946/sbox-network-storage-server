using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Pure evaluation of the game version policy (D9, game-version-policy spec).
/// Maps (project rules, revision index, client revision, now) to the player
/// action, owner message and grace bounds. No I/O, no clock reads: the caller
/// supplies <paramref name="nowUtc"/>. Never throws for malformed input;
/// unknown revisions fall back to live behavior (action "none").
/// </summary>
public static class VersionPolicyEvaluator
{
    public const int DefaultRetention = 3;
    public const int MinRetention = 0;
    public const int MaxRetention = 20;
    public const int MaxGraceMinutes = 10080;

    public sealed record Evaluation(
        // Player action for revision-init: "none", "notify", "rejoin",
        // "block_writes" or "block_all". In-grace "continue" surfaces as "none".
        string Action,
        // Owner notify message when set and the client is outdated; else null.
        string? Message,
        // Grace end (UTC) when a grace window applies; else null.
        DateTimeOffset? GraceEndsAt,
        int? GraceRemainingMinutes,
        bool GraceExpired,
        bool IsOutdated,
        long? CurrentRevision);

    /// <summary>
    /// True when the owner explicitly saved version rules. Only then may the
    /// server emit <c>_revisionStatus</c> or enforce anything: untouched
    /// projects keep byte-identical legacy bodies (assumption 8).
    /// </summary>
    public static bool HasExplicitPolicy(WorkspaceProject project) =>
        !string.IsNullOrWhiteSpace(project.RevisionEnforcementMode)
        || !string.IsNullOrWhiteSpace(project.RevisionInGraceAction)
        || project.RevisionMinimumRevisionId.HasValue
        || project.RevisionServerEnforcementEnabled == true
        || project.RevisionGracePeriodMinutes.HasValue
        || project.RevisionRetentionCount.HasValue;

    /// <summary>D9 migration: legacy mode → in-grace action.</summary>
    public static string InGraceActionFor(WorkspaceProject project)
    {
        if (string.Equals(project.RevisionInGraceAction, "continue", StringComparison.OrdinalIgnoreCase))
            return "continue";
        if (string.Equals(project.RevisionInGraceAction, "rejoin", StringComparison.OrdinalIgnoreCase))
            return "rejoin";
        if (string.Equals(project.RevisionInGraceAction, "notify", StringComparison.OrdinalIgnoreCase))
            return "notify";
        // Legacy: allow_continue → notify; force_upgrade → rejoin.
        return string.Equals(project.RevisionEnforcementMode, "force_upgrade", StringComparison.OrdinalIgnoreCase)
            ? "rejoin"
            : "notify";
    }

    public static string AfterGraceActionFor(WorkspaceProject project) =>
        string.Equals(project.RevisionPostGraceAction, "block_all", StringComparison.OrdinalIgnoreCase)
            ? "block_all"
            : "block_writes";

    public static int RetentionFor(WorkspaceProject? project)
    {
        var raw = project?.RevisionRetentionCount ?? DefaultRetention;
        return Math.Clamp(raw, MinRetention, MaxRetention);
    }

    public static int? GraceMinutesFor(WorkspaceProject project)
    {
        if (!project.RevisionGracePeriodMinutes.HasValue) return null;
        return Math.Clamp(project.RevisionGracePeriodMinutes.Value, 0, MaxGraceMinutes);
    }

    public static Evaluation Evaluate(
        WorkspaceProject project,
        RevisionIndex index,
        long? clientRevision,
        DateTimeOffset nowUtc,
        long? liveRevisionId = null)
    {
        var live = liveRevisionId ?? index.LiveRevisionId;
        if (!HasExplicitPolicy(project) || !clientRevision.HasValue || !live.HasValue)
            return new Evaluation("none", null, null, null, false, IsOutdated: clientRevision.HasValue && live.HasValue && clientRevision.Value < live.Value, live);

        var client = clientRevision.Value;
        if (client >= live.Value)
            return new Evaluation("none", null, null, null, false, false, live);

        var minimum = project.RevisionMinimumRevisionId;
        var belowMinimum = minimum.HasValue && client < minimum.Value;

        var entry = index.Find(client);
        // Grace starts when a newer revision went live (the actual revision
        // change), never at first-ever publish: prefer the index grace start
        // recorded at publish, else the live publish time.
        var graceStart = entry?.GraceStartUtc ?? index.LivePublishedAtUtc;
        var endedEarly = entry?.GraceEnded == true;
        var extendedUntil = entry?.GraceExtendedUntilUtc;

        string? ownerMessage = string.IsNullOrWhiteSpace(project.RevisionNotifyMessage)
            ? null
            : project.RevisionNotifyMessage.Trim();

        var graceMinutes = GraceMinutesFor(project);
        DateTimeOffset? graceEndsAt = null;
        if (extendedUntil.HasValue && extendedUntil.Value > nowUtc)
        {
            graceEndsAt = extendedUntil.Value;
        }
        else if (graceMinutes.HasValue && graceStart.HasValue)
        {
            // 0 means no grace window: the window ends at the grace start, so
            // an outdated client is past grace immediately. Null (unset)
            // leaves graceEndsAt empty and the in-grace action applies.
            graceEndsAt = graceStart.Value.AddMinutes(graceMinutes.Value);
        }

        bool inGrace;
        if (belowMinimum || endedEarly)
        {
            inGrace = false;
        }
        else if (extendedUntil.HasValue)
        {
            inGrace = extendedUntil.Value > nowUtc;
            if (!inGrace) graceEndsAt = null;
        }
        else if (graceEndsAt.HasValue)
        {
            inGrace = nowUtc < graceEndsAt.Value;
        }
        else
        {
            // No grace anchor or no grace period configured: the in-grace
            // action applies (never strand players on unprovable expiry).
            inGrace = true;
        }

        if (inGrace)
        {
            var inGraceAction = InGraceActionFor(project);
            var action = inGraceAction == "continue" ? "none" : inGraceAction;
            int? remaining = graceEndsAt.HasValue
                ? Math.Max(0, (int)Math.Ceiling((graceEndsAt.Value - nowUtc).TotalMinutes))
                : null;
            return new Evaluation(action, ownerMessage, graceEndsAt, remaining, false, true, live);
        }

        var after = AfterGraceActionFor(project);
        return new Evaluation(after, ownerMessage, graceEndsAt, 0, true, true, live);
    }
}
