using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

 namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Phase 4 (game-version-policy, 9.3): revision-init keeps its current fields
/// and additively returns <c>action</c>, the owner's <c>message</c>,
/// <c>graceEndsAt</c> and <c>minimumRevisionId</c>. Covers the
/// outdated-in-grace and current cases; stays read-only.
/// </summary>
public abstract class RevisionInitPolicyHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;

    protected RevisionInitPolicyHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task OutdatedClientInGrace_GetsRejoinActionMessageAndGraceEndsAt()
    {
        using var setup = await CreateWithPolicyAsync(graceMinutes: "60", mode: "force_upgrade", message: "New update is out, please rejoin");
        using var response = await PostInitAsync(setup, clientRevision: 41);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("revisionOutdated").GetBoolean());
        Assert.Equal("rejoin", body.RootElement.GetProperty("action").GetString());
        Assert.Equal("New update is out, please rejoin", body.RootElement.GetProperty("message").GetString());
        var graceEndsAt = body.RootElement.GetProperty("graceEndsAt").GetInt64();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(graceEndsAt, now + 50 * 60, now + 70 * 60);
    }

    [SkippableFact]
    public async Task CurrentClient_GetsNoneActionAndNoGrace()
    {
        using var setup = await CreateWithPolicyAsync(graceMinutes: "60", mode: "force_upgrade", message: "New update is out, please rejoin");
        using var response = await PostInitAsync(setup, clientRevision: 42);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("revisionOutdated").GetBoolean());
        Assert.Equal("none", body.RootElement.GetProperty("action").GetString());
        Assert.Equal("Revision is current.", body.RootElement.GetProperty("message").GetString());
        Assert.False(body.RootElement.TryGetProperty("graceEndsAt", out _));
    }

    [SkippableFact]
    public async Task MinimumRevision_IsReportedWhenSet()
    {
        using var setup = await CreateWithPolicyAsync(graceMinutes: "60", mode: "allow_continue", message: null, minimum: "41");
        using var response = await PostInitAsync(setup, clientRevision: 41);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(41, body.RootElement.GetProperty("minimumRevisionId").GetInt64());
    }

    [SkippableFact]
    public async Task NoPolicy_LegacyBodyHasNoAction()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var sync = await SyncRevisionAsync(client, project.ProjectId, project.SecretKey, 42);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        using var init = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}");
        init.Content = JsonContent.Create(new { projectId = project.ProjectId, revisionId = 41 });
        using var response = await client.SendAsync(init);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("revisionOutdated").GetBoolean());
        // Omitted when not applicable: legacy bodies are unchanged.
        Assert.False(body.RootElement.TryGetProperty("action", out _));
        Assert.False(body.RootElement.TryGetProperty("graceEndsAt", out _));
        Assert.False(body.RootElement.TryGetProperty("minimumRevisionId", out _));
    }

    private async Task<Setup> CreateWithPolicyAsync(string graceMinutes, string mode, string? message, string? minimum = null)
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var first = await SyncRevisionAsync(client, project.ProjectId, project.SecretKey, 41);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await SyncRevisionAsync(client, project.ProjectId, project.SecretKey, 42);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var form = new Dictionary<string, string>
        {
            ["revisionEnforcementMode"] = mode,
            ["revisionGracePeriodMinutes"] = graceMinutes,
            ["revisionPostGraceAction"] = "block_writes",
        };
        if (message is not null) form["revisionNotifyMessage"] = message;
        if (minimum is not null) form["revisionMinimumRevisionId"] = minimum;
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            1, project.ProjectId, "revisions", form, CancellationToken.None);
        return new Setup(factory.CreateClient(), project);
    }

    private static async Task<HttpResponseMessage> SyncRevisionAsync(HttpClient client, string projectId, string secretKey, long revision)
    {
        using var sync = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{projectId}/package-sync");
        sync.Headers.Add("x-api-key", secretKey);
        sync.Content = JsonContent.Create(new { packageIdent = "test.game", currentRevisionId = revision });
        return await client.SendAsync(sync);
    }

    private static async Task<HttpResponseMessage> PostInitAsync(Setup setup, long clientRevision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/manage/{setup.Project.ProjectId}/revision-init?apiKey={setup.Project.PublicKey}");
        request.Headers.Add("x-public-key", setup.Project.PublicKey);
        request.Headers.Add("x-ns-revision-id", clientRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new
        {
            projectId = setup.Project.ProjectId,
            clientType = "game",
            networkStorageVersion = "1.0",
            revisionId = clientRevision,
        });
        return await setup.Client.SendAsync(request);
    }

    private sealed record Setup(HttpClient Client, SelfHostProject Project) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
        }
    }
}

public sealed class SqliteRevisionInitPolicyHttpTests(SqliteHostFactory factory)
    : RevisionInitPolicyHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresRevisionInitPolicyHttpTests(PostgresHostFactory factory)
    : RevisionInitPolicyHttpTests<PostgresHostFactory>(factory);
