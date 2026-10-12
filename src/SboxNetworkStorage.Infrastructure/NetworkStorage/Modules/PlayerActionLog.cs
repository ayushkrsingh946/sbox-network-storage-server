using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>Effective per-project insights settings (catalog defaults overlaid with saved values).</summary>
public sealed record InsightsSettings(
    int ActionLogRetentionDays,
    bool CaptureBodies,
    IReadOnlySet<string> BodyEndpoints,
    IReadOnlySet<string> Permissions)
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 90;
    public const int DefaultRetentionDays = 14;

    public static InsightsSettings From(IReadOnlyDictionary<string, string> saved, IReadOnlyList<string> permissions)
    {
        var retention = DefaultRetentionDays;
        if (saved.TryGetValue("action_log_retention_days", out var raw) && int.TryParse(raw, out var days))
            retention = Math.Clamp(days, MinRetentionDays, MaxRetentionDays);
        var capture = saved.TryGetValue("capture_bodies", out var captureRaw)
            && captureRaw.Equals("true", StringComparison.OrdinalIgnoreCase);
        var endpoints = saved.TryGetValue("body_endpoints", out var list) && !string.IsNullOrWhiteSpace(list)
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new InsightsSettings(retention, capture,
            new HashSet<string>(endpoints, StringComparer.Ordinal),
            new HashSet<string>(permissions, StringComparer.Ordinal));
    }
}

