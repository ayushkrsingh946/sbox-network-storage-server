using Microsoft.Data.Sqlite;
using SboxNetworkStorage.Storage.Sqlite;
using SboxNetworkStorage.Storage.Relational;
using Npgsql;

namespace SboxNetworkStorage.Storage.ConformanceTests;

public abstract class SchemaCompatibilityContract
{
    protected abstract RelationalNetworkStorageStore Create();
    protected abstract Task ExecuteAsync(string sql);
    protected abstract Task CleanupAsync();
    [SkippableTheory]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(null, false)]
    public async Task Future_additive_schema_preserves_new_data_but_breaking_or_uncertified_schema_is_refused(int? floor, bool compatible)
    {
        try
        {
            await using var store = Create();
            await store.MigrateAsync(CancellationToken.None);
            await store.UpsertProjectAsync("proj_example", System.Text.Json.JsonSerializer.SerializeToElement(new { name = "after update" }), 1, CancellationToken.None);
            await ExecuteAsync("INSERT INTO schema_version VALUES (5,0,'future migration');" + (floor is { } f ? $"INSERT INTO schema_compatibility VALUES (5,{f});" : ""));
            Assert.Equal(compatible, (await store.GetSchemaCompatibilityAsync(CancellationToken.None)).Compatible);
            if (compatible)
            {
                await store.EnsureSchemaCompatibleAsync(CancellationToken.None);
                Assert.Equal(5, (await store.MigrateAsync(CancellationToken.None)).ToVersion);
                Assert.Equal("after update", (await store.ReadProjectAsync("proj_example", CancellationToken.None))!.Value.GetProperty("name").GetString());
            }
            else await Assert.ThrowsAsync<SchemaVersionTooNewException>(() => store.MigrateAsync(CancellationToken.None));
        }
        finally { await CleanupAsync(); }
    }
    [SkippableTheory]
    [InlineData("schema_version")]
    [InlineData("schema_compatibility")]
    public async Task Missing_historical_certificate_chain_cannot_authorize_forward_compatibility(string table)
    {
        try
        {
            await using var store = Create();
            await store.MigrateAsync(CancellationToken.None);
            await ExecuteAsync($"INSERT INTO schema_version VALUES (5,0,'future migration'); INSERT INTO schema_compatibility VALUES (5,4); DELETE FROM {table} WHERE version=3;");
            Assert.False((await store.GetSchemaCompatibilityAsync(CancellationToken.None)).Compatible);
            await Assert.ThrowsAsync<SchemaVersionTooNewException>(() => store.MigrateAsync(CancellationToken.None));
        }
        finally { await CleanupAsync(); }
    }
}

public sealed class SchemaCompatibilityTests : SchemaCompatibilityContract
{
    private readonly string _path = TestDatabases.NewSqlitePath();
    protected override RelationalNetworkStorageStore Create() => TestDatabases.CreateSqlite(_path, null);
    protected override async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    protected override Task CleanupAsync() { TestDatabases.DeleteSqlite(_path); return Task.CompletedTask; }
}

public sealed class PostgresSchemaCompatibilityTests : SchemaCompatibilityContract
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();
    protected override RelationalNetworkStorageStore Create()
    {
        Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason);
        return TestDatabases.CreatePostgres(_schema, null);
    }
    protected override async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabases.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET search_path TO \"{_schema}\";" + sql;
        await command.ExecuteNonQueryAsync();
    }
    protected override Task CleanupAsync() => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
