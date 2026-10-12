using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Storage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class DemoReadOnlyTests
{
    [Fact]
    public async Task AnonymousVisitorsBrowseStoredRowsButCannotMutateOrAccessCredentials()
    {
        using var fixture = new SqliteHostFactory();
        await using var app = ServerHost.Build(DemoConfig(fixture.Config), configureBuilder: builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var scope = app.Services.CreateScope();
        var project = Assert.Single(await scope.ServiceProvider.GetRequiredService<IWorkspaceStore>().GetUserProjectsAsync(1, default));
        var store = scope.ServiceProvider.GetRequiredService<INetworkStorageStore>();
        var before = (await store.ReadRecordAsync(project.Id, "players", "demo-player-001", default))!.Value.GetRawText();
        Assert.Contains("Read-only demo", await client.GetStringAsync("/dashboard"));
        Assert.Contains("Demo Scout", await client.GetStringAsync($"/dashboard/projects/{project.Id}/data/players/records/demo-player-001"));
        foreach (var path in new[] { "/setup", "/v3/endpoints/example/save", "/dashboard/export", "/v3/manage/example/keys" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        foreach (var path in new[] { "/dashboard/projects", $"/dashboard/projects/{project.Id}/delete", $"/dashboard/projects/{project.Id}/data/players/records/demo-player-001", "/logout",
            "/dashboard/security/disable", "/dashboard/server/alerts/channels", "/dashboard/server/alerts/channels/test", $"/dashboard/projects/{project.Id}/tests/run" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, new StringContent("{}"))).StatusCode);
        Assert.Equal(before, (await store.ReadRecordAsync(project.Id, "players", "demo-player-001", default))!.Value.GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task MonitoringExamplesNeverExposeHostPathsOrSavedNotificationCredentials()
    {
        using var fixture = new SqliteHostFactory();
        await using var app = ServerHost.Build(DemoConfig(fixture.Config), configureBuilder: builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        using var client = app.GetTestClient();
        var monitoring = app.Services.GetRequiredService<SboxNetworkStorage.Server.Monitoring.MonitoringStore>();
        monitoring.SaveChannel(new("private-channel", "Private operator channel", "webhook", "generic",
            SboxNetworkStorage.Server.Alerts.AlertSeverity.Info, null, true), new(Url: "https://example.invalid/private-token"));
        var server = await client.GetStringAsync("/dashboard/server");
        Assert.DoesNotContain(fixture.Config.DataDirectory, server);
        Assert.Contains("Generated monitoring example", server);
        var alerts = await client.GetStringAsync("/dashboard/server/alerts?channel=private-channel");
        Assert.DoesNotContain("Private operator channel", alerts);
        Assert.DoesNotContain("private-token", alerts);
        Assert.Contains("High CPU (sample)", alerts);
        using var stats = JsonDocument.Parse(await client.GetStringAsync("/dashboard/server/stats?viewer=demo&after=9223372036854775807"));
        Assert.True(stats.RootElement.GetProperty("sampleData").GetBoolean());
        Assert.Empty(stats.RootElement.GetProperty("samples").EnumerateArray());
        var security = await client.GetStringAsync("/dashboard/security");
        Assert.DoesNotContain("otpauth://", security);
        Assert.DoesNotContain(fixture.Config.ConfigDirectory, security);
    }

    [Fact]
    public async Task EnablingPublicDemoRefusesAnExistingNonDemoProjectDatabase()
    {
        using var fixture = new SqliteHostFactory();
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().CreateProjectAsync(1, "Private project", "Must stay private", true, true, "player", "", default);
        await using var app = ServerHost.Build(DemoConfig(fixture.Config), configureBuilder: builder => builder.WebHost.UseTestServer());
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
    }

    private static EffectiveConfig DemoConfig(EffectiveConfig config)
    {
        var values = config.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["adminpanel.demo_read_only"] = values["adminpanel.demo_read_only"] with { Value = true };
        return new EffectiveConfig { ConfigDirectory = config.ConfigDirectory, DataDirectory = config.DataDirectory, Layout = config.Layout,
            Values = values, LoadedFiles = config.LoadedFiles, Issues = [] };
    }
}
