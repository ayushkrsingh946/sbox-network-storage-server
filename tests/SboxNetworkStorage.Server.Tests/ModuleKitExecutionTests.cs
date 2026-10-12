using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Application.NetworkStorage.Endpoints;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Starter kits running through the real endpoint engine on SQLite:
/// economy earn/spend checks, progression level math, inventory grant/consume/craft,
/// leaderboard validation, and the disabled-module gate. All calls use a public key.
/// </summary>
public sealed class ModuleKitExecutionTests : IDisposable
{
    private const string Project = "kit-game";
    private const string Player = "76561198000000001";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sbox-kit-" + Guid.NewGuid().ToString("N"));
    private SqliteNetworkStorageStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private async Task<SqliteNetworkStorageStore> StoreAsync()
    {
        if (_store is not null) return _store;
        Directory.CreateDirectory(_directory);
        var store = new SqliteNetworkStorageStore(new SqliteStoreOptions { DatabasePath = Path.Combine(_directory, "store.db") });
        await store.MigrateAsync(Ct);
        _store = store;
        return store;
    }

    private static ModuleInstaller Installer(SqliteNetworkStorageStore store)
    {
        var workspace = new FakeWorkspaceStore();
        return new ModuleInstaller(store, workspace, new ModuleStateStore(workspace),
            new FakeAuditLogger(), TimeProvider.System);
    }

    private static async Task<Dictionary<string, object?>> ValuesAsync(SqliteNetworkStorageStore store)
    {
        var provider = new StoreQueryValuesContextProvider(store, NullLogger<StoreQueryValuesContextProvider>.Instance);
        return new Dictionary<string, object?>(await provider.GetValuesAsync(Project, Ct), StringComparer.Ordinal);
    }

    private static async Task<EndpointExecutionResult?> CallAsync(SqliteNetworkStorageStore store,
        string slug, IReadOnlyDictionary<string, object?> input,
        Func<string, bool>? isModuleDisabled = null)
    {
        var source = new StoreEndpointDataSource(store, NullLogger<StoreEndpointDataSource>.Instance);
        var executor = new EndpointExecutor(source);
        return await executor.TryExecuteAsync(Project, slug, input, Player, "owner",
            await ValuesAsync(store), hasSecretKey: false, isDedicatedServer: false, Ct,
            liveServe: true, enforcePublicAccessGates: true, isModuleDisabled: isModuleDisabled);
    }

    private static JsonElement Body(EndpointExecutionResult? result)
    {
        Assert.NotNull(result);
        return JsonSerializer.SerializeToElement(result!.Body);
    }

    private static double Number(JsonElement body, string name)
    {
        var value = body.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetDouble();
    }

