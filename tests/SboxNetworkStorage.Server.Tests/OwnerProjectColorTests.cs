using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;
using Xunit;
using static SboxNetworkStorage.Server.Tests.Support.OwnerHttp;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Project colour setting: persist, reject, clear, and render the generated tile.</summary>
public sealed class OwnerProjectColorTests
{
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string action, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(pageUrl);
        return await client.PostAsync(action, Form([.. fields, ("__RequestVerificationToken", Csrf(page))]));
    }

    private static async Task<string?> ColorAsync(SqliteHostFactory factory, string projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var access = await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
            .ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, projectId, CancellationToken.None);
        return access?.Project.Color;
    }

    [Fact]
    public async Task ProjectColorPersistsNormalizesRejectsAndClears()
    {
        using var factory = new SqliteHostFactory();
        await CreateOwnerAsync(factory);
        using var owner = await LoggedInClientAsync(factory);
        var project = await factory.CreateProjectAsync("Colour game");
        var url = $"/dashboard/projects/{project.ProjectId}";

        var settings = await owner.GetStringAsync(url);
        Assert.Contains("name=\"color\"", settings, StringComparison.Ordinal);
        Assert.Contains("Automatic colour", settings, StringComparison.Ordinal);

        using (var saved = await PostFormAsync(owner, url, url + "/settings",
            ("tab", "project"), ("name", "Colour game"), ("description", ""), ("enabled", "true"), ("color", "#3366CC")))
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("#3366cc", await ColorAsync(factory, project.ProjectId));

        var hub = WebUtility.HtmlDecode(await owner.GetStringAsync("/dashboard"));
        Assert.Contains("<svg", hub, StringComparison.Ordinal);
        Assert.Contains("fill=\"#3366cc\"", hub, StringComparison.Ordinal);

        using (var kept = await PostFormAsync(owner, url, url + "/settings",
            ("tab", "project"), ("name", "Colour game"), ("description", ""), ("enabled", "true"), ("color", "not-a-colour")))
            Assert.Equal(HttpStatusCode.Redirect, kept.StatusCode);
        Assert.Equal("#3366cc", await ColorAsync(factory, project.ProjectId));

        using (var cleared = await PostFormAsync(owner, url, url + "/settings",
            ("tab", "project"), ("name", "Colour game"), ("description", ""), ("enabled", "true"), ("color", "auto")))
            Assert.Equal(HttpStatusCode.Redirect, cleared.StatusCode);
        Assert.Null(await ColorAsync(factory, project.ProjectId));
    }
}
