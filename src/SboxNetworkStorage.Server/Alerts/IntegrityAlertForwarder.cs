using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// Routes integrity-module flag alerts through the existing operator alert
/// channels with the same throttle and suppression as every other alert.
/// One alert per detector/player fingerprint per repeat window; held-back
/// repeats are noted, never dropped silently.
/// </summary>
public sealed class IntegrityAlertForwarder(
    AlertThrottle throttle,
    AlertDispatcher dispatcher,
    TimeProvider time) : IIntegrityAlertSink
{
    public async Task NotifyFlagAsync(string projectId, string detector, string severity, string steamId,
        string summary, CancellationToken ct)
    {
        var fingerprint = $"integrity\n{projectId}\n{detector}\n{steamId}";
        if (!throttle.TryAdmit(fingerprint, out var note)) return;
        var alert = new AlertMessage(
            Kind: AlertMessage.RuleKind,
            Severity: severity == "high" ? AlertSeverity.Error : AlertSeverity.Warning,
            Title: $"Integrity flag: {detector}",
            Subject: $"[sbox-ns] integrity flag {detector} in {projectId}",
            Message: summary,
            Fields:
            [
                new AlertField("Project", projectId, Code: true),
                new AlertField("Detector", detector, Code: true),
                new AlertField("Player", steamId, Code: true),
                new AlertField("Severity", severity, Code: true),
            ],
            Timestamp: time.GetUtcNow(),
            ProjectId: projectId,
            RuleId: $"integrity-{detector}");
        if (note is not null) alert = alert.WithNote(note);
        await dispatcher.SendAsync(alert, channelIds: null, ct);
    }
}
