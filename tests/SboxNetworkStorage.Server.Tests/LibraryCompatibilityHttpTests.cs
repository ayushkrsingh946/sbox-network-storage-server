using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Activity;
using SboxNetworkStorage.Server.Compatibility;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public abstract class LibraryCompatibilityHttpTests<TFactory>(TFactory factory) : IClassFixture<TFactory> where TFactory : SelfHostFactory
{
    [SkippableFact]
    public async Task Legacy_header_and_body_versions_preserve_handshake_and_are_observed_in_logs_and_analytics()
    {
        Skip.If(!factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync();
        using var client = factory.CreateClient();
        foreach (var version in new[] { "1.0", "1.1.0", "garbage" })
        {
            using var response = await client.PostAsJsonAsync($"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}", new { networkStorageVersion = version, revisionId = 42 });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(BuildInfo.Version, response.Headers.GetValues("x-ns-server-version").Single());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        }
        await factory.Services.GetRequiredService<RuntimeActivityWriterService>().FlushAsync(CancellationToken.None);
        var log = await factory.Services.GetRequiredService<INetworkStorageStore>().ListStorageRequestLogAsync(project.ProjectId, 20, CancellationToken.None);
        Assert.Contains(log, row => row.GetProperty("library_version").GetString() == "1.1.0");
        await factory.Services.GetRequiredService<ClientVersionWriter>().FlushAsync(CancellationToken.None);
        var report = await factory.Services.GetRequiredService<ClientVersionsQuery>().ReadAsync(project.ProjectId, 1, null, CancellationToken.None);
        Assert.Contains(report.Versions, row => row.LibraryVersion == "legacy" && row.Requests == 2);
        Assert.Contains(report.Versions, row => row.LibraryVersion == "1.1.0" && row.Requests == 1);
    }

    [SkippableFact]
    public async Task Explicit_project_floor_blocks_runtime_but_leaves_management_recovery_and_auth_errors_unchanged()
    {
        Skip.If(!factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync(requireSboxAuth: false);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(1, project.ProjectId, "library", new() { ["libraryMinimum"] = "1.2.0" }, CancellationToken.None);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-ns-library-version", "1.1.0");
        using var blocked = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal(HttpStatusCode.UpgradeRequired, blocked.StatusCode);
        Assert.Equal("unsupported", blocked.Headers.GetValues("x-ns-library-status").Single());
        using var error = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("LIBRARY_UNSUPPORTED", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var recovery = await client.PostAsJsonAsync($"/v3/manage/{project.ProjectId}/revision-init?apiKey={project.PublicKey}", new { revisionId = 42 });
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
        using var authError = await client.GetAsync($"/v3/values/{project.ProjectId}");
        Assert.Equal(HttpStatusCode.Unauthorized, authError.StatusCode);
    }

    private SelfHostFactory WithAdvertisedPolicy()
    {
        var matrix = factory.Services.GetRequiredService<LibraryCompatibility>().Matrix with
        {
            Recommended = "1.2.0", Latest = "1.3.0",
            Deprecated = [new("<1.1.0", new(2020, 1, 1), new(2020, 1, 1), new(2020, 10, 1), "1.1.0", "https://github.com/sbox-cool/sbox-network-storage-server/issues/57")],
            Blocked = [new("1.2.1", "Test security incident", "https://github.com/sbox-cool/sbox-network-storage-server/issues/63")]
        };
        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(new LibraryCompatibility(matrix, TimeProvider.System))));
    }

    [SkippableTheory]
    [InlineData(null, 200, "unknown")]
    [InlineData("1.0.0", 200, "deprecated")]
    [InlineData("1.1.0", 200, "outdated")]
    [InlineData("1.2.0", 200, "supported")]
    [InlineData("1.2.1", 426, "blocked")]
    public async Task Recommendations_and_deprecation_do_not_break_games_but_linked_security_blocks_do(string? version, int status, string libraryStatus)
    {
        Skip.If(!factory.IsAvailable, factory.SkipReason);
        using var host = WithAdvertisedPolicy();
        var project = await host.CreateProjectAsync();
        using var client = host.CreateClient();
        if (version is not null) client.DefaultRequestHeaders.Add("x-ns-library-version", version);
        using var response = await client.GetAsync($"/v3/values/{project.ProjectId}?apiKey={project.PublicKey}");
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(libraryStatus, response.Headers.GetValues("x-ns-library-status").Single());
        if (status == 426)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("LIBRARY_BLOCKED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task Blocked_package_push_retains_missing_public_and_restricted_key_auth_contracts()
    {
        Skip.If(!factory.IsAvailable, factory.SkipReason);
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
        foreach (var (key, expected) in new[] { ((string?)null, 401), (project.PublicKey, 401), (restricted, 403), (project.SecretKey, 426) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/manage/{project.ProjectId}/package-sync") { Content = JsonContent.Create(new { }) };
            request.Headers.Add("x-ns-library-version", "1.2.1");
            if (key is not null) request.Headers.Add("x-api-key", key);
            using var response = await client.SendAsync(request);
            Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        }
    }
}
public sealed class SqliteLibraryCompatibilityHttpTests(SqliteHostFactory factory) : LibraryCompatibilityHttpTests<SqliteHostFactory>(factory);
public sealed class PostgresLibraryCompatibilityHttpTests(PostgresHostFactory factory) : LibraryCompatibilityHttpTests<PostgresHostFactory>(factory);
