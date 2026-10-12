using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>A hook event published off the request path.</summary>
public sealed record ModuleHookEvent(
    string Hook,
    string ProjectId,
    string? SteamId,
    string? EndpointSlug,
    long? RevisionId,
    bool HasSecretKey,
    DateTimeOffset At,
    IReadOnlyDictionary<string, object?> Data);

/// <summary>Subscribes to hooks; called on a background thread, never on the request path.</summary>
public interface IModuleHookSubscriber
{
    IReadOnlyList<string> Hooks { get; }
    Task OnEventAsync(ModuleHookEvent hookEvent, CancellationToken ct);
}
/// Detection work happens in <see cref="ModuleHookService"/>, off the request path.
/// </summary>
public interface IModuleHookEmitter
{
    bool TryEmit(ModuleHookEvent hookEvent);
    long DroppedEvents { get; }
}

public sealed class ModuleHookDispatcher : IModuleHookEmitter
{
    public const int Capacity = 4096;

    // Wait mode with TryWrite-only emission: a full channel refuses the write
    // (counted as dropped) instead of ever blocking the request path.
    private readonly Channel<ModuleHookEvent> _channel =
        Channel.CreateBounded<ModuleHookEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    private long _dropped;

    public long DroppedEvents => Interlocked.Read(ref _dropped);

    internal ChannelReader<ModuleHookEvent> Reader => _channel.Reader;

    public bool TryEmit(ModuleHookEvent hookEvent)
    {
        if (_channel.Writer.TryWrite(hookEvent)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public static ModuleHookEvent Describe(string hook, string projectId, DateTimeOffset at,
        string? steamId = null, string? endpointSlug = null, long? revisionId = null,
        bool hasSecretKey = false, IReadOnlyDictionary<string, object?>? data = null)
        => new(hook, projectId, steamId, endpointSlug, revisionId, hasSecretKey, at,
            data ?? new Dictionary<string, object?>(StringComparer.Ordinal));
}

/// <summary>
/// Drains the hook channel and fans events out to subscribers. One slow
/// subscriber never delays game requests (they already returned) and never
/// stops the others: each subscriber call is exception-isolated.
/// </summary>
public sealed class ModuleHookService(
    ModuleHookDispatcher dispatcher,
    IEnumerable<IModuleHookSubscriber> subscribers,
    ILogger<ModuleHookService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var byHook = subscribers.SelectMany(subscriber => subscriber.Hooks.Select(hook => (hook, subscriber)))
            .GroupBy(pair => pair.hook, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.subscriber).ToList(), StringComparer.Ordinal);
        await foreach (var hookEvent in dispatcher.Reader.ReadAllAsync(stoppingToken))
        {
            if (!byHook.TryGetValue(hookEvent.Hook, out var targets)) continue;
            foreach (var target in targets)
            {
                try
                {
                    await target.OnEventAsync(hookEvent, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Module hook subscriber failed for hook {Hook}", hookEvent.Hook);
                }
            }
        }
    }
}
