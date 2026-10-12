using System.Text.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Monitoring;

/// <summary>A notification channel added in the owner console. Secrets live in a separate file.</summary>
/// <param name="Kind"><c>smtp</c>, <c>discord</c>, <c>webhook</c>, or a provider id (<c>cloudflare</c>, <c>sboxns</c>).</param>
/// <param name="Preset">Webhook payload: <c>generic</c>, <c>slack</c>, <c>teams</c>, <c>ntfy</c> or <c>gotify</c>.</param>
/// <param name="ProjectId">Null for server-wide; otherwise only alerts about this project.</param>
public sealed record ChannelDefinition(
    string Id,
    string Name,
    string Kind,
    string Preset,
    AlertSeverity MinSeverity,
    string? ProjectId,
    bool Enabled,
    string SmtpHost = "",
    int SmtpPort = 587,
    bool SmtpUseTls = true,
    string SmtpUsername = "",
    string SmtpFrom = "",
    IReadOnlyList<string>? SmtpTo = null,
    string DiscordUsername = "sbox-ns");

/// <summary>Channel secrets: webhook URL (Discord and webhooks), SMTP password, webhook signing secret.</summary>
public sealed record ChannelSecrets(string? Url = null, string? Password = null, string? SigningSecret = null);

/// <summary>An operator-defined alert rule over one <see cref="MonitoringMetrics"/> metric.</summary>
/// <param name="Comparison"><c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c> or <c>&lt;=</c>.</param>
/// <param name="ForSeconds">Gauges: how long the condition must hold. Request metrics: the window they are measured over.</param>
/// <param name="ChannelIds">Empty sends to every channel that accepts the severity.</param>
/// <param name="CooldownMinutes">Minimum time between two firing notifications of this rule.</param>
public sealed record AlertRule(
    string Id,
    string Name,
    string Metric,
    string Comparison,
    double Threshold,
    int ForSeconds,
    AlertSeverity Severity,
    IReadOnlyList<string> ChannelIds,
    bool Enabled,
    int CooldownMinutes = 10);

/// <summary>One entry of the monitoring history: alerts sent and configuration changes.</summary>
/// <param name="Type"><c>alert</c> for notifications, <c>change</c> for rule and channel edits.</param>
public sealed record MonitoringEvent(DateTimeOffset At, string Type, string Summary, string? Detail = null);

/// <summary>
/// Owner-console alert rules and channels, kept in <c>&lt;state&gt;/monitoring.json</c> and edited
/// live (no restart). Channel secrets are written owner-only under
/// <c>&lt;secrets&gt;/monitoring/</c>, never into the settings file, never rendered back and never
/// included in config exports. The history (sent alerts and changes) is a bounded list in
/// <c>&lt;state&gt;/monitoring-history.json</c>. Provider setups (section 4 of issue #72) are kept
/// in the same files: non-secret setup state in <c>monitoring.json</c>, provider tokens in
/// <c>&lt;secrets&gt;/monitoring/provider-&lt;id&gt;.json</c>.
/// </summary>
public sealed class MonitoringStore : IProviderStore
{
    public const int MaxChannels = 50;
    public const int MaxRules = 100;
    public const int MaxHistory = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _settingsPath;
    private readonly string _historyPath;
    private readonly string _secretsDirectory;
    private readonly ILogger<MonitoringStore> _logger;
    private readonly Lock _gate = new();
    private Settings _settings;
    private List<MonitoringEvent> _history;

    public MonitoringStore(EffectiveConfig config, ILogger<MonitoringStore> logger)
    {
        _settingsPath = Path.Combine(config.StateDirectory, "monitoring.json");
        _historyPath = Path.Combine(config.StateDirectory, "monitoring-history.json");
        _secretsDirectory = Path.Combine(config.SecretsDirectory, "monitoring");
        _logger = logger;
        _settings = Load<Settings>(_settingsPath) ?? new Settings([], []);
        _history = Load<List<MonitoringEvent>>(_historyPath) ?? [];
    }

