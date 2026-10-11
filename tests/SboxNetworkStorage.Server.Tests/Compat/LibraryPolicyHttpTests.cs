using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>
/// Opt-in library enforcement over HTTP: legacy clients are never refused, the release-feed overlay is
/// report-only, and only a per-project minimum or an embedded blocklist range returns HTTP 426.
/// </summary>
public abstract class LibraryPolicyHttpTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly TFactory factory;

    protected LibraryPolicyHttpTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        this.factory = factory;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private SelfHostFactory WithAdvertisedPolicy()
    {
        var matrix = new LibraryMatrix(
            Minimum: null,
            Recommended: "1.2.0",
            Libraries: [],
            Deprecated: [new MatrixRule("<1.1.0", "2020-10-01", null, null)],
            Blocked: [new MatrixRule("1.2.1", null, "Test security incident", "https://github.com/sbox-cool/sbox-network-storage-server/issues/63")],
            Tested: []);
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(new LibraryCompatProvider(matrix))));
    }

    [SkippableFact]
    public async Task Legacy_header_and_body_versions_preserve_handshake_and_are_observed_in_logs_and_analytics()
    {
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        foreach (var version in new[] { "1.0", "1.1.0", "garbage" })
        {
            using var response = await client.PostAsJsonAsync($"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}", new { networkStorageVersion = version, revisionId = 42 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(BuildInfo.Version, Header(response, CompatHeaders.ServerVersion));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }
        await factory.Services.GetRequiredService<RuntimeActivityWriterService>().FlushAsync(CancellationToken.None);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var log = await scope.ServiceProvider.GetRequiredService<SboxNetworkStorage.Storage.INetworkStorageStore>()
                .ListStorageRequestLogAsync(project.ProjectId, 20, CancellationToken.None);
            Assert.Contains(log, row => row.GetProperty("library_version").GetString() == "1.1.0");
        }
        await factory.Services.GetRequiredService<ClientVersionWriter>().FlushAsync(CancellationToken.None);
        var report = await factory.Services.GetRequiredService<ClientVersionsQuery>().ReadAsync(project.ProjectId, 1, null, CancellationToken.None);
        Assert.Contains(report.Versions, row => row.LibraryVersion == "legacy" && row.Requests == 2);
        Assert.Contains(report.Versions, row => row.LibraryVersion == "1.1.0" && row.Requests == 1);
    }

    [SkippableFact]
    public async Task Explicit_project_floor_blocks_runtime_but_leaves_management_recovery_and_auth_errors_unchanged()
    {
        var project = await factory.CreateProjectAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-ns-library-version", "1.1.0");
        using var blocked = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.UpgradeRequired, blocked.StatusCode);
        Assert.Equal("unsupported", Header(blocked, CompatHeaders.LibraryStatus));
        using var error = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("LIBRARY_UNSUPPORTED", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var recovery = await client.PostAsJsonAsync($"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}", new { revisionId = 42 });
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        using var authError = await client.GetAsync($"/v3/values/{project.ProjectId}");
        Assert.Equal(HttpStatusCode.Unauthorized, authError.StatusCode);
    }

    [SkippableFact]
    public async Task Legacy_client_with_a_project_minimum_is_never_refused()
    {
        var project = await factory.CreateProjectAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        using var client = factory.CreateClient();
        foreach (var version in new string?[] { null, "garbage", "1.0" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
            if (version is not null) request.Headers.TryAddWithoutValidation(ClientHeaders.LibraryVersion, version);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("unknown", Header(response, CompatHeaders.LibraryStatus));
        }
    }

    [SkippableFact]
    public async Task Feed_overlay_block_is_report_only_and_never_refuses()
    {
        var project = await factory.CreateProjectAsync();
        var provider = factory.Services.GetRequiredService<LibraryCompatProvider>();
        provider.SetOverlay(new LibraryCompatOverlay(Blocked: [new MatrixRule("1.1.0", null, "Feed-only block", null)]));
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("x-ns-library-version", "1.1.0");
            using var response = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("blocked", Header(response, CompatHeaders.LibraryStatus));
        }
        finally
        {
            provider.SetOverlay(null);
        }
    }

    [SkippableTheory]
    [InlineData(null, HttpStatusCode.OK, "unknown")]
    [InlineData("1.0.0", HttpStatusCode.OK, "deprecated")]
    [InlineData("1.1.0", HttpStatusCode.OK, "outdated")]
    [InlineData("1.2.0", HttpStatusCode.OK, "supported")]
    [InlineData("1.2.1", HttpStatusCode.UpgradeRequired, "blocked")]
    public async Task Recommendations_and_deprecation_do_not_break_games_but_embedded_blocks_do(string? version, HttpStatusCode status, string libraryStatus)
    {
        using var host = WithAdvertisedPolicy();
        var project = await host.CreateProjectAsync();
        using var client = host.CreateClient();
        if (version is not null) client.DefaultRequestHeaders.Add("x-ns-library-version", version);
        using var response = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(libraryStatus, Header(response, CompatHeaders.LibraryStatus));
        if (status == HttpStatusCode.UpgradeRequired)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("LIBRARY_BLOCKED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Blocked_package_push_retains_missing_public_and_restricted_key_auth_contracts()
    {
        using var host = WithAdvertisedPolicy();
        var project = await host.CreateProjectAsync();
        string restricted;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var (_, key) = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().CreateProjectKeyAsync(
                1, project.ProjectId, "restricted", "secret", new() { ["settings"] = "none" }, CancellationToken.None);
            restricted = key;
        }
        using var client = host.CreateClient();
        foreach (var (key, expected) in new[] { ((string?)null, HttpStatusCode.Unauthorized), (project.PublicKey, HttpStatusCode.Unauthorized), (restricted, HttpStatusCode.Forbidden), (project.SecretKey, HttpStatusCode.UpgradeRequired) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync") { Content = JsonContent.Create(new { }) };
            request.Headers.Add("x-ns-library-version", "1.2.1");
            if (key is not null) request.Headers.Add("x-api-key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
    }
}

public sealed class SqliteLibraryPolicyHttpTests(SqliteHostFactory factory) : LibraryPolicyHttpTests<SqliteHostFactory>(factory);

public sealed class PostgresLibraryPolicyHttpTests(PostgresHostFactory factory) : LibraryPolicyHttpTests<PostgresHostFactory>(factory);
