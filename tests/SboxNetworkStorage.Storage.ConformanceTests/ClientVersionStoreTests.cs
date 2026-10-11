using SboxNetworkStorage.Storage;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract class ClientVersionStoreTests : IAsyncLifetime
{
    private RelationalNetworkStorageStore? _store;
    protected abstract RelationalNetworkStorageStore Create();
    protected abstract Task Cleanup();
    public async Task InitializeAsync() { _store = Create(); await _store.MigrateAsync(CancellationToken.None); }
    public async Task DisposeAsync() { if (_store is not null) await _store.DisposeAsync(); await Cleanup(); }
    [SkippableFact]
    public async Task Flushes_accumulate_requests_merge_players_and_prune_only_expired_days()
    {
        var store = (IClientVersionStore)_store!;
        var key = new ClientVersionKey("proj_example", 1000, "1.1.0", 42, "game", 11);
        var sketch = new byte[PlayerCountSketch.Size];
        PlayerCountSketch.Add(sketch, key.ProjectId, "76561198000000001");
        await store.WriteClientVersionsAsync([new(key, 10000, 10, sketch)], CancellationToken.None);
        await store.WriteClientVersionsAsync([new(key, 2, 20, sketch), new(key with { Day = 999 }, 1, 15, sketch)], CancellationToken.None);
        var recent = Assert.Single(await store.ReadClientVersionsAsync(key.ProjectId, 1000, CancellationToken.None));
        Assert.Equal(10002, recent.Requests);
        Assert.Equal(20, recent.LastSeenUnixMs);
        Assert.Equal(1, recent.EstimatedPlayers);
        await store.PurgeClientVersionsAsync(1000, CancellationToken.None);
        Assert.Equal(recent.Requests, Assert.Single(await store.ReadClientVersionsAsync(key.ProjectId, 0, CancellationToken.None)).Requests);
    }
}
public sealed class SqliteClientVersionStoreTests : ClientVersionStoreTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();
    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);
    protected override Task Cleanup() { TestDatabases.DeleteSqlite(_path); return Task.CompletedTask; }
}
public sealed class PostgresClientVersionStoreTests : ClientVersionStoreTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();
    protected override RelationalNetworkStorageStore Create() { Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason); return TestDatabases.CreatePostgres(_schema, null); }
    protected override Task Cleanup() => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
