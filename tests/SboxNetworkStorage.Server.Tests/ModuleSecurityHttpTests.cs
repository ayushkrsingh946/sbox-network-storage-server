using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Module controls on the live request path: a disabled module's endpoints
/// deny even when the caller reports an old revision, and player blocks
/// enforce write/all scopes with secret-key exemption.
/// </summary>
public sealed class ModuleSecurityHttpTests(SqliteHostFactory factory) : IClassFixture<SqliteHostFactory>
{
    private const string Player = "76561198000000001";

    [SkippableFact]
    public async Task Disabled_module_endpoint_denies_despite_old_revision_header()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Module Gates");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
            Assert.True((await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId,
                project.ProjectId, "economy", null, CancellationToken.None)).Ok);
            Assert.True(await installer.SetEnabledAsync(NetworkStorageServices.LocalOwnerUserId,
                project.ProjectId, "economy", enabled: false, CancellationToken.None));
        }
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/v3/endpoints/{project.ProjectId}/economy_get_balance");
        request.Headers.Add("x-api-key", project.PublicKey);
        request.Headers.Add("x-steam-id", Player);
        request.Headers.Add("x-ns-revision-id", "41");
        request.Content = JsonContent.Create(new { });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ENDPOINT_DISABLED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Blocked_player_gets_403_on_writes_reads_succeed_secret_key_exempt()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Module Blocks");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
            Assert.True((await installer.InstallAsync(NetworkStorageServices.LocalOwnerUserId,
                project.ProjectId, "economy", null, CancellationToken.None)).Ok);
        }
        await factory.Services.GetRequiredService<PlayerBlockService>().BlockAsync(
            project.ProjectId, Player, PlayerBlockService.WritesScope, "demo cheat", expiresAtUnixMs: null,
            "owner-dashboard", CancellationToken.None);

        using var client = factory.CreateClient();
        using var earn = await PostAsync(client, project.ProjectId, project.PublicKey, "economy_earn", new { amount = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, earn.StatusCode);
        using var earnBody = JsonDocument.Parse(await earn.Content.ReadAsStringAsync());
        Assert.Equal("PLAYER_BLOCKED", earnBody.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var balance = await PostAsync(client, project.ProjectId, project.PublicKey, "economy_get_balance", new { });
        Assert.Equal(HttpStatusCode.OK, balance.StatusCode);

        using var secret = await PostAsync(client, project.ProjectId, project.SecretKey, "economy_earn", new { amount = 10 }, secret: true);
        Assert.Equal(HttpStatusCode.OK, secret.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string projectId, string apiKey,
        string slug, object body, bool secret = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/endpoints/{projectId}/{slug}");
        request.Headers.Add("x-api-key", apiKey);
        if (!secret) request.Headers.Add("x-steam-id", Player);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
