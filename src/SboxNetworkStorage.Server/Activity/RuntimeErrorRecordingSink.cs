using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;
using SboxNetworkStorage.Server.Compat;

namespace SboxNetworkStorage.Server.Activity;

/// <summary>
/// Wraps the operator alert sinks so every reported error that names a project also lands in that project's
/// Errors tab (<c>storage_errors</c>) through <see cref="RuntimeActivityLog"/>. Recording never throws and
/// never delays the alert.
/// </summary>
public sealed class RuntimeErrorRecordingSink(
    IExceptionAlertSink exceptionSink,
    INetworkStorageErrorAlertSink storageSink,
    RuntimeActivityLog log,
    IHttpContextAccessor contextAccessor) : IExceptionAlertSink, INetworkStorageErrorAlertSink
{
    private ClientIdentity? CurrentClient()
        => contextAccessor.HttpContext is { } context ? ClientIdentityContext.Get(context) : null;
    public Task NotifyAsync(CapturedErrorDto capturedError, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(capturedError.ProjectId))
        {
            log.RecordError(capturedError.ProjectId, capturedError.Classification, capturedError.Message, capturedError.StackTrace,
                capturedError.Source, $"{capturedError.Method} {capturedError.Path}",
                capturedError.StatusCode >= StatusCodes.Status500InternalServerError ? "error" : "warning", CurrentClient());
        }
        return exceptionSink.NotifyAsync(capturedError, cancellationToken);
    }

    public Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken)
    {
        log.RecordError(error.ProjectId, error.Code, error.Message, null, "storage " + error.Operation,
            $"{error.CollectionId}/{error.RecordKey}", "error", CurrentClient());
        return storageSink.NotifyAsync(error, cancellationToken);
    }
}
