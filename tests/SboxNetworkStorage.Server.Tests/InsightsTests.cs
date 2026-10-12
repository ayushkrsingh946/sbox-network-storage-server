using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Player insights: overview math, settings, CSV export, log drops and retention.</summary>
public sealed class InsightsTests
{
    private const long DayMs = 86_400_000L;
    private const long Now = 1_760_000_000_000L;

    private static SessionSample Session(string player, long daysAgo, long seconds = 1800)
        => new(player, Now - daysAgo * DayMs - 3600_000L, Now - daysAgo * DayMs - 3600_000L + seconds * 1000, Now - daysAgo * DayMs - 3600_000L + seconds * 1000);

    [Fact]
    public void Overview_reports_windows_medians_and_return_rates()
    {
        var sessions = new List<SessionSample>();
        for (var day = 9; day >= 0; day--) sessions.Add(Session("a", day));
        sessions.Add(Session("b", 10));
        sessions.Add(Session("b", 0));
        var overview = InsightsCalculator.Summarize(sessions, Now, droppedLogEntries: 0);
        var one = overview.Windows.First(window => window.WindowDays == 1);
        Assert.Equal(2, one.ActivePlayers);
        Assert.Equal(0, one.NewPlayers);
        Assert.Equal(2, one.Sessions);
        Assert.Equal(1800, one.MedianSessionSeconds);
        var thirty = overview.Windows.First(window => window.WindowDays == 30);
        Assert.Equal(2, thirty.ActivePlayers);
        Assert.Equal(2, thirty.NewPlayers);
        Assert.Equal(12, thirty.Sessions);
        Assert.Equal(0.5, overview.Day1ReturnRate);
        Assert.Equal(0.5, overview.Day7ReturnRate);
        Assert.True(overview.Day7EnoughData);
    }

    [Fact]
    public void Overview_reports_insufficient_data_instead_of_placeholders()
    {
        var empty = InsightsCalculator.Summarize([], Now, 0);
        Assert.All(empty.Windows, window => Assert.False(window.EnoughData));
        Assert.Null(empty.Day1ReturnRate);
        Assert.Null(empty.Day7ReturnRate);
        Assert.False(empty.Day7EnoughData);

        var fresh = InsightsCalculator.Summarize([Session("a", 0), Session("b", 1)], Now, 0);
        Assert.Null(fresh.Day7ReturnRate);
        Assert.False(fresh.Day7EnoughData);
    }

    [Fact]
    public void Settings_clamp_retention_and_parse_body_endpoints()
    {
        var defaults = InsightsSettings.From(new Dictionary<string, string>(), []);
        Assert.Equal(14, defaults.ActionLogRetentionDays);
        Assert.False(defaults.CaptureBodies);
        var clamped = InsightsSettings.From(new Dictionary<string, string>
        {
            ["action_log_retention_days"] = "500",
            ["capture_bodies"] = "true",
            ["body_endpoints"] = "economy_earn, progression_grant_xp",
        }, []);
        Assert.Equal(90, clamped.ActionLogRetentionDays);
        Assert.True(clamped.CaptureBodies);
        Assert.Contains("economy_earn", clamped.BodyEndpoints);
    }

    [Fact]
    public void Csv_rows_equal_the_filtered_results_with_escaping()
    {
        var rows = new[]
        {
            new ActionLogRow(Now, "p1", "economy_earn", 42, "ok", "[\"gold\"]"),
            new ActionLogRow(Now, "p2", "weird,endpoint", null, "ok", null),
        };
        var csv = InsightsService.ToCsv(rows);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("created_at,steam_id,endpoint_slug,revision_id,outcome,changed_fields", lines[0]);
        Assert.Contains("p1", lines[1]);
        Assert.Contains("\"weird,endpoint\"", lines[2]);
    }

    [Fact]
    public void Full_action_log_queue_drops_and_counts_without_failing_requests()
    {
        var modules = new InMemoryModuleDataStore();
        var writer = new PlayerActionLogWriter(modules, Provider(), TimeProvider.System,
            NullLogger<PlayerActionLogWriter>.Instance);
        for (var i = 0; i < PlayerActionLogWriter.Capacity + 5; i++)
        {
            writer.TryEnqueue(new PlayerActionLogEntry("game", "p1", Now, $"log_{i}",
                "economy_earn", 42, "ok", null, null));
        }
        Assert.Equal(5, writer.DroppedFor("game"));
        Assert.Equal(0, writer.DroppedFor("other"));
    }

    [Fact]
    public async Task Retention_deletes_only_expired_rows_of_the_project()
    {
        var modules = new InMemoryModuleDataStore();
        var writer = new PlayerActionLogWriter(modules, Provider(), TimeProvider.System,
            NullLogger<PlayerActionLogWriter>.Instance);
        var realNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var oldMs = realNow - 20 * DayMs;
        await modules.InsertActionLogAsync("game", "p1", oldMs, "old", "economy_earn", 42, "ok", null, null, default);
        await modules.InsertActionLogAsync("game", "p1", realNow, "fresh", "economy_earn", 42, "ok", null, null, default);
        await modules.InsertActionLogAsync("other", "p9", oldMs, "old-other", "economy_earn", 42, "ok", null, null, default);
        await writer.RunRetentionAsync("game", default);
        Assert.Empty(await modules.ListActionLogAsync("game", "p1", null, null, null, oldMs + 1, 10, default));
        Assert.NotEmpty(await modules.ListActionLogAsync("game", null, null, null, null, null, 10, default));
        Assert.NotEmpty(await modules.ListActionLogAsync("other", null, null, null, null, null, 10, default));
    }

    [Fact]
    public async Task Unknown_players_have_no_profile_and_no_search_hits()
    {
        var service = new InsightsService(new InMemoryNetworkStorageStore(), new InMemoryModuleDataStore(),
            new PlayerActionLogWriter(new InMemoryModuleDataStore(), Provider(), TimeProvider.System,
                NullLogger<PlayerActionLogWriter>.Instance), TimeProvider.System);
        Assert.Null(await service.ProfileAsync("game", "ghost", default));
        Assert.Empty(await service.SearchAsync("game", "ghost", default));
        Assert.Null(await service.OverviewAsync("game", default));
    }

    private static InsightsSettingsProvider Provider()
        => new(null!, new ModuleOwnerOptions(1),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<InsightsSettingsProvider>.Instance);
}
