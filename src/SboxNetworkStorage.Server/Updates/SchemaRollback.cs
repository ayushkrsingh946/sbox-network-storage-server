using System.Text.Json;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>
/// What a binary says about itself through <c>sbox-ns schema-info --json</c>: the highest schema it can run on.
/// Binaries released before rollback-safe migrations have no such command, so a missing answer means "needs the exact schema".
/// </summary>
public sealed record BinarySchemaInfo(string Version, int SupportedSchemaVersion)
{
    /// <summary>This binary. SQLite and PostgreSQL share one numbering (a conformance test pins it).</summary>
    public static BinarySchemaInfo ForThisBuild()
        => new(BuildInfo.Version, SqliteMigrations.All.Max(m => m.Version));

    public string ToJson() => new System.Text.Json.Nodes.JsonObject
    {
        ["version"] = Version,
        ["supportedSchemaVersion"] = SupportedSchemaVersion,
        ["compat"] = SchemaCompatibilityMode,
    }.ToJsonString();

    /// <summary>Null for anything that is not a <c>schema-info --json</c> answer (an older binary prints an error instead).</summary>
    public static BinarySchemaInfo? TryParse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("supportedSchemaVersion", out var supported) || !supported.TryGetInt32(out var schema)
                || !root.TryGetProperty("compat", out var compat) || compat.GetString() != SchemaCompatibilityMode)
                return null;
            return new BinarySchemaInfo(root.TryGetProperty("version", out var version) ? version.GetString() ?? "unknown" : "unknown", schema);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Marks a binary that applies the <c>min_binary_schema</c> floor rule at startup.</summary>
    public const string SchemaCompatibilityMode = "floor";

}

/// <summary>Outcome of the data-preserving rollback or downgrade check.</summary>
public sealed record RollbackDecision(bool Safe, string Reason);

/// <summary>
/// The rule behind <c>sbox-ns rollback --keep-data</c> and <c>sbox-ns update --version X --allow-downgrade</c>:
/// the target binary can run on the existing database exactly when its supported schema is at least the database's
/// <c>min_binary_schema</c> (or the database is not newer than it at all).
/// </summary>
public static class SchemaRollback
{
    public static RollbackDecision Evaluate(SchemaState database, BinarySchemaInfo? target, IReadOnlyList<SchemaMigration> known)
    {
        if (target is null)
        {
            return new RollbackDecision(false,
                "the target binary predates rollback-safe migrations (it does not answer `schema-info`), so it only runs on the exact schema it shipped with");
        }

        if (database.Version <= target.SupportedSchemaVersion)
        {
            return new RollbackDecision(true,
                $"database schema {database.Version} is not newer than {target.Version}'s schema {target.SupportedSchemaVersion}; nothing to undo");
        }

        if (database.MinBinarySchema <= target.SupportedSchemaVersion)
        {
            var since = known.Where(m => m.Version > target.SupportedSchemaVersion && m.Version <= database.Version).Select(m => $"v{m.Version}").ToList();
            return new RollbackDecision(true,
                $"database schema {database.Version} is newer than {target.Version}'s schema {target.SupportedSchemaVersion}, but every migration since"
                + (since.Count > 0 ? $" ({string.Join(", ", since)})" : string.Empty)
                + $" is additive (min_binary_schema {database.MinBinarySchema})");
        }

        var breaking = known.FirstOrDefault(m => m.Version == database.MinBinarySchema);
        var what = breaking is null ? $"migration v{database.MinBinarySchema}" : $"migration v{breaking.Version} (\"{breaking.Description}\")";
        return new RollbackDecision(false,
            $"{what} is breaking: a binary must support schema {database.MinBinarySchema} or newer to run on this database, "
            + $"but {target.Version} supports {target.SupportedSchemaVersion} (database schema {database.Version})");
    }

