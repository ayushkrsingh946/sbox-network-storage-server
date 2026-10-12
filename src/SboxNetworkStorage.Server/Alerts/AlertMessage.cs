using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>Alert severity, lowest first. Channels deliver alerts at or above their minimum.</summary>
public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}

/// <summary>One labelled value of an alert.</summary>
/// <param name="Code">Caller-chosen text (routes, ids): chat channels show it as code so it cannot add links or formatting.</param>
/// <param name="Chat">False keeps the field out of chat channels (Discord, chat webhooks); email and JSON webhooks still carry it.</param>
public sealed record AlertField(string Name, string Value, bool Code = false, bool Inline = true, bool Chat = true);

/// <summary>
/// A channel-neutral notification. Captured errors, alert rules and test sends all
/// become one of these, so every channel (Discord, SMTP, webhooks) formats the same data
/// and the shared throttle in <see cref="OperatorAlertSink"/> and the rule engine see one shape.
/// </summary>
/// <param name="Kind"><c>error</c>, <c>rule</c> or <c>test</c>.</param>
/// <param name="State">For rules: <c>firing</c> or <c>resolved</c>.</param>
/// <param name="Subject">Mail subject line.</param>
/// <param name="MessageAsCode">The message is caller-chosen text; chat channels show it in a code block.</param>
public sealed record AlertMessage(
    string Kind,
    AlertSeverity Severity,
    string Title,
    string Subject,
    string Message,
    IReadOnlyList<AlertField> Fields,
    DateTimeOffset Timestamp,
    string? ProjectId = null,
    string? RuleId = null,
    string? State = null,
    string? CorrelationId = null,
    bool MessageAsCode = false)
{
    public const string ErrorKind = "error";
    public const string RuleKind = "rule";
    public const string TestKind = "test";

    /// <summary>Appends a note (held-back counts) to the message.</summary>
    public AlertMessage WithNote(string? note) => note is null ? this : this with { Message = Message + "\n\n" + note };

    /// <summary>A captured request error, as Discord and SMTP have always shown it.</summary>
    public static AlertMessage FromCaptured(CapturedErrorDto error)
    {
        var fields = new List<AlertField>
        {
            new("Route", $"{error.Method} {error.Path}", Code: true),
            new("Status", error.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Classification", error.Classification),
            new("Correlation ID", error.CorrelationId, Inline: false),
            new("Source", error.Source, Chat: false),
        };
        if (!string.IsNullOrWhiteSpace(error.ProjectId)) fields.Add(new("Project", error.ProjectId, Code: true));
        fields.Add(new("Steam ID", error.SteamId ?? "-", Chat: false));
        return new AlertMessage(
            ErrorKind, AlertSeverity.Error,
            Title: $"sbox-ns error: {error.Classification}",
            Subject: $"[sbox-ns] {error.Classification} {error.Method} {error.Path} ({error.CorrelationId})",
            Message: error.Message,
            Fields: fields,
            Timestamp: error.Timestamp,
            ProjectId: string.IsNullOrWhiteSpace(error.ProjectId) ? null : error.ProjectId,
            CorrelationId: error.CorrelationId,
            MessageAsCode: true);
    }

    /// <summary>A data-plane storage error (unconfirmed saves, store failures, library traffic).</summary>
    public static AlertMessage FromStorage(NetworkStorageError error, DateTimeOffset now)
    {
        var fields = new List<AlertField>
        {
            new("Code", error.Code),
            new("Operation", error.Operation),
            new("Collection", error.CollectionId, Code: true),
            new("Record", error.RecordKey, Code: true),
        };
        if (!string.IsNullOrWhiteSpace(error.ProjectId)) fields.Add(new("Project", error.ProjectId, Code: true));
        return new AlertMessage(
            ErrorKind, AlertSeverity.Error,
            Title: $"sbox-ns error: {error.Code}",
            Subject: $"[sbox-ns] {error.Code} {error.Operation} {error.CollectionId}/{error.RecordKey}",
            Message: error.Message,
            Fields: fields,
            Timestamp: now,
            ProjectId: string.IsNullOrWhiteSpace(error.ProjectId) ? null : error.ProjectId,
            MessageAsCode: true);
    }

    public static string SeverityName(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Info => "info",
        AlertSeverity.Warning => "warning",
        AlertSeverity.Error => "error",
        _ => "critical",
    };

    public static bool TryParseSeverity(string? value, out AlertSeverity severity)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "info": severity = AlertSeverity.Info; return true;
            case "warning": severity = AlertSeverity.Warning; return true;
            case "error": severity = AlertSeverity.Error; return true;
            case "critical": severity = AlertSeverity.Critical; return true;
            default: severity = AlertSeverity.Info; return false;
        }
    }
}
