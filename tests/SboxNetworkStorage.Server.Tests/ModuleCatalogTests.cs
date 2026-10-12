using System.Text.Json;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Embedded module catalog: manifest validation, fixed registries and kit compilation.</summary>
public sealed class ModuleCatalogTests
{
    [Fact]
    public void Embedded_catalog_loads_six_available_modules()
    {
        var catalog = ModuleCatalog.Load();
        Assert.Equal(["economy", "integrity", "inventory", "leaderboards", "player-insights", "progression"],
            catalog.Select(module => module.Id).Order(StringComparer.Ordinal));
        Assert.All(catalog, module => Assert.True(module.Available, module.Id));
    }

    [Fact]
    public void Unknown_hook_marks_only_that_module_unavailable()
    {
        var good = ModuleCatalog.ParseModule("good", MinimalManifest("good"), "[]");
        var bad = ModuleCatalog.ParseModule("bad", MinimalManifest("bad", hooks: ["endpoint.executed", "nope.missing"]), "[]");
        Assert.True(good.Available);
        Assert.False(bad.Available);
        Assert.Contains("nope.missing", bad.UnavailableReason ?? string.Empty);
    }

    [Fact]
    public void Unknown_permission_and_panel_are_rejected()
    {
        Assert.Contains("fly",
            ModuleCatalog.ParseModule("bad", MinimalManifest("bad", permissions: ["fly"]), "[]").UnavailableReason ?? string.Empty);
        Assert.Contains("nope",
            ModuleCatalog.ParseModule("bad", MinimalManifest("bad", panels: ["nope"]), "[]").UnavailableReason ?? string.Empty);
    }

    [Fact]
    public void Fixed_registries_cover_the_documented_hooks_permissions_and_panels()
    {
        Assert.Contains("endpoint.executed", ModuleRegistries.Hooks);
        Assert.Contains("ledger.delta", ModuleRegistries.Hooks);
        Assert.Contains("revision.reported", ModuleRegistries.Hooks);
        Assert.Contains("flag-players", ModuleRegistries.Permissions);
        Assert.Contains("block-requests", ModuleRegistries.Permissions);
        Assert.Contains("send-alerts", ModuleRegistries.Permissions);
        Assert.Contains("review-queue", ModuleRegistries.Panels);
        Assert.Contains("action-log", ModuleRegistries.Panels);
    }

    [Fact]
    public void Semantic_versions_compare_numerically()
    {
        Assert.True(ModuleCatalog.CompareVersions("1.0.0", "1.0.1") < 0);
        Assert.True(ModuleCatalog.CompareVersions("1.10.0", "1.9.9") > 0);
        Assert.Equal(0, ModuleCatalog.CompareVersions("2.0.0", "2.0.0"));
    }

    [Theory]
    [InlineData("economy")]
    [InlineData("progression")]
    [InlineData("inventory")]
    [InlineData("leaderboards")]
    public void Starter_kit_definitions_compile(string moduleId)
    {
        var module = ModuleCatalog.Load().First(candidate => candidate.Id == moduleId);
        Assert.True(ModuleInstaller.TryCompileModule(module, out var compiled, out var error), error);
        Assert.NotEmpty(compiled);
    }

    [Fact]
    public void Kit_endpoints_are_public_key_callable()
    {
        foreach (var moduleId in new[] { "economy", "progression", "inventory", "leaderboards" })
        {
            var module = ModuleCatalog.Load().First(candidate => candidate.Id == moduleId);
            Assert.True(ModuleInstaller.TryCompileModule(module, out var compiled, out _));
            foreach (var definition in compiled)
            {
                var requiresSecret = definition.ValueKind == JsonValueKind.Object
                    && definition.TryGetProperty("requiresSecretKey", out var flag)
                    && flag.ValueKind == JsonValueKind.True;
                Assert.False(requiresSecret, $"{moduleId} must work with a public key.");
            }
        }
    }

    private static string MinimalManifest(string id,
        string[]? permissions = null, string[]? hooks = null, string[]? panels = null)
        => $"""
            id: {id}
            version: 1.0.0
            minServerVersion: 0.6.0
            name: Test module
            description: Minimal test module.
            permissions: [{string.Join(", ", permissions ?? [])}]
            hooks: [{string.Join(", ", hooks ?? ["endpoint.executed"])}]
            panels: [{string.Join(", ", panels ?? [])}]
            """;
}
