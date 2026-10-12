using System.Globalization;

namespace SboxNetworkStorage.Server.Monitoring;

/// <summary>How a rule reads its metric.</summary>
public enum MetricKind
{
    /// <summary>A point value; the condition must hold for the rule's duration.</summary>
    Gauge,

    /// <summary>Aggregated over the rule's duration (request rate, error rate, latency).</summary>
    Window,

    /// <summary>Fires once when it happens (server start).</summary>
    Event,
}

public sealed record MetricDefinition(string Id, string Label, string Unit, MetricKind Kind, string Help);

/// <summary>The metrics alert rules can watch. Ids are stored in <c>monitoring.json</c>: never rename one.</summary>
public static class MonitoringMetrics
{
    public const string ServerStarted = "server.started";

    /// <summary>Request-window metrics only evaluate with at least this many requests in the window.</summary>
    public const int MinimumRequests = 20;

    public static readonly IReadOnlyList<MetricDefinition> All =
    [
        new("cpu.percent", "CPU use", "%", MetricKind.Gauge, "Host CPU, or the container's CPU limit when one is set."),
        new("process.cpu.percent", "Server process CPU", "%", MetricKind.Gauge, "CPU used by sbox-ns against every core it may use."),
        new("memory.percent", "Memory in use", "%", MetricKind.Gauge, "Host memory, or the container's memory limit when one is set."),
        new("process.memory.mb", "Server process memory", "MB", MetricKind.Gauge, "Working set of the sbox-ns process."),
        new("disk.data.free.gb", "Free disk space (data folder)", "GB", MetricKind.Gauge, "Free space on the volume that holds the data folder."),
        new("disk.data.free.percent", "Free disk space (data folder)", "%", MetricKind.Gauge, "Free space on the data volume as a share of its size."),
        new("http.error_rate.percent", "Request error rate", "%", MetricKind.Window, $"Share of requests answered with 5xx over the window. Needs {MinimumRequests} requests in the window."),
        new("http.p95.ms", "Request latency p95", "ms", MetricKind.Window, $"95th percentile request duration over the window. Needs {MinimumRequests} requests in the window."),
        new("http.requests.per_second", "Request rate", "req/s", MetricKind.Window, "Average requests per second over the window."),
        new("backup.age.hours", "Newest backup age", "h", MetricKind.Gauge, "Age of the newest file in the backups folder. No rule result while there is no backup."),
        new("update.available", "Update available", "", MetricKind.Gauge, "1 when the update check reports a newer release, otherwise 0."),
        new(ServerStarted, "Server started", "", MetricKind.Event, "Sends one notification each time the server starts."),
    ];

    public static MetricDefinition? Find(string? id) => All.FirstOrDefault(metric => metric.Id == id);

    public static readonly IReadOnlyList<string> Comparisons = [">", ">=", "<", "<="];

    public static bool Compare(double value, string comparison, double threshold) => comparison switch
    {
        ">" => value > threshold,
        ">=" => value >= threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        _ => false,
    };

    /// <summary>"CPU use &gt; 90 % for 2 min", for lists, history and notifications.</summary>
    public static string Describe(AlertRule rule)
    {
        var metric = Find(rule.Metric);
        if (metric is null) return rule.Metric;
        if (metric.Kind == MetricKind.Event) return metric.Label;
        var threshold = rule.Threshold.ToString("0.##", CultureInfo.InvariantCulture) + (metric.Unit.Length == 0 ? "" : " " + metric.Unit);
        var duration = FormatDuration(rule.ForSeconds);
        return metric.Kind == MetricKind.Window
            ? $"{metric.Label} {rule.Comparison} {threshold} over {duration}"
            : rule.ForSeconds == 0 ? $"{metric.Label} {rule.Comparison} {threshold}" : $"{metric.Label} {rule.Comparison} {threshold} for {duration}";
    }

    public static string FormatDuration(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        _ when seconds % 3600 == 0 => $"{seconds / 3600} h",
        _ when seconds % 60 == 0 => $"{seconds / 60} min",
        _ => $"{seconds / 60} min {seconds % 60} s",
    };
}
