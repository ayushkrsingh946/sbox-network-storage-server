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
/// Phase 4 (game-version-policy, 9.4): opt-in data-plane enforcement runs
/// after auth. Enforcement off allows; <c>block_writes</c> blocks a mutating
/// endpoint but allows reads; secret keys are exempt; requests without a
/// revision are never blocked.
/// </summary>
public abstract class VersionPolicyEnforcementHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string SteamId = "76561198000000001";
    // Proven shape (EndpointPlayerIdentityHttpTests.Definition): a public
    // endpoint that writes the caller record and answers its version marker.
    private const string DefinitionV1 = """
        {"exposure":"public","steps":[{"id":"save","type":"write","collection":"players","key":"{{steamId}}","ops":[{"op":"set","path":"marker","value":"executed"}]}],"response":{"status":200,"body":{"ok":true,"version":"v1"}}}
        """;
    private readonly TFactory factory;

    protected VersionPolicyEnforcementHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    [SkippableFact]
    public async Task EnforcementOff_AllowsExpiredWrite()
    {
        using var setup = await CreateExpiredSetupAsync(serverEnforcement: false);
        using var response = await PostEndpointAsync(setup, revision: 41, secret: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task BlockWrites_BlocksMutatingEndpoint_AllowsReads()
    {
        using var setup = await CreateExpiredSetupAsync(serverEnforcement: true);
        using var blocked = await PostEndpointAsync(setup, revision: 41, secret: false);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var body = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("REVISION_BLOCKED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("block_writes", body.RootElement.GetProperty("error").GetProperty("action").GetString());
        Assert.Equal(42, body.RootElement.GetProperty("error").GetProperty("latestRevisionId").GetInt64());
        Assert.True(body.RootElement.TryGetProperty("_revisionStatus", out _));

        // Reads from the same expired client still succeed.
        using var values = await GetValuesAsync(setup, revision: 41);
        Assert.Equal(HttpStatusCode.OK, values.StatusCode);
    }

    [SkippableFact]
    public async Task SecretKey_IsExempt()
    {
        using var setup = await CreateExpiredSetupAsync(serverEnforcement: true);
        using var response = await PostEndpointAsync(setup, revision: 41, secret: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task NoRevisionHeader_IsNeverBlocked()
    {
        using var setup = await CreateExpiredSetupAsync(serverEnforcement: true);
        using var response = await PostEndpointAsync(setup, revision: null, secret: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task UnknownRevision_FallsBackLive()
    {
        using var setup = await CreateExpiredSetupAsync(serverEnforcement: true);
        using var response = await PostEndpointAsync(setup, revision: 9999, secret: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Setup> CreateExpiredSetupAsync(bool serverEnforcement)
    {
        var project = await factory.CreateProjectAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        await store.UpsertCollectionAsync(project.ProjectId, "players", "players", "private",
            JsonDocument.Parse("{\"collectionType\":\"player\"}").RootElement, 1, CancellationToken.None);
        await store.UpsertEndpointAsync(project.ProjectId, "ping", "ping", "POST", true,
            JsonDocument.Parse(DefinitionV1).RootElement, null, 1, CancellationToken.None);
        await store.UpsertGameValuesAsync(project.ProjectId,
            JsonDocument.Parse("{\"items\":[]}").RootElement, null, 1, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
            1, project.ProjectId, "revisions", new Dictionary<string, string>
            {
                ["revisionEnforcementMode"] = "force_upgrade",
                // Zero grace: an outdated client is past grace immediately.
                ["revisionGracePeriodMinutes"] = "0",
                ["revisionPostGraceAction"] = "block_writes",
                ["revisionServerEnforcementEnabled"] = serverEnforcement ? "true" : "false",
            }, CancellationToken.None);

        using var client = factory.CreateClient();
        foreach (var revision in new[] { 41L, 42L })
        {
            using var sync = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync");
            sync.Headers.Add("x-api-key", project.SecretKey);
            sync.Content = JsonContent.Create(new { packageIdent = "test.game", currentRevisionId = revision });
            using var response = await client.SendAsync(sync);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        return new Setup(factory.CreateClient(), project);
    }

    private static async Task<HttpResponseMessage> PostEndpointAsync(Setup setup, long? revision, bool secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{setup.Project.ProjectId}/ping");
        if (secret)
            request.Headers.Add("x-secret-key", setup.Project.SecretKey);
        else
            request.Headers.Add("x-api-key", setup.Project.PublicKey);
        if (revision.HasValue)
            request.Headers.Add("x-ns-revision-id", revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new { steamId = SteamId });
        return await setup.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetValuesAsync(Setup setup, long? revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/values/{setup.Project.ProjectId}?apiKey={setup.Project.PublicKey}");
        if (revision.HasValue)
            request.Headers.Add("x-ns-revision-id", revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

public sealed class SqliteVersionPolicyEnforcementHttpTests(SqliteHostFactory factory)
    : VersionPolicyEnforcementHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresVersionPolicyEnforcementHttpTests(PostgresHostFactory factory)
    : VersionPolicyEnforcementHttpTests<PostgresHostFactory>(factory);
