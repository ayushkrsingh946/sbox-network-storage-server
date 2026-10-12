using System.Globalization;
using SboxNetworkStorage.Server.Alerts;

namespace SboxNetworkStorage.Server.Monitoring;

/// <summary>Live state of one rule for the console.</summary>
/// <param name="State"><c>ok</c>, <c>pending</c> (condition true, waiting for the duration), <c>firing</c> or <c>no data</c>.</param>
public sealed record RuleStatus(string State, DateTimeOffset Since, double? Value);

/// <summary>
/// Evaluates alert rules against the sampler and sends firing and resolved notifications through
/// the shared <see cref="AlertThrottle"/> and <see cref="AlertDispatcher"/>, the same path captured
/// errors take. A gauge rule fires after its condition holds for the rule's duration; a request rule
/// is measured over that duration. A rule fires at most once per cooldown; a resolve is sent only
/// for a firing that was sent.
/// </summary>
public sealed class AlertRuleEngine(
    MonitoringStore store,
    AlertThrottle throttle,
    AlertDispatcher dispatcher,
    TimeProvider time,
    ILogger<AlertRuleEngine> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Tracked> _states = new(StringComparer.Ordinal);

    public RuleStatus? Status(string ruleId)
    {
        lock (_gate) return _states.TryGetValue(ruleId, out var state) ? new RuleStatus(state.State, state.Since, state.Value) : null;
    }

    /// <summary>True when an enabled rule watches a sampled metric.</summary>
    public bool NeedsSampling(IReadOnlyList<AlertRule> rules)
        => rules.Any(rule => rule.Enabled && MonitoringMetrics.Find(rule.Metric) is { Kind: not MetricKind.Event });

    /// <summary>Sends the "server started" rules once, at startup.</summary>
    public async Task AnnounceStartAsync(CancellationToken cancellationToken)
    {
        foreach (var rule in store.Rules.Where(r => r.Enabled && r.Metric == MonitoringMetrics.ServerStarted))
        {
            var message = Message(rule, "firing", value: null) with
            {
                Title = $"sbox-ns started: {rule.Name}",
                Subject = $"[sbox-ns] Server started ({Hosting.BuildInfo.Version})",
                Message = $"sbox-ns {Hosting.BuildInfo.Version} started on {Environment.MachineName}.",
                State = null,
            };
            await NotifyAsync(rule, message, "started", cancellationToken);
        }
    }

    /// <summary>Evaluates every enabled rule once. Notifications are sent in the background.</summary>
    public void Evaluate(MetricsSampler sampler, bool updateAvailable)
    {
        var now = time.GetUtcNow();
        var rules = store.Rules.Where(r => r.Enabled && MonitoringMetrics.Find(r.Metric) is { Kind: not MetricKind.Event }).ToList();
        var pending = new List<(AlertRule Rule, string Transition, double? Value)>();
        lock (_gate)
        {
            foreach (var gone in _states.Keys.Where(id => !rules.Any(r => r.Id == id)).ToList()) _states.Remove(gone);
            foreach (var rule in rules)
            {
                if (!_states.TryGetValue(rule.Id, out var state) || state.Rule != rule)
                {
                    // New or edited rule: start from a clean state.
                    state = new Tracked(rule, now);
                    _states[rule.Id] = state;
                }

                var value = Read(rule, sampler, updateAvailable);
                state.Value = value;
                var transition = Step(state, value, now);
                if (transition is not null) pending.Add((rule, transition, value));
            }
        }

        foreach (var (rule, transition, value) in pending)
        {
            _ = NotifyAsync(rule, Message(rule, transition, value), transition, CancellationToken.None);
        }
    }

    /// <summary>Advances one rule; returns <c>firing</c> or <c>resolved</c> when a notification is due.</summary>
    private static string? Step(Tracked state, double? value, DateTimeOffset now)
    {
        var rule = state.Rule;
        if (value is null)
        {
            // No data never fires and never resolves a firing rule.
            if (state.State is "ok" or "pending") Move(state, "no data", now);
            return null;
        }

        var holds = MonitoringMetrics.Compare(value.Value, rule.Comparison, rule.Threshold);
        var gauge = MonitoringMetrics.Find(rule.Metric)!.Kind == MetricKind.Gauge;
        switch (state.State)
        {
            case "firing" when !holds:
                Move(state, "ok", now);
                return state.Notified ? "resolved" : null;
            case "firing":
                return null;
            case "pending" when holds && now - state.Since >= TimeSpan.FromSeconds(rule.ForSeconds):
                return Fire(state, now);
            case "pending" when holds:
                return null;
            default:
                if (!holds)
                {
                    if (state.State != "ok") Move(state, "ok", now);
                    return null;
                }

                if (gauge && rule.ForSeconds > 0)
                {
                    Move(state, "pending", now);
                    return null;
                }

                return Fire(state, now);
        }
    }

    private static string? Fire(Tracked state, DateTimeOffset now)
    {
        Move(state, "firing", now);
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, state.Rule.CooldownMinutes));
        state.Notified = state.LastNotified is null || now - state.LastNotified >= cooldown;
        if (!state.Notified) return null;
        state.LastNotified = now;
        return "firing";
    }

    private static void Move(Tracked state, string to, DateTimeOffset now)
    {
        state.State = to;
        state.Since = now;
    }

    private double? Read(AlertRule rule, MetricsSampler sampler, bool updateAvailable)
    {
        var latest = sampler.Latest;
        switch (rule.Metric)
        {
            case "cpu.percent": return latest?.Cpu;
            case "process.cpu.percent": return latest?.ProcessCpu;
            case "memory.percent": return latest?.MemoryPercent;
            case "process.memory.mb": return latest is null ? null : Math.Round(latest.ProcessMemory / 1048576d, 1);
            case "disk.data.free.gb": return sampler.Slow.DataFree is { } free ? Math.Round(free / 1073741824d, 2) : null;
            case "disk.data.free.percent":
                return sampler.Slow is { DataFree: { } f, DataTotal: { } t } && t > 0 ? Math.Round(f * 100d / t, 1) : null;
            case "backup.age.hours":
                return sampler.Slow.NewestBackup is { } newest ? Math.Round((time.GetUtcNow() - newest).TotalHours, 1) : null;
            case "update.available": return updateAvailable ? 1 : 0;
        }

        var span = TimeSpan.FromSeconds(Math.Max(rule.ForSeconds, 60));
        var (window, covered) = sampler.RequestWindowOver(span);
        // Too little of the window sampled yet (just started) means no result.
        if (covered < span.TotalSeconds / 2) return null;
        return rule.Metric switch
        {
            "http.requests.per_second" => Math.Round(window.Requests / covered, 2),
            "http.error_rate.percent" => window.Requests >= MonitoringMetrics.MinimumRequests ? Math.Round(window.ErrorRatePercent!.Value, 2) : null,
            "http.p95.ms" => window.Requests >= MonitoringMetrics.MinimumRequests ? window.Percentile(0.95) : null,
            _ => null,
        };
    }

    private AlertMessage Message(AlertRule rule, string transition, double? value)
    {
        var metric = MonitoringMetrics.Find(rule.Metric);
        var shown = value is null ? "no data" : value.Value.ToString("0.##", CultureInfo.InvariantCulture) + (metric?.Unit is { Length: > 0 } unit ? " " + unit : "");
        var firing = transition == "firing";
        return new AlertMessage(
            AlertMessage.RuleKind, rule.Severity,
            Title: $"{(firing ? "Firing" : "Resolved")}: {rule.Name}",
            Subject: $"[sbox-ns] {(firing ? "FIRING" : "RESOLVED")} {rule.Name}",
            Message: firing
                ? $"{MonitoringMetrics.Describe(rule)}. Current value: {shown}."
                : $"Back to normal: {MonitoringMetrics.Describe(rule)} no longer holds. Current value: {shown}.",
            Fields:
            [
                new AlertField("Rule", rule.Name),
                new AlertField("Condition", MonitoringMetrics.Describe(rule), Inline: false),
                new AlertField("Value", shown),
                new AlertField("Severity", AlertMessage.SeverityName(rule.Severity)),
                new AlertField("Server", $"{Environment.MachineName} ({Hosting.BuildInfo.Version})"),
            ],
            Timestamp: time.GetUtcNow(),
            RuleId: rule.Id,
            State: transition);
    }

    private async Task NotifyAsync(AlertRule rule, AlertMessage message, string transition, CancellationToken cancellationToken)
    {
        try
        {
            if (!throttle.TryAdmit($"rule\n{rule.Id}\n{transition}", out var note))
            {
                store.Append(new MonitoringEvent(time.GetUtcNow(), "alert", message.Title, "Held back by the alert limit."));
                return;
            }

            var deliveries = await dispatcher.SendAsync(message.WithNote(note), rule.ChannelIds, cancellationToken);
            var failed = deliveries.Where(d => !d.Ok).Select(d => $"{d.ChannelName}: {d.Error}").ToList();
            store.Append(new MonitoringEvent(time.GetUtcNow(), "alert", message.Title,
                deliveries.Count == 0 ? "No channel accepts this alert." :
                failed.Count == 0 ? $"Sent to {deliveries.Count} channel(s)." : "Failed: " + string.Join("; ", failed)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sending the {Transition} notification of rule {Rule} failed", transition, rule.Name);
        }
    }

    private sealed class Tracked(AlertRule rule, DateTimeOffset since)
    {
        public AlertRule Rule { get; } = rule;
        public string State { get; set; } = "ok";
        public DateTimeOffset Since { get; set; } = since;
        public double? Value { get; set; }
        public bool Notified { get; set; }
        public DateTimeOffset? LastNotified { get; set; }
    }
}