/// <summary>Per-project insights settings with a short cache, read off the request path.</summary>
public sealed class InsightsSettingsProvider(
    IServiceScopeFactory scopes,
    ModuleOwnerOptions owner,
    IMemoryCache cache,
    ILogger<InsightsSettingsProvider> logger)
{
    // Singleton (hook drain must not depend on scoped services): workspace
    // access goes through a scope, amortized by the 30-second settings cache.
    public async Task<InsightsSettings?> ForProjectAsync(string projectId, CancellationToken ct)
    {
        var cacheKey = $"insights-settings:{projectId}";
        if (cache.TryGetValue(cacheKey, out InsightsSettings? cached)) return cached;
        InsightsSettings? settings = null;
        try
        {
            using var scope = scopes.CreateScope();
            var states = scope.ServiceProvider.GetRequiredService<ModuleStateStore>();
            var state = await states.LoadAsync(owner.OwnerUserId, projectId, ct);
            var installed = state.Modules.FirstOrDefault(module => module.Id == "player-insights" && module.Enabled);
            if (installed is not null)
                settings = InsightsSettings.From(installed.Settings, installed.Permissions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading insights settings failed for project {ProjectId}", projectId);
        }
        cache.Set(cacheKey, settings, TimeSpan.FromSeconds(30));
        return settings;
    }

    public void Invalidate(string projectId) => cache.Remove($"insights-settings:{projectId}");
}

/// <summary>One batched action-log entry.</summary>
public sealed record PlayerActionLogEntry(
    string ProjectId,
    string SteamId,
    long CreatedAtUnixMs,
    string EntryId,
    string? EndpointSlug,
    long? RevisionId,
    string Outcome,
    JsonElement? ChangedFields,
    JsonElement? Body);

/// <summary>
/// Batched player action log writer. Request threads only enqueue; a background
/// service batches rows into the store. When the bounded queue is full, entries
/// are dropped and counted per project — game requests still succeed and the
/// panel shows the dropped count for the period.
/// </summary>
public sealed class PlayerActionLogWriter
{
    public const int Capacity = 4096;
    public const int BatchSize = 100;
    // Wait mode with TryWrite-only enqueue: a full queue refuses the entry
    // (counted per project) instead of ever blocking or failing requests.
    private readonly Channel<PlayerActionLogEntry> _channel =
        Channel.CreateBounded<PlayerActionLogEntry>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    private readonly ConcurrentDictionary<string, long> _droppedByProject = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastRetentionByProject = new(StringComparer.Ordinal);
    private readonly IModuleDataStore _modules;
    private readonly InsightsSettingsProvider _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayerActionLogWriter> _logger;

    public PlayerActionLogWriter(IModuleDataStore modules, InsightsSettingsProvider settings,
        TimeProvider time, ILogger<PlayerActionLogWriter> logger)
    {
        _modules = modules;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    internal ChannelReader<PlayerActionLogEntry> Reader => _channel.Reader;

    /// <summary>Enqueues an entry; false when the queue was full (dropped and counted).</summary>
    public bool TryEnqueue(PlayerActionLogEntry entry)
    {
        if (_channel.Writer.TryWrite(entry)) return true;
        _droppedByProject.AddOrUpdate(entry.ProjectId, 1, (_, count) => count + 1);
        return false;
    }

    public long DroppedFor(string projectId)
        => _droppedByProject.TryGetValue(projectId, out var count) ? count : 0;

    internal async Task DrainAsync(CancellationToken ct)
    {
        var batch = new List<PlayerActionLogEntry>(BatchSize);
        await foreach (var entry in _channel.Reader.ReadAllAsync(ct))
        {
            batch.Add(entry);
            while (batch.Count < BatchSize && _channel.Reader.TryRead(out var next)) batch.Add(next);
            await FlushAsync(batch, ct);
            batch.Clear();
        }
    }

    internal async Task FlushAsync(IReadOnlyList<PlayerActionLogEntry> batch, CancellationToken ct)
    {
        foreach (var entry in batch)
        {
            try
            {
                await _modules.InsertActionLogAsync(entry.ProjectId, entry.SteamId, entry.CreatedAtUnixMs,
                    entry.EntryId, entry.EndpointSlug, entry.RevisionId, entry.Outcome,
                    entry.ChangedFields, entry.Body, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Writing player action log failed for project {ProjectId}", entry.ProjectId);
            }
        }
        foreach (var projectId in batch.Select(entry => entry.ProjectId).Distinct(StringComparer.Ordinal))
            await RunRetentionAsync(projectId, ct);
    }

    internal async Task RunRetentionAsync(string projectId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var last = _lastRetentionByProject.GetOrAdd(projectId, 0);
        if (now - last < TimeSpan.FromHours(1).TotalMilliseconds) return;
        _lastRetentionByProject[projectId] = now;
        try
        {
            var settings = await _settings.ForProjectAsync(projectId, ct);
            var retentionDays = settings?.ActionLogRetentionDays ?? InsightsSettings.DefaultRetentionDays;
            var cutoff = _time.GetUtcNow().AddDays(-retentionDays).ToUnixTimeMilliseconds();
            await _modules.PurgeProjectActionLogAsync(projectId, cutoff, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Player action log retention failed for project {ProjectId}", projectId);
        }
    }
}

/// <summary>Background drain for <see cref="PlayerActionLogWriter"/>.</summary>
public sealed class PlayerActionLogService(PlayerActionLogWriter writer, ILogger<PlayerActionLogService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await writer.DrainAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Player action log drain failed.");
        }
    }
}

/// <summary>
/// Records endpoint calls into the per-player action log for projects where
/// the insights module is installed and enabled. Request bodies are stored
/// only when the owner enables capture for the endpoint.
/// </summary>
public sealed class InsightsActionLogSubscriber(
    PlayerActionLogWriter writer,
    InsightsSettingsProvider settings,
    TimeProvider time) : IModuleHookSubscriber
{
    public IReadOnlyList<string> Hooks => ["endpoint.executed"];

    public async Task OnEventAsync(ModuleHookEvent hookEvent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hookEvent.SteamId)) return;
        var config = await settings.ForProjectAsync(hookEvent.ProjectId, ct);
        if (config is null) return;
        JsonElement? changed = null;
        if (hookEvent.Data.TryGetValue(ModuleHookEventData.ChangedFields, out var fields)
            && fields is IReadOnlyList<string> list && list.Count > 0)
            changed = JsonSerializer.SerializeToElement(list);
        JsonElement? body = null;
        if (config.CaptureBodies && hookEvent.EndpointSlug is not null
            && config.BodyEndpoints.Contains(hookEvent.EndpointSlug)
            && hookEvent.Data.TryGetValue(ModuleHookEventData.Body, out var rawBody) && rawBody is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            try { body = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { body = JsonSerializer.SerializeToElement(text); }
        }
        var outcome = hookEvent.Data.TryGetValue(ModuleHookEventData.Outcome, out var outcomeObj)
            ? outcomeObj?.ToString() : "ok";
        writer.TryEnqueue(new PlayerActionLogEntry(
            hookEvent.ProjectId, hookEvent.SteamId, hookEvent.At.ToUnixTimeMilliseconds(),
            $"log_{Guid.NewGuid():N}"[..20], hookEvent.EndpointSlug, hookEvent.RevisionId,
            outcome ?? "ok", changed, body));
    }
}
