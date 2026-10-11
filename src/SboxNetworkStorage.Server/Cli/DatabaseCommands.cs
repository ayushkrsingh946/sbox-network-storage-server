using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage.Relational;
using SboxNetworkStorage.Server.Hosting;
using System.Text.Json;

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
                var compatibility = await admin.GetSchemaCompatibilityAsync(CancellationToken.None);
                Console.WriteLine($"Database:        {admin.ProviderName} at {admin.RedactedTarget}");
                Console.WriteLine($"Schema version:  {compatibility.DatabaseVersion} (this build supports {admin.SupportedSchemaVersion})");
                Console.WriteLine($"Compatibility floor: {compatibility.MinimumBinarySchema?.ToString() ?? "unknown"}");
                Console.WriteLine(compatibility.MigrationRequired ? "Status:          migration pending (sbox-ns db migrate, or start the server)"
                    : compatibility.Compatible ? "Status:          compatible (newer additive schemas remain usable)"
                    : "Status:          database is incompatible with this binary; upgrade sbox-ns or restore a backup");
                return compatibility.Compatible || compatibility.MigrationRequired ? CliApp.Ok : CliApp.Failure;
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

    public static async Task<int> CheckCompatibilityAsync(CliContext context)
    {
        // Dedicated top-level command: older binaries reject it before loading configuration or generating secrets.
        var config = context.LoadValidConfig();
        await using var probe = new ServiceCollection().AddConfiguredStore(config).BuildServiceProvider();
        var compatibility = await probe.GetRequiredService<INetworkStorageStoreAdmin>().GetSchemaCompatibilityAsync(CancellationToken.None);
        if (context.Args.Flag("json"))
            Console.WriteLine(JsonSerializer.Serialize(new { protocol = 1, version = BuildInfo.Version, compatibility.DatabaseVersion,
                compatibility.SupportedVersion, compatibility.MinimumBinarySchema, compatibility.Compatible }));
        else
            Console.WriteLine($"Schema {compatibility.DatabaseVersion}; binary supports {compatibility.SupportedVersion}; certified floor {compatibility.MinimumBinarySchema?.ToString() ?? "unknown"}; compatible: {compatibility.Compatible}.");
        return compatibility.Compatible ? CliApp.Ok : CliApp.Failure;
    }
}
