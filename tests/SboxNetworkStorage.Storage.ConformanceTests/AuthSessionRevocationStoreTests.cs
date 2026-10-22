using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>Auth-session revocation markers (schema v12): durable across restarts, scoped to a project, bounded by purge time.</summary>
public abstract class AuthSessionRevocationStoreTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private RelationalNetworkStorageStore? _store;

    protected abstract RelationalNetworkStorageStore Create();

    protected abstract Task Cleanup();

    private RelationalNetworkStorageStore Store => _store ??= Create();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        await Cleanup();
    }

    [SkippableFact]
    public async Task Revocation_survives_a_restart_of_the_store()
    {
        await Store.MigrateAsync(Ct);
        await Store.RevokeAsync("p1", "fam-1", 1_000, 9_000, Ct);

        await using var restarted = Create();
        Assert.True(await restarted.IsRevokedAsync("p1", "fam-1", Ct));
    }

    [SkippableFact]
    public async Task Markers_are_scoped_to_their_project_and_session_family()
    {
        await Store.MigrateAsync(Ct);
        await Store.RevokeAsync("p1", "fam-1", 1_000, 9_000, Ct);

        Assert.False(await Store.IsRevokedAsync("p2", "fam-1", Ct));
        Assert.False(await Store.IsRevokedAsync("p1", "fam-2", Ct));
    }

    [SkippableFact]
    public async Task Listing_returns_only_the_project_markers_in_session_order()
    {
        await Store.MigrateAsync(Ct);
        await Store.RevokeAsync("p1", "fam-b", 2_000, 9_000, Ct);
        await Store.RevokeAsync("p1", "fam-a", 1_000, 8_000, Ct);
        await Store.RevokeAsync("p2", "fam-c", 3_000, 7_000, Ct);

        var rows = await Store.ListAsync("p1", Ct);

        Assert.Equal(
            new[] { new AuthSessionRevocation("fam-a", 1_000, 8_000), new AuthSessionRevocation("fam-b", 2_000, 9_000) },
            rows);
    }

    [SkippableFact]
    public async Task Repeated_revocation_preserves_earliest_time_and_longest_retention()
    {
        await Store.MigrateAsync(Ct);
        await Store.RevokeAsync("p1", "fam-1", 1_000, 2_000, Ct);
        await Store.RevokeAsync("p1", "fam-1", 1_500, 9_000, Ct);
        await Store.RevokeAsync("p1", "fam-1", 500, 3_000, Ct);

        Assert.Equal(new[] { new AuthSessionRevocation("fam-1", 500, 9_000) }, await Store.ListAsync("p1", Ct));
        Assert.Equal(0, await Store.PurgeExpiredAsync(2_500, Ct));
        Assert.True(await Store.IsRevokedAsync("p1", "fam-1", Ct));
        Assert.Equal(1, await Store.PurgeExpiredAsync(9_001, Ct));
        Assert.False(await Store.IsRevokedAsync("p1", "fam-1", Ct));
    }

    [SkippableFact]
    public async Task Purge_keeps_markers_until_their_purge_time_has_passed()
    {
        await Store.MigrateAsync(Ct);
        await Store.RevokeAsync("p1", "fam-1", 1_000, 5_000, Ct);

        Assert.Equal(0, await Store.PurgeExpiredAsync(5_000, Ct));
        Assert.True(await Store.IsRevokedAsync("p1", "fam-1", Ct));

        Assert.Equal(1, await Store.PurgeExpiredAsync(5_001, Ct));
        Assert.False(await Store.IsRevokedAsync("p1", "fam-1", Ct));
    }

    [SkippableFact]
    public async Task Concurrent_revocations_of_one_family_all_complete_and_leave_one_marker()
    {
        await Store.MigrateAsync(Ct);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Store.RevokeAsync("p1", "fam-1", 1_000 + i, 9_000, Ct)));

        Assert.True(await Store.IsRevokedAsync("p1", "fam-1", Ct));
        Assert.Equal(1, await Store.PurgeExpiredAsync(9_001, Ct));
    }
}

public sealed class SqliteAuthSessionRevocationStoreTests : AuthSessionRevocationStoreTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();

    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);

    protected override Task Cleanup()
    {
        TestDatabases.DeleteSqlite(_path);
        return Task.CompletedTask;
    }
}

public sealed class PostgresAuthSessionRevocationStoreTests : AuthSessionRevocationStoreTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();

    protected override RelationalNetworkStorageStore Create()
    {
        Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason);
        return TestDatabases.CreatePostgres(_schema, null);
    }

    protected override Task Cleanup()
        => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
