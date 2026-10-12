using System.Text.Json;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>
/// Generated module/integrity/insights fixture for the public demo (isolated
/// SQLite database, demo-player-001..006). Idempotent: fixed ids make re-runs
/// no-ops. Only writes clearly generated sample rows; never touches secrets.
/// </summary>
public static class ModuleDemoSeeder
{
    public static async Task SeedAsync(ModuleInstaller installer, IModuleDataStore modules,
        INetworkStorageStore store, long ownerUserId, string projectId,
        DateTimeOffset timestamp, CancellationToken ct)
    {
        foreach (var moduleId in new[] { "economy", "progression", "inventory", "leaderboards", "integrity", "player-insights" })
            await installer.InstallAsync(ownerUserId, projectId, moduleId, settings: null, ct);

        var now = timestamp.ToUnixTimeMilliseconds();
        string[] names = ["Demo Scout", "Demo Builder", "Demo Miner", "Demo Ranger", "Demo Farmer", "Demo Sailor"];
        for (var i = 0; i < names.Length; i++)
        {
            var player = $"demo-player-{i + 1:000}";
            for (var day = 0; day < 3; day++)
            {
                var started = now - day * 86_400_000L - i * 900_000L;
                await store.InsertPlayerSessionAsync(projectId, player, $"demo-session-{i}-{day}",
                    started, started + 1_800_000L, started + 3_600_000L,
                    "{\"sampleData\":true}", "{\"sampleData\":true}", ct);
            }
            await modules.InsertActionLogAsync(projectId, player, now - i * 600_000L, $"demo-log-{i}",
                "economy_earn", 42, "ok",
                JsonSerializer.SerializeToElement(new[] { "gold" }), null, ct);
        }

        await modules.InsertFlagAsync(projectId, "demo-flag-delta", now - 3_600_000L, "demo-player-002",
            IntegrityDetectors.ValueDelta, "high", "open", null,
            JsonSerializer.SerializeToElement(new
            {
                field = "gold", observedDelta = 8750.0, limitPerMinute = 5000.0,
                endpoint = "economy_earn", sampleData = true,
            }), ct);
        await modules.InsertFlagAsync(projectId, "demo-flag-rate", now - 7_200_000L, "demo-player-005",
            IntegrityDetectors.CallRate, "medium", "open", null,
            JsonSerializer.SerializeToElement(new
            {
                callsPerMinute = 121, limitPerMinute = 120,
                endpoint = "progression_grant_xp", sampleData = true,
            }), ct);
        await modules.UpsertBlockAsync(projectId, "demo-player-006", PlayerBlockService.WritesScope,
            "Sample write block from the demo dataset.", now - 1_800_000L, now + 22 * 3_600_000L,
            "demo-fixture", ct);
    }
}
