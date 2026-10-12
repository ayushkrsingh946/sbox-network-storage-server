using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Phase 4 (game-version-policy, 8.2): a data-plane request carrying
/// <c>x-ns-revision-id</c> for a retained revision inside its support window
/// runs that revision's endpoint and values. Requests without a revision, or
/// with an unknown one, use live.
/// </summary>
public abstract class RevisionPinningHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000001";
    private const string DefinitionV1 = """
        {"exposure":"public","steps":[{"id":"save","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"set","path":"marker","value":"executed"}]}],"response":{"status":200,"body":{"ok":true,"version":"v1"}}}
        """;
    private const string DefinitionV2 = """
        {"exposure":"public","steps":[{"id":"save","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"set","path":"marker","value":"executed"}]}],"response":{"status":200,"body":{"ok":true,"version":"v2"}}}
        """;
    private const string ValuesV1 = """{"items":[{"id":"tuning","type":"group","entries":{"xp_per_level":500}}]}""";
    private const string ValuesV2 = """{"items":[{"id":"tuning","type":"group","entries":{"xp_per_level":1000}}]}""";

    private readonly TFactory factory;

    protected RevisionPinningHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task OldRevision_RunsOldEndpointAndValues_WithStatus()
    {
        using var setup = await CreateTwoRevisionSetupAsync();
        using var response = await PostEndpointAsync(setup, revision: "41");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("v1", body.RootElement.GetProperty("version").GetString());
        Assert.True(body.RootElement.TryGetProperty("_revisionStatus", out var status));
        Assert.True(status.GetProperty("isOutdatedRevision").GetBoolean());
        Assert.Equal(42, status.GetProperty("currentRevision").GetInt64());

        using var values = await GetValuesAsync(setup, revision: "41");
        Assert.Equal(HttpStatusCode.OK, values.StatusCode);
        var text = await values.Content.ReadAsStringAsync();
        Assert.Contains("500", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1000", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task NoHeader_RunsLive()
    {
        using var setup = await CreateTwoRevisionSetupAsync();
        using var response = await PostEndpointAsync(setup, revision: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("v2", body.RootElement.GetProperty("version").GetString());

        using var values = await GetValuesAsync(setup, revision: null);
        var text = await values.Content.ReadAsStringAsync();
        Assert.Contains("1000", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task UnknownRevision_FallsBackLive()
    {
        using var setup = await CreateTwoRevisionSetupAsync();
        using var response = await PostEndpointAsync(setup, revision: "9999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("v2", body.RootElement.GetProperty("version").GetString());
    }

    private async Task<Setup> CreateTwoRevisionSetupAsync()
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertCollectionAsync(project.ProjectId, "players", "players", "private",
            JsonDocument.Parse("{\"collectionType\":\"player\"}").RootElement, 1, CancellationToken.None);
        await store.UpsertEndpointAsync(project.ProjectId, "ping", "ping", "POST", true,
            JsonDocument.Parse(DefinitionV1).RootElement, null, 1, CancellationToken.None);
        await store.UpsertGameValuesAsync(project.ProjectId,
            JsonDocument.Parse(ValuesV1).RootElement, null, 1, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            1, project.ProjectId, "revisions", new Dictionary<string, string>
            {
                ["revisionEnforcementMode"] = "allow_continue",
                ["revisionGracePeriodMinutes"] = "60",
                ["revisionPostGraceAction"] = "block_writes",
            }, CancellationToken.None);

        using var client = factory.CreateClient();
        using var first = await SyncRevisionAsync(client, project, 41);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await using var scope2 = factory.Services.CreateAsyncScope();
        var store2 = scope2.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store2.UpsertEndpointAsync(project.ProjectId, "ping", "ping", "POST", true,
            JsonDocument.Parse(DefinitionV2).RootElement, null, 2, CancellationToken.None);
        await store2.UpsertGameValuesAsync(project.ProjectId,
            JsonDocument.Parse(ValuesV2).RootElement, null, 2, CancellationToken.None);
        using var second = await SyncRevisionAsync(client, project, 42);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        return new Setup(factory.CreateClient(), project);
    }

    private static async Task<HttpResponseMessage> SyncRevisionAsync(HttpClient client, SelfHostProject project, long revision)
    {
        using var sync = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync");
        sync.Headers.Add("x-api-key", project.SecretKey);
        sync.Content = JsonContent.Create(new { packageIdent = "test.game", currentRevisionId = revision });
        return await client.SendAsync(sync);
    }

    private static async Task<HttpResponseMessage> PostEndpointAsync(Setup setup, string? revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{setup.Project.ProjectId}/ping");
        request.Headers.Add("x-api-key", setup.Project.PublicKey);
        if (revision is not null) request.Headers.Add("x-ns-revision-id", revision);
        request.Content = JsonContent.Create(new { steamId = SteamId });
        return await setup.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetValuesAsync(Setup setup, string? revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/values/{setup.Project.ProjectId}?apiKey={setup.Project.PublicKey}");
        if (revision is not null) request.Headers.Add("x-ns-revision-id", revision);
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

public sealed class SqliteRevisionPinningHttpTests(SqliteHostFactory factory)
    : RevisionPinningHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresRevisionPinningHttpTests(PostgresHostFactory factory)
    : RevisionPinningHttpTests<PostgresHostFactory>(factory);
