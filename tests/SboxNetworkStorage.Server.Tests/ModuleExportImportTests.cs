using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Module definitions and modules.json survive a project export/import round trip
/// and a package sync without module sources keeps module definitions.
/// </summary>
public sealed class ModuleExportImportTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly SqliteHostFactory factory = new();

    public void Dispose() => factory.Dispose();

    [SkippableFact]
    public async Task Round_trip_carries_module_state_and_owned_definitions()
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        var project = await factory.CreateProjectAsync("Module Game");
        var projectId = project.ProjectId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var installer = scope.ServiceProvider.GetRequiredService<ModuleInstaller>();
            Assert.True((await installer.InstallAsync(1, projectId, "economy", null, Ct)).Ok);
        }

        byte[] archive;
        {
            var admin = factory.Services.GetRequiredService<Storage.Relational.INetworkStorageStoreAdmin>();
            await using var staged = await ServerArchive.PrepareProjectExportAsync(
                factory.Services.GetRequiredService<INetworkStorageStore>(), admin, factory.Config, projectId, Ct);
            using var buffer = new MemoryStream();
            await staged.WriteToAsync(buffer, Ct);
            archive = buffer.ToArray();
        }

        var target = await factory.NewStoreAsync();
        ImportResult result;
        {
            using var stream = new MemoryStream(archive);
            result = await ProjectArchive.ImportAsync(stream, target, factory.Config, Ct);
        }
        Assert.Contains(result.Manifest.Projects, entry => entry.Id == projectId);
        var collections = await target.ListCollectionsAsync(projectId, Ct);
        var owned = Assert.Single(collections);
        Assert.Equal("economy_balance", owned.GetProperty("name").GetString());
        Assert.Equal("economy", DefinitionOf(owned).GetProperty("module").GetString());
        Assert.Contains(await target.ListEndpointsAsync(projectId, Ct),
            row => row.GetProperty("slug").GetString() == "economy_earn");
        var modulesJson = await target.ReadWorkspaceObjectAsync(
            $"network-storage/users/1/{projectId}/modules.json", Ct);
        Assert.NotNull(modulesJson);
        Assert.Contains("economy", modulesJson!, StringComparison.Ordinal);
    }

    private static JsonElement DefinitionOf(JsonElement row)
    {
        var definition = row.GetProperty("definition_json");
        return definition.ValueKind == JsonValueKind.String
            ? JsonDocument.Parse(definition.GetString() ?? "{}").RootElement
            : definition;
    }
}
