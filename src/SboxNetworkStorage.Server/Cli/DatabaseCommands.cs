using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

public static class DatabaseCommands
{
    public static async Task<int> RunAsync(CliContext context)
    {
        var sub = context.RequirePositional(1, "test|status|migrate|backup|restore");
        var config = context.LoadValidConfig();
        switch (sub)
        {
            case "backup":
            {
                var path = await DatabaseBackup.BackupAsync(config, context.Args.Option("output"), CancellationToken.None);
                Console.WriteLine($"Backup written to {path}");
                return CliApp.Ok;
            }
            case "restore":
            {
                var path = context.RequirePositional(2, "backup file");
                Console.WriteLine("Stop the server before restoring (sbox-ns service stop).");
                await DatabaseBackup.RestoreAsync(config, path, CancellationToken.None);
                Console.WriteLine($"Restored {path}. Start the server again: sbox-ns service start");
                return CliApp.Ok;
            }
        }

        await using var services = CliServices.Build(config);
        var admin = services.GetRequiredService<INetworkStorageStoreAdmin>();
        switch (sub)
        {
            case "test":
            {
                try
                {
                    var ping = await admin.PingAsync(CancellationToken.None);
                    Console.WriteLine($"OK: connected to {admin.ProviderName} at {admin.RedactedTarget} ({ping.ServerVersion}, {ping.RoundTrip.TotalMilliseconds:F0} ms)");
                    return CliApp.Ok;
                }
                catch (Exception ex)
                {
                    throw new CliException($"cannot connect to {admin.ProviderName} at {admin.RedactedTarget}: {ex.Message}");
                }
            }
            case "status":
            {
                var state = await admin.GetSchemaStateAsync(CancellationToken.None);
                var version = state.Version;
                var supported = admin.SupportedSchemaVersion;
                if (context.Args.Flag("json"))
                {
                    // Read by the update/rollback checks (SchemaRollback.TryParseState); the verdict is the caller's.
                    Console.WriteLine(new System.Text.Json.Nodes.JsonObject
                    {
                        ["provider"] = admin.ProviderName,
                        ["schemaVersion"] = version,
                        ["minBinarySchema"] = state.MinBinarySchema,
                        ["supportedSchemaVersion"] = supported,
                    }.ToJsonString());
                    return CliApp.Ok;
                }

                Console.WriteLine($"Database:        {admin.ProviderName} at {admin.RedactedTarget}");
                Console.WriteLine($"Schema version:  {version} (this build supports {supported})");
                Console.WriteLine($"Compat floor:    {state.MinBinarySchema} (min_binary_schema: the oldest schema version a binary must support to run on this database)");
                Console.WriteLine(version switch
                {
                    _ when version == supported => "Status:          up to date",
                    _ when version < supported => "Status:          migration pending (sbox-ns db migrate, or start the server)",
                    _ when state.IsNewerButCompatibleWith(supported) => "Status:          database is newer than this binary, but only through additive migrations; this binary can run on it",
                    _ => "Status:          database is newer than this binary; upgrade sbox-ns"
                });
                return state.IsTooNewFor(supported) ? CliApp.Failure : CliApp.Ok;
            }
            case "migrate":
            {
                var result = await admin.MigrateAsync(CancellationToken.None);
                Console.WriteLine(result.AppliedVersions.Count == 0
                    ? $"Schema already at version {result.ToVersion}."
                    : $"Migrated schema from version {result.FromVersion} to {result.ToVersion}.");
                return CliApp.Ok;
            }
            default:
                throw new CliException($"unknown db command '{sub}'", CliApp.Usage);
        }
    }
}
