namespace SboxNetworkStorage.Storage.Relational;

/// <summary>
/// Operational surface of a database-backed store, used by <c>sbox-ns db …</c>,
/// <c>doctor</c>, and server startup. Never exposes credentials.
/// </summary>
public interface INetworkStorageStoreAdmin
{
    /// <summary>Driver name: <c>sqlite</c> or <c>postgres</c>.</summary>
    string ProviderName { get; }

    /// <summary>Credential-free description of the target (file path, or <c>postgres://user@host:port/db?schema=x</c>).</summary>
    string RedactedTarget { get; }

    /// <summary>Highest schema version this binary knows how to migrate to.</summary>
    int SupportedSchemaVersion { get; }

    /// <summary>Opens a connection and runs a trivial query. Throws the driver exception on failure.</summary>
    Task<StorePingResult> PingAsync(CancellationToken ct);

    /// <summary>Current schema version recorded in the database; 0 when the database has never been migrated.</summary>
    Task<int> GetSchemaVersionAsync(CancellationToken ct);

    /// <summary>
    /// Applies every pending forward-only migration inside one transaction.
    /// Throws <see cref="SchemaVersionTooNewException"/> when the database is newer than this binary and not additively compatible;
    /// an additively newer database is left untouched and the result applies nothing.
    /// </summary>
    Task<SchemaMigrationResult> MigrateAsync(CancellationToken ct);

    /// <summary>
    /// Verifies the database schema is usable by this binary: equal to it, or newer only through additive migrations
    /// (<see cref="SchemaState.MinBinarySchema"/> at or below <see cref="SupportedSchemaVersion"/>). Throws
    /// <see cref="SchemaVersionTooNewException"/> for a database newer than this binary can run on, or
    /// <see cref="SchemaMigrationRequiredException"/> for an older one.
    /// </summary>
    Task EnsureSchemaCompatibleAsync(CancellationToken ct);

    /// <summary>
    /// The recorded schema version and compatibility floor (<c>min_binary_schema</c>). Never writes; works on databases
    /// whose <c>schema_version</c> predates the floor column by deriving it from <see cref="KnownMigrations"/>.
    /// </summary>
    Task<SchemaState> GetSchemaStateAsync(CancellationToken ct);

    /// <summary>Every migration this binary ships, in version order, with its additive/breaking tag.</summary>
    IReadOnlyList<SchemaMigration> KnownMigrations { get; }

    /// <summary>
    /// Aggregate counts for opt-in anonymous usage statistics: projects, distinct players across
    /// projects, and distinct players seen at or after <paramref name="activeSinceUnixMs"/>. Returns no identifiers.
    /// </summary>
    Task<StoreUsageCounts> CountUsageAsync(long activeSinceUnixMs, CancellationToken ct);
}

/// <summary>Outcome of <see cref="INetworkStorageStoreAdmin.PingAsync"/>.</summary>
public sealed record StorePingResult(TimeSpan RoundTrip, string ServerVersion);

/// <summary>Outcome of <see cref="INetworkStorageStoreAdmin.CountUsageAsync"/>.</summary>
public sealed record StoreUsageCounts(long Projects, long Players, long ActivePlayers);

/// <summary>Outcome of <see cref="INetworkStorageStoreAdmin.MigrateAsync"/>.</summary>
public sealed record SchemaMigrationResult(int FromVersion, int ToVersion, IReadOnlyList<int> AppliedVersions);

/// <summary>
/// Whether an older binary can keep running on the schema a migration produced.
/// <see cref="Additive"/>: the previous binaries' statements still work (new nullable columns, new tables, new indexes,
/// data that older code ignores). <see cref="Breaking"/>: anything else (dropped or renamed columns, changed types or
/// constraints, rewritten data older code misreads). When unsure, tag <see cref="Breaking"/>.
/// </summary>
public enum SchemaChange
{
    Additive,
    Breaking,
}

