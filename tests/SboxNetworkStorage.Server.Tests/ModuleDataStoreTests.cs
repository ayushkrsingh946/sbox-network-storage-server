using System.Text.Json;
using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Module data tables (v6 migration) on a real relational driver.</summary>
public sealed class ModuleDataStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sbox-moduledata-" + Guid.NewGuid().ToString("N"));
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

    private static IModuleDataStore Modules(SqliteNetworkStorageStore store) => store;

    [Fact]
    public async Task Flags_round_trip_with_filters_and_state_changes()
    {
        var modules = Modules(await StoreAsync());
        var evidence = JsonSerializer.SerializeToElement(new { field = "gold", observedDelta = 6000.0 });
        await modules.InsertFlagAsync("game", "flag_a", 1000, "p1", "value-delta", "high", "open", null, evidence, Ct);
        await modules.InsertFlagAsync("game", "flag_b", 2000, "p1", "call-rate", "medium", "open", null, null, Ct);
        await modules.InsertFlagAsync("game", "flag_c", 3000, "p2", "value-delta", "high", "open", null, null, Ct);

        Assert.Equal(3, (await modules.ListFlagsAsync("game", "open", null, null, null, 10, Ct)).Count);
        Assert.Equal(2, (await modules.ListFlagsAsync("game", null, null, null, "p1", 10, Ct)).Count);
        Assert.Single(await modules.ListFlagsAsync("game", null, "call-rate", null, null, 10, Ct));
        Assert.Equal(3, await modules.CountOpenFlagsAsync("game", Ct));

        Assert.True(await modules.UpdateFlagStateAsync("game", "flag_a", "dismissed", "event reward", Ct));
        Assert.False(await modules.UpdateFlagStateAsync("game", "missing", "dismissed", null, Ct));
        Assert.Equal(2, await modules.CountOpenFlagsAsync("game", Ct));
        var dismissed = await modules.ListFlagsAsync("game", "dismissed", null, null, null, 10, Ct);
        var kept = Assert.Single(dismissed);
        Assert.Equal("flag_a", kept.GetProperty("flag_id").GetString());
        Assert.Equal("high", kept.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task Blocks_upsert_read_list_and_delete()
    {
        var modules = Modules(await StoreAsync());
        Assert.Null(await modules.ReadBlockAsync("game", "p1", Ct));
        await modules.UpsertBlockAsync("game", "p1", "writes", "cheating", 1000, 2000, "owner-dashboard", Ct);
        var block = (await modules.ReadBlockAsync("game", "p1", Ct))!.Value;
        Assert.Equal("writes", block.GetProperty("scope").GetString());
        Assert.Equal(2000, block.GetProperty("expires_at_unix_ms").GetInt64());
        await modules.UpsertBlockAsync("game", "p1", "all", "repeat", 1500, null, "owner-dashboard", Ct);
        Assert.Equal("all", (await modules.ReadBlockAsync("game", "p1", Ct))!.Value.GetProperty("scope").GetString());
        Assert.Single(await modules.ListBlocksAsync("game", Ct));
        await modules.DeleteBlockAsync("game", "p1", Ct);
        Assert.Null(await modules.ReadBlockAsync("game", "p1", Ct));
    }

    [Fact]
    public async Task Action_log_filters_orders_and_purges_per_project()
    {
        var modules = Modules(await StoreAsync());
        await modules.InsertActionLogAsync("game", "p1", 1000, "e1", "economy_earn", 42, "ok",
            JsonSerializer.SerializeToElement(new[] { "gold" }), null, Ct);
        await modules.InsertActionLogAsync("game", "p1", 2000, "e2", "economy_spend", 42, "INSUFFICIENT_FUNDS", null, null, Ct);
        await modules.InsertActionLogAsync("game", "p2", 3000, "e3", "economy_earn", 42, "ok", null, null, Ct);

        var player = await modules.ListActionLogAsync("game", "p1", null, null, null, null, 10, Ct);
        Assert.Equal(2, player.Count);
        Assert.Equal("e2", player[0].GetProperty("entry_id").GetString());
        Assert.Single(await modules.ListActionLogAsync("game", null, "economy_spend", null, null, null, 10, Ct));
        Assert.Single(await modules.ListActionLogAsync("game", null, null, "ok", 1500, null, 10, Ct));
        Assert.Equal(42, player[0].GetProperty("revision_id").GetInt64());

        Assert.Equal(2, await modules.PurgeProjectActionLogAsync("game", 2001, Ct));
        Assert.Single(await modules.ListActionLogAsync("game", null, null, null, null, null, 10, Ct));
    }

    [Fact]
    public async Task Sessions_list_newest_first_per_player()
    {
        var store = await StoreAsync();
        var modules = Modules(store);
        Assert.Empty(await modules.ListSessionsAsync("game", null, 10, Ct));
        await store.InsertPlayerSessionAsync("game", "p1", "s1", 1000, 1500, 2000, null, null, Ct);
        await store.InsertPlayerSessionAsync("game", "p1", "s2", 3000, 3500, null, null, null, Ct);
        await store.InsertPlayerSessionAsync("game", "p2", "s3", 2000, 2500, 2600, null, null, Ct);
        var all = await modules.ListSessionsAsync("game", null, 10, Ct);
        Assert.Equal(3, all.Count);
        Assert.Equal("s2", all[0].GetProperty("session_id").GetString());
        var mine = await modules.ListSessionsAsync("game", "p1", 10, Ct);
        Assert.Equal(2, mine.Count);
    }
}