    /// <summary>
    /// Checks every instance's database against the target binary. Returns the first unsafe decision, or a safe one
    /// summarising the databases. Each database is read by the installed binary as the instance's service user.
    /// </summary>
    public static Task<RollbackDecision> CheckAsync(IUpdateHost host, string installedBinary, BinarySchemaInfo? target,
        IReadOnlyList<ServerInstance> instances, IReadOnlyList<SchemaMigration> known, CancellationToken ct)
        => CheckAsync(host, installedBinary, target, instances, _ => known, ct);

    /// <summary>
    /// Checks every instance's database against the target binary. Returns the first unsafe decision, or a safe one
    /// summarising the databases. Each database is read by the installed binary as the instance's service user.
    /// <paramref name="knownFor"/> resolves the migration list per instance, so mixed SQLite/PostgreSQL hosts
    /// name the right migrations in their reasons.
    /// </summary>
    public static async Task<RollbackDecision> CheckAsync(IUpdateHost host, string installedBinary, BinarySchemaInfo? target,
        IReadOnlyList<ServerInstance> instances, Func<ServerInstance, IReadOnlyList<SchemaMigration>> knownFor, CancellationToken ct)
    {
        var reasons = new List<string>();
        foreach (var instance in instances)
        {
            var state = await host.ReadSchemaStateAsync(installedBinary, instance, ct);
            if (state is null)
            {
                return new RollbackDecision(false, $"{instance.Name}: could not read the database schema state");
            }

            var decision = Evaluate(state, target, knownFor(instance));
            if (!decision.Safe)
            {
                return decision with { Reason = instances.Count > 1 ? $"{instance.Name}: {decision.Reason}" : decision.Reason };
            }

            reasons.Add(instances.Count > 1 ? $"{instance.Name}: {decision.Reason}" : decision.Reason);
        }

        return new RollbackDecision(true, string.Join("; ", reasons));
    }

    /// <summary>
    /// The migration list the floor-rule reasons are read from: the instance's configured provider,
    /// never a hardcoded driver. Only the human-readable part of the verdict uses it; the decision
    /// itself comes from the database's recorded <c>min_binary_schema</c>.
    /// </summary>
    public static IReadOnlyList<SchemaMigration> KnownMigrationsFor(ServerInstance instance)
        => string.Equals(instance.Config.GetString("database.provider"), "postgres", StringComparison.OrdinalIgnoreCase)
            ? PostgresMigrations.All
            : SqliteMigrations.All;

    /// <summary>
    /// The single data-preserving gate behind <c>rollback --keep-data</c> and downgrades.
    /// The floor rule decides (the target's supported schema against each database's
    /// <c>min_binary_schema</c>), and the target binary itself is asked through its non-mutating
    /// <c>compatibility</c> probe, so the target's own migration knowledge is used. Either side
    /// refusing stops everything before anything is stopped or swapped.
    /// </summary>
    public static async Task<RollbackDecision> CheckDataPreservingAsync(IUpdateHost host, string installedBinary, string targetBinary,
        IReadOnlyList<ServerInstance> instances, CancellationToken ct)
    {
        var target = await host.ProbeSchemaInfoAsync(targetBinary, ct);
        var floor = await CheckAsync(host, installedBinary, target, instances, KnownMigrationsFor, ct);
        if (!floor.Safe)
        {
            return floor;
        }

        foreach (var instance in instances)
        {
            if (await host.CheckCompatibilityAsync(targetBinary, installedBinary, instance, ct) != 0)
            {
                var prefix = instances.Count > 1 ? $"{instance.Name}: " : string.Empty;
                return new RollbackDecision(false,
                    $"{prefix}the target binary's compatibility probe failed, so it cannot run on this database without a restore; nothing was changed");
            }
        }

        return floor;
    }

    /// <summary>Parses the <c>db status --json</c> line.</summary>
    public static SchemaState? TryParseState(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var v)
                && root.TryGetProperty("minBinarySchema", out var floor) && floor.TryGetInt32(out var f)
                    ? new SchemaState(v, f)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
