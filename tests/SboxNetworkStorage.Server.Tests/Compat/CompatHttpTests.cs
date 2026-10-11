using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>
/// S1 over HTTP: the library headers are read into the request log (with the body fallback), and
/// <c>x-ns-server-version</c> rides on every response.
/// </summary>
public abstract class CompatHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;

    protected CompatHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static HttpRequestMessage Get(string url, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private async Task<IReadOnlyList<JsonElement>> RequestLogAsync(string projectId)
    {
        await factory.Services.GetRequiredService<RuntimeActivityWriterService>().FlushAsync(CancellationToken.None);
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Storage.INetworkStorageStore>()
            .ListStorageRequestLogAsync(projectId, 50, CancellationToken.None);
    }

    private static JsonElement RowFor(IReadOnlyList<JsonElement> log, string pathSuffix)
        => log.Single(r => r.GetProperty("path").GetString()!.EndsWith(pathSuffix, StringComparison.Ordinal));

    // ── S1: x-ns-server-version on every response ─────────────────────

    [SkippableFact]
    public async Task ServerVersionIsOnDataPlaneManagementAndErrorResponses()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();

        using var dataPlane = await client.SendAsync(Get($"/v3/values/{project.ProjectId}", ("x-api-key", project.PublicKey)));
        Assert.Equal(HttpStatusCode.OK, dataPlane.StatusCode);

        using var management = await client.PostAsJsonAsync(
            $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}", new { projectId = project.ProjectId, revisionId = 1 });
        Assert.Equal(HttpStatusCode.OK, management.StatusCode);

        using var unauthorized = await client.SendAsync(Get($"/v3/values/{project.ProjectId}", ("x-api-key", "sbox_ns_wrong")));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var managementError = await client.PostAsJsonAsync($"/v3/manage/{project.ProjectId}/revision-init", new { revisionId = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, managementError.StatusCode);

        using var notFound = await client.GetAsync("/v3/nothing-here");
        using var serverInfo = await client.GetAsync("/v3/server-info");
        using var health = await client.GetAsync("/health");

        foreach (var response in new[] { dataPlane, management, unauthorized, managementError, notFound, serverInfo, health })
        {
            Assert.Equal(BuildInfo.Version, Header(response, CompatHeaders.ServerVersion));
        }
    }

    // ── S1: request log ───────────────────────────────────────────────

    [SkippableFact]
    public async Task LibraryVersionClientTypeAndEngineAreLoggedWithTheRequest()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        var key = ("x-api-key", project.PublicKey);

        (await client.SendAsync(Get($"/v3/values/{project.ProjectId}?case=versioned", key, (ClientHeaders.LibraryVersion, "1.1.0"),
            (ClientHeaders.ClientType, "editor"), (ClientHeaders.EngineVersion, "26.10.02")))).Dispose();
        using var garbage = await client.SendAsync(Get($"/v3/values/{project.ProjectId}?case=garbage", key, (ClientHeaders.LibraryVersion, "not-a-version"),
            (ClientHeaders.ClientType, "toaster"), (ClientHeaders.EngineVersion, "26.10 02")));
        (await client.SendAsync(Get($"/v3/values/{project.ProjectId}?case=none", key))).Dispose();

        Assert.Equal(HttpStatusCode.OK, garbage.StatusCode);
        var log = await RequestLogAsync(project.ProjectId);
        Assert.Equal(3, log.Count);
        var versioned = log.Single(r => r.GetProperty("library_version").GetString() == "1.1.0");
        Assert.Equal("editor", versioned.GetProperty("client_type").GetString());
        Assert.Equal("26.10.02", versioned.GetProperty("engine_version").GetString());
        Assert.Equal(200, versioned.GetProperty("status_code").GetInt32());

        var legacy = log.Where(r => r.GetProperty("library_version").GetString() == "legacy").ToList();
        Assert.Equal(2, legacy.Count);
        Assert.All(legacy, r => Assert.Equal(200, r.GetProperty("status_code").GetInt32()));
        Assert.All(legacy, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("client_type").ValueKind));
        Assert.All(legacy, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("engine_version").ValueKind));
    }

    [SkippableFact]
    public async Task RejectedRequestsAreLoggedWithTheirLibraryVersionToo()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var rejected = await client.SendAsync(Get($"/v3/values/{project.ProjectId}", ("x-api-key", "sbox_ns_wrong"), (ClientHeaders.LibraryVersion, "1.1.0")));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        var row = Assert.Single(await RequestLogAsync(project.ProjectId));
        Assert.Equal(401, row.GetProperty("status_code").GetInt32());
        Assert.Equal("1.1.0", row.GetProperty("library_version").GetString());
    }

    // ── S1: body fallback ─────────────────────────────────────────────

    [SkippableTheory]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("1.0", "legacy")]       // the old constant the shipped library sends
    [InlineData("garbage", "legacy")]
    public async Task RevisionInitReportsTheLibraryVersionInItsBody(string bodyVersion, string logged)
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}",
            new { projectId = project.ProjectId, clientType = "game", networkStorageVersion = bodyVersion, revisionId = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await RequestLogAsync(project.ProjectId));
        Assert.Equal(logged, row.GetProperty("library_version").GetString());
    }

    [SkippableFact]
    public async Task HeaderWinsOverTheRevisionInitBody()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}")
        {
            Content = JsonContent.Create(new { networkStorageVersion = "1.1.0", revisionId = 1 }),
        };
        request.Headers.Add(ClientHeaders.LibraryVersion, "1.2.0");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1.2.0", Assert.Single(await RequestLogAsync(project.ProjectId)).GetProperty("library_version").GetString());
    }

    [SkippableFact]
    public async Task HeartbeatAndAnalyticsBodiesReportTheLibraryVersion()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();

        using var heartbeat = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v3/storage/{project.ProjectId}/stats/heartbeat")
        {
            Headers = { { "x-api-key", project.PublicKey } },
            Content = JsonContent.Create(new { steamId = "76561198000000001", playerName = "Tester", libraryVersion = "1.1.0" }),
        });
        using var analytics = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v3/storage/{project.ProjectId}/analytics/events")
        {
            Headers = { { "x-api-key", project.PublicKey } },
            Content = JsonContent.Create(new { steamId = "76561198000000001", type = "custom.compat_test", libraryVersion = "1.1.0" }),
        });

        Assert.True(heartbeat.IsSuccessStatusCode, await heartbeat.Content.ReadAsStringAsync());
        Assert.True(analytics.IsSuccessStatusCode, await analytics.Content.ReadAsStringAsync());

        var log = await RequestLogAsync(project.ProjectId);
        Assert.Equal("1.1.0", RowFor(log, "/stats/heartbeat").GetProperty("library_version").GetString());
        Assert.Equal("1.1.0", RowFor(log, "/analytics/events").GetProperty("library_version").GetString());
    }
}

public sealed class SqliteCompatHttpTests(SqliteHostFactory factory) : CompatHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresCompatHttpTests(PostgresHostFactory factory) : CompatHttpTests<PostgresHostFactory>(factory);
