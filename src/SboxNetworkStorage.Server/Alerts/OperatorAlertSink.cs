using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Monitoring;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// The built-in "captured errors" alert source. Registered as both <see cref="IExceptionAlertSink"/>
/// and <see cref="INetworkStorageErrorAlertSink"/> (one shared instance), so every existing call site
/// (endpoint error reporter, proxy reporter, handler-caught reporter, storage-API error reports)
/// alerts without changes.
///
/// <para>The log sink runs first and stays the always-on record. Some alerts can be triggered by
/// anyone holding a game's public key, so remote sends pass the shared <see cref="AlertThrottle"/>
/// (also used by alert rules) and then go to every channel that accepts the <c>error</c> severity
/// through the <see cref="AlertDispatcher"/>.</para>
/// </summary>
public sealed class OperatorAlertSink(
    LoggingExceptionAlertSink logSink,
    LoggingNetworkStorageErrorAlertSink storageLogSink,
    AlertThrottle throttle,
    AlertDispatcher dispatcher,
    MonitoringStore store,
    TimeProvider time) : IExceptionAlertSink, INetworkStorageErrorAlertSink
{
    public async Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
    {
        await logSink.NotifyAsync(capturedError, cancellationToken);
        var fingerprint = $"exception\n{capturedError.ProjectId}\n{capturedError.Classification}\n{capturedError.Method}\n{capturedError.Path}";
        if (!throttle.TryAdmit(fingerprint, out var note)) return;
        await SendAsync(AlertMessage.FromCaptured(capturedError).WithNote(note), cancellationToken);
    }

    public async Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken)
    {
        await storageLogSink.NotifyAsync(error, cancellationToken);
        // The record key is left out: one broken collection fails for every player.
        var fingerprint = $"storage\n{error.ProjectId}\n{error.Operation}\n{error.Code}\n{error.CollectionId}";
        if (!throttle.TryAdmit(fingerprint, out var note)) return;
        await SendAsync(AlertMessage.FromStorage(error, time.GetUtcNow()).WithNote(note), cancellationToken);
    }

    private async Task SendAsync(AlertMessage alert, CancellationToken cancellationToken)
    {
        var deliveries = await dispatcher.SendAsync(alert, channelIds: null, cancellationToken);
        if (deliveries.Count == 0) return;
        var failed = deliveries.Where(d => !d.Ok).Select(d => d.ChannelName).ToList();
        store.Append(new MonitoringEvent(time.GetUtcNow(), "alert", alert.Title,
            failed.Count == 0 ? $"Sent to {deliveries.Count} channel(s)." : $"Failed for {string.Join(", ", failed)}."));
    }
}
