using Microsoft.Data.Sqlite;
using Npgsql;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>
/// Every migration tagged <see cref="SchemaChange.Additive"/> must keep the previous binary's
/// statements working. Each test migrates to that version with the real migration SQL, then
/// replays the previous version's named-column INSERTs, SELECT lists and key filters for the
/// tables the migration changed, and reads the rows back (raw and through the current store).
/// </summary>
public abstract class AdditiveMigrationReplayTests : IAsyncLifetime
{
    protected static readonly CancellationToken Ct = CancellationToken.None;

    protected abstract IReadOnlyList<SchemaMigration> Migrations { get; }

    /// <summary>Table prefix: empty for SQLite, the quoted schema plus dot for PostgreSQL.</summary>
    protected abstract string Prefix { get; }

    protected abstract RelationalNetworkStorageStore CreateStore();

    protected abstract Task ExecuteAsync(string sql);

    protected abstract Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql);

    protected abstract Task PrepareDatabaseAsync();

    protected abstract Task CleanupAsync();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await CleanupAsync();

    /// <summary>
    /// Applies the real migration SQL for versions 1..N, recording <c>schema_version</c> rows
    /// exactly like <c>MigrateAsync</c> (lowercase tag, compatibility floor).
    /// </summary>
    protected async Task MigrateToAsync(int version)
    {
        await PrepareDatabaseAsync();
        await ExecuteAsync($"CREATE TABLE IF NOT EXISTS {Prefix}schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_at_unix_ms BIGINT NOT NULL, description TEXT NOT NULL, compat TEXT, min_binary_schema INTEGER)");
        foreach (var migration in Migrations.Where(m => m.Version <= version).OrderBy(m => m.Version))
        {
            await ExecuteAsync(migration.BuildSql(Prefix));
            var floor = SchemaCompatibility.FloorAt(Migrations, migration.Version);
            var compat = migration.Compat.ToString().ToLowerInvariant();
            var description = migration.Description.Replace("'", "''", StringComparison.Ordinal);
            await ExecuteAsync($"INSERT INTO {Prefix}schema_version (version, applied_at_unix_ms, description, compat, min_binary_schema) VALUES ({migration.Version}, 0, '{description}', '{compat}', {floor})");
        }
    }

    private static long Long(IReadOnlyDictionary<string, object?> row, string name)
        => Convert.ToInt64(row[name], System.Globalization.CultureInfo.InvariantCulture);

    private static string Text(IReadOnlyDictionary<string, object?> row, string name)
        => Convert.ToString(row[name], System.Globalization.CultureInfo.InvariantCulture)!;

    [SkippableFact]
    public async Task V2_projects_update_keeps_the_v1_project_statements_working()
    {
        await MigrateToAsync(2);
        await using var store = CreateStore();
        Assert.Equal(2, await store.GetSchemaVersionAsync(Ct));

        // v1 shape: named-column INSERT into projects (v2 only rewrites payload data).
        await ExecuteAsync($"INSERT INTO {Prefix}projects (project_id, payload_json, version, updated_at_unix_ms) VALUES ('replay', '{{\"name\":\"Old\"}}', 1, 0)");
        // v1 shape: SELECT list over the same columns.
        var rows = await QueryAsync($"SELECT project_id, payload_json, version FROM {Prefix}projects WHERE project_id = 'replay'");
        var row = Assert.Single(rows);
        Assert.Equal("replay", Text(row, "project_id"));
        Assert.Equal(1L, Long(row, "version"));
        // v1 shape: key filter on the primary key plus version.
        Assert.Single(await QueryAsync($"SELECT project_id FROM {Prefix}projects WHERE project_id = 'replay' AND version = 1"));
        // The current binary reads the previous-shaped row back.
        Assert.Equal("Old", (await store.ReadProjectAsync("replay", Ct))!.Value.GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task V3_log_rebuild_keeps_the_v2_log_statements_working()
    {
        await MigrateToAsync(3);
        await using var store = CreateStore();
        Assert.Equal(3, await store.GetSchemaVersionAsync(Ct));

        // v1/v2 shape (unchanged by v2): named-column INSERTs without the surrogate event_id.
        await ExecuteAsync($"INSERT INTO {Prefix}storage_request_log (project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier) VALUES ('p1', 100, 'GET', '/a', 200, 3, 'pk')");
        await ExecuteAsync($"INSERT INTO {Prefix}storage_request_log (project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier) VALUES ('p1', 100, 'POST', '/b', 500, 9, NULL)");
        await ExecuteAsync($"INSERT INTO {Prefix}storage_errors (project_id, created_at_unix_ms, error_id, message, stack_trace, source, request_path, severity) VALUES ('p1', 100, 'e1', 'boom', 'trace', 'worker', '/a', 'error')");
        // v1/v2 shape: SELECT lists over the pre-rebuild columns.
        var log = await QueryAsync($"SELECT project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier FROM {Prefix}storage_request_log WHERE project_id = 'p1'");
        Assert.Equal(new[] { "/a", "/b" }, log.Select(r => Text(r, "path")).OrderBy(path => path));
        var errors = await QueryAsync($"SELECT project_id, created_at_unix_ms, error_id, message, stack_trace, source, request_path, severity FROM {Prefix}storage_errors WHERE project_id = 'p1' AND created_at_unix_ms = 100");
        var error = Assert.Single(errors);
        Assert.Equal("boom", Text(error, "message"));
        // v1/v2 shape: key filter on (project_id, created_at_unix_ms); the surrogate key keeps
        // both same-millisecond rows that the old primary key could never hold together.
        Assert.Equal(2, (await QueryAsync($"SELECT path FROM {Prefix}storage_request_log WHERE project_id = 'p1' AND created_at_unix_ms = 100")).Count);
        // The current binary (which always migrates to the latest schema first) reads the previous-shaped rows back.
        await store.MigrateAsync(Ct);
        Assert.Equal(2, (await store.ListStorageRequestLogAsync("p1", 10, Ct)).Count);
        Assert.Single(await store.ListStorageErrorsAsync("p1", 10, Ct));
    }
    [SkippableFact]
    public async Task V4_request_log_columns_keep_the_v3_request_statements_working()
    {
        await MigrateToAsync(4);
        await using var store = CreateStore();
        Assert.Equal(4, await store.GetSchemaVersionAsync(Ct));

        // v3 shape: named-column INSERT without library_version/client_type/engine_version.
        await ExecuteAsync($"INSERT INTO {Prefix}storage_request_log (project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier) VALUES ('p1', 100, 'GET', '/old', 200, 3, 'pk')");
        // v3 shape: SELECT list and key filter over the pre-v4 columns.
        var rows = await QueryAsync($"SELECT project_id, created_at_unix_ms, method, path, status_code, duration_ms, api_key_identifier FROM {Prefix}storage_request_log WHERE project_id = 'p1' AND created_at_unix_ms = 100");
        var row = Assert.Single(rows);
        Assert.Equal("/old", Text(row, "path"));
        Assert.Equal(200L, Long(row, "status_code"));
        // The new columns read back as NULL for previous-shaped rows.
        Json.Equal("""{"created_at_unix_ms":100,"method":"GET","path":"/old","status_code":200,"duration_ms":3,"api_key_identifier":"pk","library_version":null,"client_type":null,"engine_version":null}""",
            (await store.ListStorageRequestLogAsync("p1", 10, Ct)).Single());
    }

    [SkippableFact]
    public async Task V5_error_columns_keep_the_v4_error_statements_working()
    {
        await MigrateToAsync(5);
        await using var store = CreateStore();
        Assert.Equal(5, await store.GetSchemaVersionAsync(Ct));

        // v4 shape (v4 left the error table alone): named-column INSERT without the identity columns.
        await ExecuteAsync($"INSERT INTO {Prefix}storage_errors (project_id, created_at_unix_ms, error_id, message, stack_trace, source, request_path, severity) VALUES ('p1', 100, 'e1', 'boom', 'trace', 'worker', '/a', 'error')");
        // v4 shape: SELECT list and key filter over the pre-v5 columns.
        var rows = await QueryAsync($"SELECT project_id, created_at_unix_ms, error_id, message, stack_trace, source, request_path, severity FROM {Prefix}storage_errors WHERE project_id = 'p1' AND created_at_unix_ms = 100");
        var row = Assert.Single(rows);
        Assert.Equal("boom", Text(row, "message"));
        Assert.Equal("/a", Text(row, "request_path"));
        // The new columns read back as NULL for previous-shaped rows.
        Json.Equal("""{"created_at_unix_ms":100,"error_id":"e1","message":"boom","stack_trace":"trace","source":"worker","request_path":"/a","severity":"error","library_version":null,"client_type":null,"engine_version":null,"library_revision":null,"game_revision":null}""",
            (await store.ListStorageErrorsAsync("p1", 10, Ct)).Single());
        // The new v5 table is usable through the current binary.
        var versions = (IClientVersionStore)store;
        var key = new ClientVersionKey("p1", 1000, "1.1.0", 42, "game", 11);
        await versions.WriteClientVersionsAsync([new(key, 3, 20, new byte[PlayerCountSketch.Size])], Ct);
        var read = Assert.Single(await versions.ReadClientVersionsAsync("p1", 1000, Ct));
        Assert.Equal(3, read.Requests);
        Assert.Equal(key, read.Key);
    }
}

public sealed class SqliteAdditiveMigrationReplayTests : AdditiveMigrationReplayTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();

    protected override IReadOnlyList<SchemaMigration> Migrations => SqliteMigrations.All;

    protected override string Prefix => string.Empty;

    protected override RelationalNetworkStorageStore CreateStore() => TestDatabases.CreateSqlite(_path, null);

    protected override Task PrepareDatabaseAsync() => Task.CompletedTask;

    protected override async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    protected override Task CleanupAsync()
    {
        TestDatabases.DeleteSqlite(_path);
        return Task.CompletedTask;
    }
}

public sealed class PostgresAdditiveMigrationReplayTests : AdditiveMigrationReplayTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();

    protected override IReadOnlyList<SchemaMigration> Migrations => PostgresMigrations.All;

    protected override string Prefix => $"\"{_schema}\".";

    protected override RelationalNetworkStorageStore CreateStore()
    {
        Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason);
        return TestDatabases.CreatePostgres(_schema, null);
    }

    protected override Task PrepareDatabaseAsync()
    {
        Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason);
        return TestDatabases.ExecutePostgresAsync($"CREATE SCHEMA IF NOT EXISTS \"{_schema}\"");
    }

    protected override Task ExecuteAsync(string sql)
        => TestDatabases.ExecutePostgresAsync(sql);

    protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabases.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    protected override Task CleanupAsync()
        => TestDatabases.PostgresConnectionString is null ? Task.CompletedTask : TestDatabases.DropPostgresSchemaAsync(_schema);
}