/// <summary>
/// One forward-only migration. <see cref="BuildSql"/> receives the table prefix (e.g. <c>"network_storage".</c>).
/// <see cref="Compat"/> is recorded in <c>schema_version</c> and decides which binaries may run after it.
/// </summary>
public sealed record SchemaMigration(int Version, string Description, SchemaChange Compat, Func<string, string> BuildSql);

/// <summary>
/// What <c>schema_version</c> says: the highest applied <see cref="Version"/> and <see cref="MinBinarySchema"/>, the highest
/// <see cref="SchemaChange.Breaking"/> version applied. A binary whose supported schema is at least
/// <see cref="MinBinarySchema"/> can run against this database even when <see cref="Version"/> is newer than it knows.
/// </summary>
public sealed record SchemaState(int Version, int MinBinarySchema)
{
    /// <summary>True when a binary supporting <paramref name="supportedVersion"/> must refuse to run on this database.</summary>
    public bool IsTooNewFor(int supportedVersion) => Version > supportedVersion && supportedVersion < MinBinarySchema;

    /// <summary>True when the database is newer than <paramref name="supportedVersion"/> but every later migration is additive.</summary>
    public bool IsNewerButCompatibleWith(int supportedVersion) => Version > supportedVersion && supportedVersion >= MinBinarySchema;
}

/// <summary>Pure rules shared by the stores, <c>doctor</c> and the rollback/downgrade checks.</summary>
public static class SchemaCompatibility
{
    /// <summary>The highest breaking migration at or below <paramref name="version"/>; 0 when there is none.</summary>
    public static int FloorAt(IEnumerable<SchemaMigration> migrations, int version)
        => migrations.Where(m => m.Version <= version && m.Compat == SchemaChange.Breaking).Select(m => m.Version).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Folds the <c>schema_version</c> rows into a <see cref="SchemaState"/>. A row without a recorded floor (written by a
    /// binary that predates it) takes the floor from <paramref name="known"/> when this binary knows the version, and is
    /// otherwise treated as breaking.
    /// </summary>
    public static SchemaState Resolve(IEnumerable<(int Version, int? MinBinarySchema)> rows, IReadOnlyList<SchemaMigration> known)
    {
        var version = 0;
        var floor = 0;
        foreach (var (rowVersion, recorded) in rows)
        {
            version = Math.Max(version, rowVersion);
            floor = Math.Max(floor, recorded ?? (known.Any(m => m.Version == rowVersion) ? FloorAt(known, rowVersion) : rowVersion));
        }

        return new SchemaState(version, floor);
    }
}

/// <summary>The database schema was written by a newer sbox-ns than the running binary, and not only additively.</summary>
public sealed class SchemaVersionTooNewException : InvalidOperationException
{
    public SchemaVersionTooNewException(int databaseVersion, int supportedVersion, int minBinarySchema = 0)
        : base($"The database schema is at version {databaseVersion}, but this sbox-ns build supports schema version {supportedVersion} at most. "
            + "Upgrade sbox-ns to a release that supports this schema (schema downgrades are not supported; restore a backup taken before the upgrade to roll back).")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
        MinBinarySchema = minBinarySchema;
    }

    public int DatabaseVersion { get; }
    public int SupportedVersion { get; }

    /// <summary>The oldest schema version a binary must support to run on this database; 0 when unknown.</summary>
    public int MinBinarySchema { get; }
}

/// <summary>The database schema is older than the running binary requires.</summary>
public sealed class SchemaMigrationRequiredException : InvalidOperationException
{
    public SchemaMigrationRequiredException(int databaseVersion, int supportedVersion)
        : base($"The database schema is at version {databaseVersion}, but this sbox-ns build requires schema version {supportedVersion}. Run `sbox-ns db migrate`.")
    {
        DatabaseVersion = databaseVersion;
        SupportedVersion = supportedVersion;
    }

    public int DatabaseVersion { get; }
    public int SupportedVersion { get; }
}
