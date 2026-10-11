using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Storage.Relational;

public abstract partial class RelationalNetworkStorageStore
{
    /// <summary>Forward-only migrations, numbered contiguously from 1.</summary>
    protected abstract IReadOnlyList<SchemaMigration> Migrations { get; }

    /// <summary>Tests a known schema metadata table without creating anything.</summary>
    protected abstract Task<bool> SchemaTableExistsAsync(DbConnection connection, string tableName, CancellationToken ct);

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
        if (!await SchemaTableExistsAsync(connection, "schema_version", ct)) return 0;
        await using var command = Command(connection, _sql.ReadSchemaVersion, []);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task EnsureSchemaCompatibleAsync(CancellationToken ct)
    {
        var compatibility = await GetSchemaCompatibilityAsync(ct);
        if (compatibility.MigrationRequired) throw new SchemaMigrationRequiredException(compatibility.DatabaseVersion, SupportedSchemaVersion);
        if (!compatibility.Compatible) throw new SchemaVersionTooNewException(compatibility.DatabaseVersion, SupportedSchemaVersion);
    }

    public async Task<SchemaCompatibility> GetSchemaCompatibilityAsync(CancellationToken ct)
    {
        var current = await GetSchemaVersionAsync(ct);
        await using var connection = await OpenConnectionAsync(ct);
        var floor = await ReadCompatibilityFloorAsync(connection, current, null, ct);
        return new SchemaCompatibility(current, SupportedSchemaVersion, floor);
    }

    private async Task<int?> ReadCompatibilityFloorAsync(DbConnection connection, int current, DbTransaction? transaction, CancellationToken ct)
    {
        if (!await SchemaTableExistsAsync(connection, "schema_compatibility", ct)) return null;
        await using var command = Command(connection,
            $"SELECT v.version, c.min_binary_schema FROM {TablePrefix}schema_version v LEFT JOIN {TablePrefix}schema_compatibility c ON c.version = v.version ORDER BY v.version",
            [], transaction);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var expected = 1;
        var floor = 0;
        while (await rows.ReadAsync(ct))
        {
            var version = rows.GetInt32(0);
            if (version != expected++ || rows.IsDBNull(1)) return null;
            var nextFloor = rows.GetInt32(1);
            var knownFloor = Migrations.Where(m => m.Version <= version && m.Compatibility == SchemaMigrationCompatibility.Breaking)
                .Select(m => m.Version).DefaultIfEmpty(0).Max();
            if (nextFloor < floor || nextFloor < knownFloor || nextFloor > version) return null;
            floor = nextFloor;
        }
        return expected - 1 == current ? floor : null;
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
            var floor = await ReadCompatibilityFloorAsync(connection, current, transaction, ct);
            if (floor is null || floor > supported) throw new SchemaVersionTooNewException(current, supported);
            await transaction.CommitAsync(ct);
            return new SchemaMigrationResult(current, current, []);
        }

        await using (var createCompatibility = Command(connection,
            $"CREATE TABLE IF NOT EXISTS {TablePrefix}schema_compatibility (version INTEGER NOT NULL PRIMARY KEY, min_binary_schema INTEGER NOT NULL)", [], transaction))
            await createCompatibility.ExecuteNonQueryAsync(ct);
        var minimumBinarySchema = 0;
        foreach (var historical in migrations.Where(m => m.Version <= current))
        {
            if (historical.Compatibility == SchemaMigrationCompatibility.Breaking) minimumBinarySchema = historical.Version;
            await RecordCompatibilityAsync(connection, transaction, historical.Version, minimumBinarySchema, ct);
        }

        var applied = new List<int>();
        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            Logger.LogInformation("Applying {Provider} schema migration v{Version}: {Description}", ProviderName, migration.Version, migration.Description);
            await using (var apply = Command(connection, migration.BuildSql(TablePrefix), [], transaction))
                await apply.ExecuteNonQueryAsync(ct);
            await using (var record = Command(connection, _sql.InsertSchemaVersion,
                [Int32("version", migration.Version), Int64("applied_at_unix_ms", UnixMs()), Text("description", migration.Description)], transaction))
                await record.ExecuteNonQueryAsync(ct);
            if (migration.Compatibility == SchemaMigrationCompatibility.Breaking) minimumBinarySchema = migration.Version;
            await RecordCompatibilityAsync(connection, transaction, migration.Version, minimumBinarySchema, ct);
            applied.Add(migration.Version);
        }

        await transaction.CommitAsync(ct);
        if (applied.Count == 0)
            Logger.LogInformation("{Provider} schema already at version {Version}", ProviderName, current);
        else
            Logger.LogInformation("{Provider} schema migrated from version {From} to {To}", ProviderName, current, supported);
        return new SchemaMigrationResult(current, Math.Max(current, supported), applied);
    }

    private async Task RecordCompatibilityAsync(DbConnection connection, DbTransaction transaction, int version, int floor, CancellationToken ct)
    {
        await using var record = Command(connection,
            $"INSERT INTO {TablePrefix}schema_compatibility (version, min_binary_schema) VALUES (@version, @floor) ON CONFLICT (version) DO NOTHING",
            [Int32("version", version), Int32("floor", floor)], transaction);
        await record.ExecuteNonQueryAsync(ct);
    }
}
