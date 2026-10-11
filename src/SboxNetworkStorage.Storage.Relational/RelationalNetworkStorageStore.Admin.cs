using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Storage.Relational;

public abstract partial class RelationalNetworkStorageStore
{
    /// <summary>Forward-only migrations, numbered contiguously from 1.</summary>
    protected abstract IReadOnlyList<SchemaMigration> Migrations { get; }

    /// <summary>True when the <c>schema_version</c> table exists (without creating anything).</summary>
    protected abstract Task<bool> SchemaVersionTableExistsAsync(DbConnection connection, CancellationToken ct);

    /// <summary>True when <c>schema_version</c> has the <c>min_binary_schema</c> column (tables created before it do not).</summary>
    protected abstract Task<bool> SchemaVersionHasFloorColumnAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct);

    /// <summary>Runs before the migration transaction starts (e.g. create the PostgreSQL schema).</summary>
    protected virtual Task PrepareForMigrationAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Serializes concurrent migrators inside the migration transaction.</summary>
    protected virtual Task LockForMigrationAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public int SupportedSchemaVersion => Migrations.Count == 0 ? 0 : Migrations.Max(m => m.Version);

    /// <inheritdoc />
    public async Task<StorePingResult> PingAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = Command(connection, "SELECT 1", []);
        await command.ExecuteScalarAsync(ct);
        stopwatch.Stop();
        return new StorePingResult(stopwatch.Elapsed, connection.ServerVersion);
    }

    /// <inheritdoc />
    public async Task<int> GetSchemaVersionAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await SchemaVersionTableExistsAsync(connection, ct)) return 0;
        await using var command = Command(connection, _sql.ReadSchemaVersion, []);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public IReadOnlyList<SchemaMigration> KnownMigrations => Migrations.OrderBy(m => m.Version).ToList();

    /// <inheritdoc />
    public async Task<SchemaState> GetSchemaStateAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        if (!await SchemaVersionTableExistsAsync(connection, ct)) return new SchemaState(0, 0);
        return await ReadSchemaStateAsync(connection, null, KnownMigrations, ct);
    }

    /// <inheritdoc />
    public async Task EnsureSchemaCompatibleAsync(CancellationToken ct)
    {
        var state = await GetSchemaStateAsync(ct);
        var supported = SupportedSchemaVersion;
        if (state.IsTooNewFor(supported)) throw new SchemaVersionTooNewException(state.Version, supported, state.MinBinarySchema);
        if (state.Version < supported) throw new SchemaMigrationRequiredException(state.Version, supported);
    }

    /// <inheritdoc />
    public async Task<StoreUsageCounts> CountUsageAsync(long activeSinceUnixMs, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = Command(connection, _sql.CountUsage, [Int64("active_since", activeSinceUnixMs)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new StoreUsageCounts(0, 0, 0);
        long Read(int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        return new StoreUsageCounts(Read(0), Read(1), Read(2));
    }

    /// <inheritdoc />
    public async Task<SchemaMigrationResult> MigrateAsync(CancellationToken ct)
    {
        var migrations = Migrations.OrderBy(m => m.Version).ToList();
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException($"{ProviderName} migrations must be numbered contiguously from 1; found version {migrations[i].Version} at position {i + 1}.");
        }
        var supported = migrations.Count;

        await using var connection = await OpenConnectionAsync(ct);
        await PrepareForMigrationAsync(connection, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockForMigrationAsync(connection, transaction, ct);

        await using (var create = Command(connection, _sql.CreateSchemaVersionTable, [], transaction))
            await create.ExecuteNonQueryAsync(ct);

        int current;
        await using (var read = Command(connection, _sql.ReadSchemaVersion, [], transaction))
            current = Convert.ToInt32(await read.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);

        if (current > supported)
        {
            // Newer database: fine when every migration past this binary is additive, otherwise refuse. Nothing is written.
            var newer = await ReadSchemaStateAsync(connection, transaction, migrations, ct);
            if (newer.IsTooNewFor(supported)) throw new SchemaVersionTooNewException(current, supported, newer.MinBinarySchema);
            await transaction.CommitAsync(ct);
            Logger.LogInformation("{Provider} schema is at version {Current}, newer than this build's {Supported}, but every later migration is additive (min_binary_schema {Floor}); running on it as is",
                ProviderName, current, supported, newer.MinBinarySchema);
            return new SchemaMigrationResult(current, current, []);
        }

        await EnsureFloorColumnsAsync(connection, transaction, migrations, current, ct);

        var applied = new List<int>();
        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            Logger.LogInformation("Applying {Provider} schema migration v{Version} ({Compat}): {Description}", ProviderName, migration.Version, migration.Compat.ToString().ToLowerInvariant(), migration.Description);
            await using (var apply = Command(connection, migration.BuildSql(TablePrefix), [], transaction))
                await apply.ExecuteNonQueryAsync(ct);
            await using (var record = Command(connection, _sql.InsertSchemaVersion,
                [Int32("version", migration.Version), Int64("applied_at_unix_ms", UnixMs()), Text("description", migration.Description),
                 Text("compat", CompatName(migration.Compat)), Int32("min_binary_schema", SchemaCompatibility.FloorAt(migrations, migration.Version))], transaction))
                await record.ExecuteNonQueryAsync(ct);
            applied.Add(migration.Version);
        }

        await transaction.CommitAsync(ct);
        if (applied.Count == 0)
            Logger.LogInformation("{Provider} schema already at version {Version}", ProviderName, current);
        else
            Logger.LogInformation("{Provider} schema migrated from version {From} to {To}", ProviderName, current, supported);
        return new SchemaMigrationResult(current, Math.Max(current, supported), applied);
    }

    private static string CompatName(SchemaChange change) => change.ToString().ToLowerInvariant();

    /// <summary>
    /// Adds the <c>compat</c>/<c>min_binary_schema</c> columns to a <c>schema_version</c> table that predates them and fills in
    /// the rows this binary knows (their tags are part of the code). Rows already carrying a floor are never rewritten.
    /// </summary>
    private async Task EnsureFloorColumnsAsync(DbConnection connection, DbTransaction transaction, IReadOnlyList<SchemaMigration> migrations, int current, CancellationToken ct)
    {
        if (!await SchemaVersionHasFloorColumnAsync(connection, transaction, ct))
        {
            await using (var compat = Command(connection, _sql.AddSchemaVersionCompatColumn, [], transaction))
                await compat.ExecuteNonQueryAsync(ct);
            await using (var floor = Command(connection, _sql.AddSchemaVersionFloorColumn, [], transaction))
                await floor.ExecuteNonQueryAsync(ct);
        }

        long missing;
        await using (var count = Command(connection, _sql.CountSchemaRowsWithoutFloor, [Int32("version", current)], transaction))
            missing = Convert.ToInt64(await count.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        if (missing == 0) return;

        foreach (var migration in migrations.Where(m => m.Version <= current))
        {
            await using var backfill = Command(connection, _sql.BackfillSchemaFloor,
                [Int32("version", migration.Version), Text("compat", CompatName(migration.Compat)), Int32("min_binary_schema", SchemaCompatibility.FloorAt(migrations, migration.Version))], transaction);
            await backfill.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<SchemaState> ReadSchemaStateAsync(DbConnection connection, DbTransaction? transaction, IReadOnlyList<SchemaMigration> migrations, CancellationToken ct)
    {
        var rows = new List<(int Version, int? MinBinarySchema)>();
        var hasFloor = await SchemaVersionHasFloorColumnAsync(connection, transaction, ct);
        await using var command = Command(connection, hasFloor ? _sql.ReadSchemaRows : _sql.ReadSchemaVersions, [], transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var version = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            int? floor = hasFloor && !reader.IsDBNull(1) ? Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) : null;
            rows.Add((version, floor));
        }

        return SchemaCompatibility.Resolve(rows, migrations);
    }
}
