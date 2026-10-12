using SboxNetworkStorage.Server.Monitoring;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// The one alert limit for every source (captured errors and alert rules). An alert like one sent in
/// the last <see cref="RepeatWindow"/> is held back, and at most <see cref="MaxPerMinute"/> alerts go
/// out per minute. The next alert that is sent says how many were held back.
/// </summary>
public sealed class AlertThrottle(TimeProvider time)
{
    public const int MaxPerMinute = 10;
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    private const int MaxTrackedKinds = 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Kind> _recent = new(StringComparer.Ordinal);
    private DateTimeOffset _minuteStart = DateTimeOffset.MinValue;
    private int _sentThisMinute;
    private int _overLimit;

    /// <summary>Decides whether an alert goes out; <paramref name="note"/> reports alerts held back since the last one.</summary>
    public bool TryAdmit(string fingerprint, out string? note)
    {
        note = null;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_recent.TryGetValue(fingerprint, out var kind) && now - kind.SentAt < RepeatWindow)
            {
                kind.Repeats++;
                return false;
            }

            if (now - _minuteStart >= TimeSpan.FromMinutes(1))
            {
                _minuteStart = now;
                _sentThisMinute = 0;
            }

            if (_sentThisMinute >= MaxPerMinute)
            {
                _overLimit++;
                return false;
            }

            _sentThisMinute++;
            var notes = new List<string>(2);
            if (kind is { Repeats: > 0 })
                notes.Add($"{kind.Repeats} more like this were not sent in the {RepeatWindow.TotalMinutes:0} minutes before this one.");
            if (_overLimit > 0)
                notes.Add($"{_overLimit} other alerts were not sent because of the limit of {MaxPerMinute} per minute.");
            _overLimit = 0;
            note = notes.Count == 0 ? null : string.Join(" ", notes);

            if (kind is null && _recent.Count >= MaxTrackedKinds)
            {
                foreach (var stale in _recent.Where(entry => now - entry.Value.SentAt >= RepeatWindow).Select(entry => entry.Key).ToList())
                    _recent.Remove(stale);
            }

            if (kind is not null)
            {
                kind.SentAt = now;
                kind.Repeats = 0;
            }
            else if (_recent.Count < MaxTrackedKinds)
            {
                _recent[fingerprint] = new Kind { SentAt = now };
            }

            return true;
        }
    }

    private sealed class Kind
    {
        public DateTimeOffset SentAt;
        public int Repeats;
    }
}

/// <summary>A channel as the owner console lists it.</summary>
/// <param name="Source"><c>config</c> for <c>alerts.toml</c> channels (read-only in the console), <c>console</c> otherwise.</param>
/// <param name="Problem">Why an enabled channel cannot send (missing host, URL), or null.</param>
public sealed record ChannelInfo(
    string Id, string Name, string Kind, string Preset, AlertSeverity MinSeverity, string? ProjectId,
    bool Enabled, string Source, string? Problem);

/// <summary>Outcome of one channel send, shown after "Send test" and kept in the history.</summary>
public sealed record ChannelDelivery(string ChannelId, string ChannelName, bool Ok, string? Error);