    private static bool Succeeded(JsonElement body)
        => body.GetProperty("success") switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.String } text => bool.Parse(text.GetString()!),
            _ => false,
        };

    private static string ErrorCode(JsonElement body)
        => body.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;

    private static async Task<double> GoldAsync(SqliteNetworkStorageStore store)
    {
        var row = await store.ReadRecordAsync(Project, "economy_balance", Player, Ct);
        Assert.NotNull(row);
        var payload = row!.Value.TryGetProperty("payload_json", out var nested) ? nested : row.Value;
        return payload.GetProperty("gold").GetDouble();
    }

    [Fact]
    public async Task Progression_grant_returns_level_3_for_1200_xp_at_500_per_level()
    {
        var store = await StoreAsync();
        Assert.True((await Installer(store).InstallAsync(1, Project, "progression", null, Ct)).Ok);
        var body = Body(await CallAsync(store, "progression_grant_xp",
            new Dictionary<string, object?> { ["amount"] = 1200.0 }));
        Assert.True(Succeeded(body));
        Assert.Equal(1200, Number(body, "xp"));
        Assert.Equal(3, Number(body, "level"));
    }

    [Fact]
    public async Task Economy_spend_more_than_balance_fails_while_earn_respects_limits()
    {
        var store = await StoreAsync();
        Assert.True((await Installer(store).InstallAsync(1, Project, "economy", null, Ct)).Ok);
        Assert.True(Succeeded(Body(await CallAsync(store, "economy_earn",
            new Dictionary<string, object?> { ["amount"] = 100.0 }))));
        Assert.Equal(100, await GoldAsync(store));

        var denied = Body(await CallAsync(store, "economy_spend",
            new Dictionary<string, object?> { ["amount"] = 200.0 }));
        Assert.Equal(409, (await CallAsync(store, "economy_spend",
            new Dictionary<string, object?> { ["amount"] = 200.0 }))!.Status);
        Assert.Equal("INSUFFICIENT_FUNDS", ErrorCode(denied));
        Assert.Equal(100, await GoldAsync(store));

        var overLimit = await CallAsync(store, "economy_earn",
            new Dictionary<string, object?> { ["amount"] = 100000.0 });
        Assert.Equal(422, overLimit!.Status);

        Assert.True(Succeeded(Body(await CallAsync(store, "economy_spend",
            new Dictionary<string, object?> { ["amount"] = 40.0 }))));
        Assert.Equal(60, await GoldAsync(store));
    }

    [Fact]
    public async Task Inventory_craft_fails_without_ingredients_and_consume_removes_items()
    {
        var store = await StoreAsync();
        Assert.True((await Installer(store).InstallAsync(1, Project, "inventory", null, Ct)).Ok);
        var missing = await CallAsync(store, "inventory_craft",
            new Dictionary<string, object?> { ["recipe_id"] = "copper_dagger" });
        Assert.Equal(400, missing!.Status);
        Assert.Equal("MISSING_MATERIALS", ErrorCode(Body(missing)));

        Assert.True(Succeeded(Body(await CallAsync(store, "inventory_grant",
            new Dictionary<string, object?> { ["item_id"] = "small_potion", ["name"] = "Small Potion" }))));
        var used = Body(await CallAsync(store, "inventory_consume",
            new Dictionary<string, object?> { ["item_id"] = "small_potion" }));
        Assert.True(Succeeded(used));

        var again = await CallAsync(store, "inventory_consume",
            new Dictionary<string, object?> { ["item_id"] = "small_potion" });
        Assert.Equal(400, again!.Status);
    }

    [Fact]
    public async Task Leaderboard_rejects_invalid_scores_and_keeps_the_best()
    {
        var store = await StoreAsync();
        Assert.True((await Installer(store).InstallAsync(1, Project, "leaderboards", null, Ct)).Ok);
        var invalid = await CallAsync(store, "leaderboards_submit",
            new Dictionary<string, object?> { ["score"] = -5.0, ["name"] = "Cheater" });
        Assert.Equal(422, invalid!.Status);
        Assert.Equal("INVALID_SCORE", ErrorCode(Body(invalid)));

        var first = Body(await CallAsync(store, "leaderboards_submit",
            new Dictionary<string, object?> { ["score"] = 100.0, ["name"] = "Scout" }));
        Assert.Equal(100, Number(first, "score"));
        var lower = Body(await CallAsync(store, "leaderboards_submit",
            new Dictionary<string, object?> { ["score"] = 50.0, ["name"] = "Scout" }));
        Assert.Equal(100, Number(lower, "score"));
    }

    [Fact]
    public async Task Disabled_module_endpoints_answer_endpoint_disabled_and_reenable_restores_them()
    {
        var store = await StoreAsync();
        var installer = Installer(store);
        Assert.True((await installer.InstallAsync(1, Project, "economy", null, Ct)).Ok);
        Assert.True(await installer.SetEnabledAsync(1, Project, "economy", enabled: false, Ct));
        var disabled = await CallAsync(store, "economy_get_balance",
            new Dictionary<string, object?>(), isModuleDisabled: _ => true);
        Assert.Equal(403, disabled!.Status);
        Assert.Equal("ENDPOINT_DISABLED", ErrorCode(Body(disabled)));

        Assert.True(await installer.SetEnabledAsync(1, Project, "economy", enabled: true, Ct));
        var restored = await CallAsync(store, "economy_get_balance",
            new Dictionary<string, object?>(), isModuleDisabled: _ => false);
        Assert.Equal(200, restored!.Status);
    }
}