    /// <summary>Raised after rules change, so the sampler can start or stop.</summary>
    public event Action? Changed;

    public IReadOnlyList<ChannelDefinition> Channels
    {
        get { lock (_gate) return _settings.Channels; }
    }

    public IReadOnlyList<AlertRule> Rules
    {
        get { lock (_gate) return _settings.Rules; }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<MonitoringEvent> History
    {
        get { lock (_gate) return [.. _history]; }
    }

    public ChannelSecrets ReadSecrets(string channelId)
        => Load<ChannelSecrets>(SecretPath(channelId)) ?? new ChannelSecrets();

    public bool HasSecret(string channelId) => File.Exists(SecretPath(channelId));

    public IReadOnlyList<ProviderSetup> ProviderSetups
    {
        get { lock (_gate) return _settings.Providers ?? []; }
    }

    public ProviderSetup? GetProviderSetup(string providerId)
    {
        lock (_gate) return (_settings.Providers ?? []).FirstOrDefault(p => p.ProviderId == providerId);
    }

    public ProviderSecrets ReadProviderSecrets(string providerId)
        => Load<ProviderSecrets>(ProviderSecretPath(providerId)) ?? new ProviderSecrets();

    public bool HasProviderSecret(string providerId) => File.Exists(ProviderSecretPath(providerId));

    /// <summary>
    /// Adds or replaces a provider setup. Null <paramref name="secrets"/> keeps the stored ones.
    /// The setup and the history entry never carry the token.
    /// </summary>
    public void SaveProviderSetup(ProviderSetup setup, ProviderSecrets? secrets)
    {
        lock (_gate)
        {
            if (secrets is not null)
                TunnelFiles.WriteSecret(ProviderSecretPath(setup.ProviderId), JsonSerializer.Serialize(secrets, Json));
            var providers = (_settings.Providers ?? []).Where(p => p.ProviderId != setup.ProviderId).Append(setup).ToList();
            _settings = _settings with { Providers = providers };
            SaveSettings();
            AppendLocked(new MonitoringEvent(DateTimeOffset.UtcNow, "change",
                $"Provider {(setup.Configured ? "set up" : "updated")}: {setup.ProviderId}", setup.FromAddress));
        }
    }

    public bool DeleteProviderSetup(string providerId)
    {
        lock (_gate)
        {
            var setup = (_settings.Providers ?? []).FirstOrDefault(p => p.ProviderId == providerId);
            var hadSecret = File.Exists(ProviderSecretPath(providerId));
            if (setup is null && !hadSecret) return false;
            _settings = _settings with { Providers = (_settings.Providers ?? []).Where(p => p.ProviderId != providerId).ToList() };
            SaveSettings();
            try { File.Delete(ProviderSecretPath(providerId)); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete the secret file of provider {Provider}", providerId); }
            AppendLocked(new MonitoringEvent(DateTimeOffset.UtcNow, "change", $"Provider removed: {providerId}", null));
        }

        return true;
    }

    /// <summary>Adds or replaces a channel. Null <paramref name="secrets"/> keeps the stored ones.</summary>
    public void SaveChannel(ChannelDefinition channel, ChannelSecrets? secrets)
    {
        lock (_gate)
        {
            var existing = _settings.Channels.Any(c => c.Id == channel.Id);
            if (!existing && _settings.Channels.Count >= MaxChannels)
                throw new InvalidOperationException($"At most {MaxChannels} channels can be added.");
            if (secrets is not null)
                TunnelFiles.WriteSecret(SecretPath(channel.Id), JsonSerializer.Serialize(secrets, Json));
            var channels = _settings.Channels.Where(c => c.Id != channel.Id).Append(channel).ToList();
            _settings = _settings with { Channels = channels };
            SaveSettings();
            AppendLocked(new MonitoringEvent(DateTimeOffset.UtcNow, "change", $"Channel {(existing ? "updated" : "added")}: {channel.Name}", KindLabel(channel)));
        }
    }

    public bool DeleteChannel(string id)
    {
        lock (_gate)
        {
            var channel = _settings.Channels.FirstOrDefault(c => c.Id == id);
            if (channel is null) return false;
            _settings = _settings with
            {
                Channels = _settings.Channels.Where(c => c.Id != id).ToList(),
                // A rule that named only this channel falls back to every channel.
                Rules = _settings.Rules.Select(r => r.ChannelIds.Contains(id) ? r with { ChannelIds = r.ChannelIds.Where(c => c != id).ToList() } : r).ToList(),
            };
            SaveSettings();
            try { File.Delete(SecretPath(id)); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete the secret file of channel {Channel}", id); }
            AppendLocked(new MonitoringEvent(DateTimeOffset.UtcNow, "change", $"Channel deleted: {channel.Name}", KindLabel(channel)));
        }

        Changed?.Invoke();
        return true;
    }

    public void SaveRule(AlertRule rule)
    {
        lock (_gate)
        {
            var existing = _settings.Rules.Any(r => r.Id == rule.Id);
            if (!existing && _settings.Rules.Count >= MaxRules)
                throw new InvalidOperationException($"At most {MaxRules} rules can be added.");
            _settings = _settings with { Rules = _settings.Rules.Where(r => r.Id != rule.Id).Append(rule).ToList() };
            SaveSettings();
            AppendLocked(new MonitoringEvent(DateTimeOffset.UtcNow, "change", $"Rule {(existing ? "updated" : "added")}: {rule.Name}",
                $"{MonitoringMetrics.Describe(rule)}{(rule.Enabled ? "" : " (disabled)")}"));
        }

        Changed?.Invoke();
    }

    public bool DeleteRule(string id)
    {
        lock (_gate)
        {
            var rule = _settings.Rules.FirstOrDefault(r => r.Id == id);
            if (rule is null) return false;
            _settings = _settings with { Rules = _settings.Rules.Where(r => r.Id != id).ToList() };
            SaveSettings();
            AppendLocked(new MonitoringEvent(DateTimeOffset.UtcNow, "change", $"Rule deleted: {rule.Name}", MonitoringMetrics.Describe(rule)));
        }

        Changed?.Invoke();
        return true;
    }

    public void Append(MonitoringEvent entry)
    {
        lock (_gate) AppendLocked(entry);
    }

    private void AppendLocked(MonitoringEvent entry)
    {
        _history.Insert(0, entry);
        if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        try
        {
            ConfigFiles.WriteAtomically(_historyPath, JsonSerializer.Serialize(_history, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience record; the log still has every alert.
            _logger.LogWarning(ex, "Could not write {Path}", _historyPath);
        }
    }

    private void SaveSettings() => ConfigFiles.WriteAtomically(_settingsPath, JsonSerializer.Serialize(_settings, Json));

    private static string KindLabel(ChannelDefinition channel) => channel.Kind switch
    {
        "smtp" => "Email",
        "discord" => "Discord",
        "cloudflare" => "Cloudflare",
        "sboxns" => "sboxns.com",
        _ => $"Webhook ({channel.Preset})",
    };

    private string SecretPath(string channelId) => Path.Combine(_secretsDirectory, $"channel-{channelId}.json");

    private string ProviderSecretPath(string providerId) => Path.Combine(_secretsDirectory, $"provider-{providerId}.json");


    private T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file must not stop the server. Keep it beside the new one so the next save
            // cannot overwrite what the operator may want to recover.
            _logger.LogError(ex, "Could not read {Path}; monitoring starts without it", path);
            if (ex is JsonException)
            {
                try { File.Move(path, $"{path}.damaged-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", overwrite: true); }
                catch (IOException) { }
            }

            return null;
        }
    }

    private sealed record Settings(IReadOnlyList<ChannelDefinition> Channels, IReadOnlyList<AlertRule> Rules, IReadOnlyList<ProviderSetup>? Providers = null);
}