/// <summary>
/// Sends one <see cref="AlertMessage"/> to the channels that accept it: the <c>alerts.toml</c> Discord
/// and SMTP channels plus those added in the owner console. Sends run concurrently and are
/// best-effort: a failing channel is logged and reported, never thrown, so alerting cannot break the
/// request or rule evaluation that triggered it.
/// </summary>
public sealed class AlertDispatcher(
    AlertOptions options,
    MonitoringStore store,
    IDiscordWebhookClient discord,
    ISmtpTransport smtp,
    IWebhookClient webhooks,
    TimeProvider time,
    ILogger<AlertDispatcher> logger)
{
    public const string ConfigDiscordId = "config-discord";
    public const string ConfigSmtpId = "config-smtp";

    /// <summary>Webhook retry delays; tests shorten them.</summary>
    public IReadOnlyList<TimeSpan> WebhookRetryDelays { get; init; } = WebhookChannel.DefaultRetryDelays;

    /// <summary>
    /// Provider wiring, set by DI. Null in older tests and when no provider is registered;
    /// provider code never runs without a configured provider channel.
    /// </summary>
    public IProviderStore? ProviderStore { get; init; }

    public ProviderRegistry? Providers { get; init; }

    public IReadOnlyList<ChannelInfo> Channels()
    {
        var list = new List<ChannelInfo>();
        if (options.DiscordEnabled)
        {
            list.Add(new ChannelInfo(ConfigDiscordId, "Discord (alerts.toml)", "discord", "", AlertSeverity.Info, null, true, "config",
                string.IsNullOrWhiteSpace(options.DiscordWebhookUrl) ? "No webhook URL is configured." : null));
        }

        if (options.SmtpEnabled)
        {
            list.Add(new ChannelInfo(ConfigSmtpId, "Email (alerts.toml)", "smtp", "", AlertSeverity.Info, null, true, "config",
                string.IsNullOrWhiteSpace(options.SmtpHost) || options.SmtpTo.Count == 0 || string.IsNullOrWhiteSpace(options.SmtpFrom)
                    ? "Host, from or to is missing." : null));
        }

        foreach (var channel in store.Channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new ChannelInfo(channel.Id, channel.Name, channel.Kind, channel.Preset, channel.MinSeverity, channel.ProjectId,
                channel.Enabled, "console", ProviderProblem(channel.Kind)
                    ?? (channel.Kind == "smtp" && (channel.SmtpTo is null || channel.SmtpTo.Count == 0) ? "No recipients." : null)));
        }

        return list;
    }

    private string? ProviderProblem(string kind) => kind switch
    {
        "cloudflare" or "sboxns" => Providers?.Find(kind) is null ? $"The {kind} provider is not registered."
            : ProviderStore?.GetProviderSetup(kind) is not { Configured: true } ? $"Set up the {kind} provider below first."
            : null,
        _ => null,
    };


    /// <summary>
    /// Sends to every enabled channel whose minimum severity and project scope accept the alert, or,
    /// when <paramref name="channelIds"/> is non-empty, to those of them only.
    /// </summary>
    public async Task<IReadOnlyList<ChannelDelivery>> SendAsync(AlertMessage message, IReadOnlyCollection<string>? channelIds, CancellationToken cancellationToken)
    {
        var targets = Channels()
            .Where(channel => channel.Enabled
                && message.Severity >= channel.MinSeverity
                && (channel.ProjectId is null || channel.ProjectId == message.ProjectId)
                && (channelIds is null || channelIds.Count == 0 || channelIds.Contains(channel.Id)))
            .ToList();
        return await Task.WhenAll(targets.Select(channel => DeliverAsync(channel, message, cancellationToken)));
    }

    /// <summary>Sends a test notification to one channel, ignoring its severity and project filters.</summary>
    public async Task<ChannelDelivery?> SendTestAsync(string channelId, CancellationToken cancellationToken)
    {
        var channel = Channels().FirstOrDefault(c => c.Id == channelId);
        if (channel is null) return null;
        var message = new AlertMessage(
            AlertMessage.TestKind, AlertSeverity.Info,
            Title: "sbox-ns test notification",
            Subject: "[sbox-ns] Test notification",
            Message: $"This is a test from the sbox-ns owner console. The channel \"{channel.Name}\" works.",
            Fields: [new AlertField("Server version", Hosting.BuildInfo.Version), new AlertField("Channel", channel.Name)],
            Timestamp: time.GetUtcNow());
        var delivery = await DeliverAsync(channel, message, cancellationToken);
        store.Append(new MonitoringEvent(time.GetUtcNow(), "alert",
            delivery.Ok ? $"Test sent to {channel.Name}" : $"Test to {channel.Name} failed", delivery.Error));
        return delivery;
    }

    private async Task<ChannelDelivery> DeliverAsync(ChannelInfo info, AlertMessage message, CancellationToken cancellationToken)
    {
        if (info.Problem is not null)
        {
            logger.LogWarning("Alert channel {Channel} is enabled but incomplete ({Problem}); skipping alert {Title}", info.Name, info.Problem, message.Title);
            return new ChannelDelivery(info.Id, info.Name, false, info.Problem);
        }

        try
        {
            await Create(info).SendAsync(message, cancellationToken);
            return new ChannelDelivery(info.Id, info.Name, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to send alert {Title} to channel {Channel} correlationId={CorrelationId}", message.Title, info.Name, message.CorrelationId);
            return new ChannelDelivery(info.Id, info.Name, false, Describe(ex));
        }
    }

    private IAlertChannel Create(ChannelInfo info)
    {
        if (info.Id == ConfigDiscordId) return new DiscordChannel(options.DiscordWebhookUrl, options.DiscordUsername, discord);
        if (info.Id == ConfigSmtpId)
        {
            return new SmtpChannel(options.SmtpHost, options.SmtpPort, options.SmtpUseTls, options.SmtpUsername, options.SmtpPassword,
                options.SmtpFrom, options.SmtpTo, smtp);
        }

        var channel = store.Channels.First(c => c.Id == info.Id);
        var secrets = store.ReadSecrets(channel.Id);
        if (channel.Kind is "cloudflare" or "sboxns")
            return ProviderChannel(channel.Kind);
        return channel.Kind switch
        {
            "discord" => new DiscordChannel(Required(secrets.Url, "webhook URL"), channel.DiscordUsername, discord),
            "smtp" => new SmtpChannel(channel.SmtpHost, channel.SmtpPort, channel.SmtpUseTls, channel.SmtpUsername, secrets.Password ?? "",
                channel.SmtpFrom, channel.SmtpTo ?? [], smtp),
            "webhook" => new WebhookChannel(Required(secrets.Url, "webhook URL"), channel.Preset, secrets.SigningSecret, webhooks, time, WebhookRetryDelays),
            _ => throw new InvalidOperationException($"Unknown channel kind {channel.Kind}."),
        };
    }

    private IAlertChannel ProviderChannel(string kind)
    {
        var provider = Providers?.Find(kind) ?? throw new InvalidOperationException($"The {kind} provider is not registered.");
        var setup = ProviderStore?.GetProviderSetup(kind) ?? throw new InvalidOperationException($"The {kind} provider is not set up.");
        if (!setup.Configured) throw new InvalidOperationException($"The {kind} provider is not set up.");
        var secrets = ProviderStore.ReadProviderSecrets(kind);
        return new ProviderChannel(provider, new ProviderSendContext(
            setup.AccountId, setup.ZoneId, setup.Subdomain, setup.FromAddress, secrets.ForwardTo ?? "", secrets.Token ?? ""));
    }

    private static string Required(string? value, string what)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"The channel has no {what}; edit the channel and enter it again.") : value;

    /// <summary>A short reason for the console. Exception messages here never contain the secret URL or password.</summary>
    private static string Describe(Exception ex)
    {
        var message = ex switch
        {
            TaskCanceledException => "Timed out.",
            HttpRequestException { StatusCode: { } status } => $"The server answered HTTP {(int)status}.",
            _ => ex.Message,
        };
        return message.Length <= 300 ? message : message[..300] + "…";
    }
}
