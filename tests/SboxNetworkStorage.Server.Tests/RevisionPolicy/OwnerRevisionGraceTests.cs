using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using Xunit;

using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Phase 4 (game-version-policy, 8.3): the Versions page lists retained
/// revisions with support state, and the owner can end or extend grace with
/// a confirmation and an audit entry.
/// </summary>
public sealed class OwnerRevisionGraceTests
{
    [SkippableFact]
    public async Task EndGrace_MarksRevisionExpired_WithAudit()
    {
        using var factory = new SqliteHostFactory();
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        using var setup = await CreateTwoRevisionSetupAsync(factory);
        var versionsUrl = $"{setup.Url}/versions";
        var before = await setup.Owner.GetStringAsync(versionsUrl);
        Assert.Contains("41", before, StringComparison.Ordinal);
        Assert.Contains("In grace", before, StringComparison.Ordinal);

        using var ended = await PostFormAsync(setup.Owner, versionsUrl, versionsUrl + "/grace/end",
            ("revisionId", "41"),
            ("confirmTitle", "End grace period"),
            ("confirmMessage", "End grace?"),
            ("confirmed", "true"));
        Assert.Equal(HttpStatusCode.Redirect, ended.StatusCode);

        var after = await setup.Owner.GetStringAsync(versionsUrl);
        Assert.Contains("Expired", after, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var audits = await scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Storage.INetworkStorageStore>()
            .ListAuditLogsAsync(setup.Project.ProjectId, 50, CancellationToken.None);
        Assert.Contains(audits, row => row.GetProperty("action").GetString() == "revision.grace.end");
    }

    [SkippableFact]
    public async Task ExtendGrace_KeepsRevisionInGrace()
    {
        using var factory = new SqliteHostFactory();
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        using var setup = await CreateTwoRevisionSetupAsync(factory);
        var versionsUrl = $"{setup.Url}/versions";
        using var extended = await PostFormAsync(setup.Owner, versionsUrl, versionsUrl + "/grace/extend",
            ("revisionId", "41"),
            ("extendMinutes", "120"),
            ("confirmTitle", "Extend grace period"),
            ("confirmMessage", "Extend grace?"),
            ("confirmed", "true"));
        Assert.Equal(HttpStatusCode.Redirect, extended.StatusCode);
        var after = await setup.Owner.GetStringAsync(versionsUrl);
        Assert.Contains("In grace", after, StringComparison.Ordinal);
    }

    private static async Task<Setup> CreateTwoRevisionSetupAsync(SqliteHostFactory factory)
    {
        await CreateOwnerAsync(factory);
        var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Grace game");
        var url = $"/dashboard/projects/{project.ProjectId}";
        using var client = factory.CreateClient();
        foreach (var revision in new[] { 41L, 42L })
        {
            using var sync = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync");
            sync.Headers.Add("x-api-key", project.SecretKey);
            sync.Content = JsonContent.Create(new { packageIdent = "test.game", currentRevisionId = revision });
            using var response = await client.SendAsync(sync);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            1, project.ProjectId, "revisions", new Dictionary<string, string>
            {
                ["revisionEnforcementMode"] = "allow_continue",
                ["revisionGracePeriodMinutes"] = "60",
                ["revisionPostGraceAction"] = "block_writes",
            }, CancellationToken.None);
        return new Setup(owner, project, url);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string action, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(pageUrl);
        return await client.PostAsync(action, Form([.. fields, ("__RequestVerificationToken", Csrf(page))]));
    }

    private sealed record Setup(HttpClient Owner, SelfHostProject Project, string Url) : IDisposable
    {
        public void Dispose()
        {
            Owner.Dispose();
        }
    }
}
