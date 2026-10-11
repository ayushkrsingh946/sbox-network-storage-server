using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Compatibility;

public sealed class ClientVersionWriter(ClientVersionTracker tracker, IClientVersionStore store, LibraryCompatibility compatibility,
    IServiceScopeFactory scopes, TimeProvider time, ILogger<ClientVersionWriter> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var batch = tracker.Drain();
            if (batch.Count > 0) await store.WriteClientVersionsAsync(batch, ct);
            // Aggregate alert threshold is per-project; not a per-request network call.
            foreach (var project in batch.Select(delta => delta.Key.ProjectId).Distinct(StringComparer.Ordinal))
            {
                await using var scope = scopes.CreateAsyncScope();
                var projects = scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>();
                var access = await projects.ResolveProjectAccessAsync(NetworkStorageServices.LocalOwnerUserId, project, ct);
                if (access?.Project.LibraryAlertPercent is not { } threshold || threshold <= 0) continue;
                var total = batch.Where(delta => delta.Key.ProjectId == project).Sum(delta => delta.Requests);
                var affected = batch.Where(delta => delta.Key.ProjectId == project && (compatibility.StatusFor(delta.Key.LibraryVersion) is "unsupported" or "blocked"
                    || ClientVersionsQuery.BelowMinimum(delta.Key.LibraryVersion, access.Project.LibraryMinimum))).Sum(delta => delta.Requests);
                if (total >= 100 && affected * 100d / total >= threshold)
                    await scope.ServiceProvider.GetRequiredService<INetworkStorageErrorAlertSink>().NotifyAsync(
                        new NetworkStorageError(project, "", "", "library-compatibility", "LIBRARY_TRAFFIC",
                            $"{affected}/{total} requests ({affected * 100d / total:0.0}%) use an unsupported or blocked library. See project Clients."), ct);
            }
        }
        finally { _gate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        var lastPurgeDay = -1;
        var reportedDrops = 0L;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await FlushAsync(stoppingToken);
                    var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).DayNumber;
                    if (day != lastPurgeDay)
                    {
                        await store.PurgeClientVersionsAsync(day - 89, stoppingToken);
                        lastPurgeDay = day;
                    }
                    var dropped = tracker.Dropped;
                    if (dropped > reportedDrops)
                    {
                        logger.LogWarning("Client version analytics dropped {Count} additional requests at the daily cardinality bound", dropped - reportedDrops);
                        reportedDrops = dropped;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Client version analytics flush failed; unpersisted batch was dropped"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await FlushAsync(cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Final client version analytics flush failed"); }
    }
}
