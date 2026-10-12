using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Phase 4 (game-version-policy, 9.2): the evaluator maps (project rules,
/// revision index, client revision, now) to the player action. Covers none,
/// notify, rejoin, block_writes, block_all, minimum revision, ended and
/// extended grace, and unknown revision.
/// </summary>
public sealed class VersionPolicyEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Publish42 = Now.AddMinutes(-10);

    private static WorkspaceProject Policy(
        string mode = "allow_continue",
        int? grace = 60,
        string? inGrace = null,
        string after = "block_writes",
        long? minimum = null,
        string? message = null) => new(
            "proj", "P", null, true, Now, Now, null,
            RevisionEnforcementMode: mode,
            RevisionGracePeriodMinutes: grace,
            RevisionPostGraceAction: after,
            RevisionNotifyMessage: message,
            RevisionInGraceAction: inGrace,
            RevisionMinimumRevisionId: minimum);

    private static RevisionIndex Index(params RevisionIndexEntry[] entries) =>
        new(entries, LiveRevisionId: 42, LivePublishedAtUtc: Publish42);

    private static RevisionIndexEntry Entry(long id, DateTimeOffset? graceStart = null, bool ended = false, DateTimeOffset? extended = null) =>
        new(id, graceStart ?? Publish42.AddMinutes(-100), graceStart ?? Publish42, ended, extended);

    [Fact]
    public void NoPolicy_CurrentClient_ReturnsNone()
    {
        var bare = new WorkspaceProject("p", "P", null, true, Now, Now, null);
        var decision = VersionPolicyEvaluator.Evaluate(bare, Index(), 42, Now);
        Assert.Equal("none", decision.Action);
        Assert.False(decision.IsOutdated);
    }

    [Fact]
    public void UnknownRevision_FallsBackLive()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(), Index(), null, Now);
        Assert.Equal("none", decision.Action);
        Assert.False(decision.IsOutdated);
    }

    [Fact]
    public void LegacyAllowContinue_MigratesToNotify_InGrace()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(mode: "allow_continue"), Index(Entry(41)), 41, Now);
        Assert.True(decision.IsOutdated);
        Assert.False(decision.GraceExpired);
        Assert.Equal("notify", decision.Action);
        Assert.Equal(Publish42.AddMinutes(60), decision.GraceEndsAt);
    }

    [Fact]
    public void LegacyForceUpgrade_MigratesToRejoin_InGrace()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(mode: "force_upgrade"), Index(Entry(41)), 41, Now);
        Assert.Equal("rejoin", decision.Action);
        Assert.False(decision.GraceExpired);
    }

    [Fact]
    public void ExplicitContinue_SurfacesAsNone()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(inGrace: "continue"), Index(Entry(41)), 41, Now);
        Assert.Equal("none", decision.Action);
        Assert.True(decision.IsOutdated);
        Assert.False(decision.GraceExpired);
    }

    [Fact]
    public void PastGrace_BlockWrites()
    {
        var old = new RevisionIndexEntry(41, Publish42.AddDays(-2), Publish42.AddDays(-2));
        var decision = VersionPolicyEvaluator.Evaluate(Policy(grace: 60, after: "block_writes"), Index(old), 41, Now);
        Assert.True(decision.GraceExpired);
        Assert.Equal("block_writes", decision.Action);
    }

    [Fact]
    public void PastGrace_BlockAll()
    {
        var old = new RevisionIndexEntry(41, Publish42.AddDays(-2), Publish42.AddDays(-2));
        var decision = VersionPolicyEvaluator.Evaluate(Policy(grace: 60, after: "block_all"), Index(old), 41, Now);
        Assert.Equal("block_all", decision.Action);
    }

    [Fact]
    public void ZeroGrace_ExpiresImmediately()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(grace: 0), Index(Entry(41)), 41, Now);
        Assert.True(decision.GraceExpired);
        Assert.Equal("block_writes", decision.Action);
    }

    [Fact]
    public void MinimumRevision_BelowMinimum_IsPastGrace()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(minimum: 42), Index(Entry(41)), 41, Now);
        Assert.True(decision.GraceExpired);
        Assert.Equal("block_writes", decision.Action);
    }

    [Fact]
    public void MinimumRevision_AtMinimum_StaysInGrace()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(minimum: 41), Index(Entry(41)), 41, Now);
        Assert.False(decision.GraceExpired);
        Assert.Equal("notify", decision.Action);
    }

    [Fact]
    public void EndedGrace_ExpiresImmediately()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(), Index(Entry(41, ended: true)), 41, Now);
        Assert.True(decision.GraceExpired);
    }

    [Fact]
    public void ExtendedGrace_StaysInGraceUntilExtension()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(grace: 1), Index(Entry(41, extended: Now.AddHours(2))), 41, Now);
        Assert.False(decision.GraceExpired);
        Assert.Equal(Now.AddHours(2), decision.GraceEndsAt);
    }

    [Fact]
    public void LapsedExtension_Expires()
    {
        var old = new RevisionIndexEntry(41, Publish42.AddDays(-2), Publish42.AddDays(-2), false, Now.AddHours(-1));
        var decision = VersionPolicyEvaluator.Evaluate(Policy(grace: 60), Index(old), 41, Now);
        Assert.True(decision.GraceExpired);
    }

    [Fact]
    public void OwnerMessage_SurfacesWhenOutdated()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(message: "Update please"), Index(Entry(41)), 41, Now);
        Assert.Equal("Update please", decision.Message);
        var current = VersionPolicyEvaluator.Evaluate(Policy(message: "Update please"), Index(Entry(41)), 42, Now);
        Assert.Null(current.Message);
    }

    [Fact]
    public void CurrentClient_ReturnsNone()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(), Index(Entry(41)), 42, Now);
        Assert.Equal("none", decision.Action);
        Assert.False(decision.IsOutdated);
    }

    [Fact]
    public void FutureRevision_IsNotOutdated()
    {
        var decision = VersionPolicyEvaluator.Evaluate(Policy(), Index(Entry(41)), 43, Now);
        Assert.False(decision.IsOutdated);
        Assert.Equal("none", decision.Action);
    }

    [Fact]
    public void Retention_Default3_Clamped0To20()
    {
        Assert.Equal(3, VersionPolicyEvaluator.RetentionFor(new WorkspaceProject("p", "P", null, true, Now, Now, null)));
        Assert.Equal(0, VersionPolicyEvaluator.RetentionFor(new WorkspaceProject("p", "P", null, true, Now, Now, null, RevisionRetentionCount: -5)));
        Assert.Equal(20, VersionPolicyEvaluator.RetentionFor(new WorkspaceProject("p", "P", null, true, Now, Now, null, RevisionRetentionCount: 99)));
    }
}
