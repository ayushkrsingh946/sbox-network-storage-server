using System.Text.Json;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>Whether a provider is run by sboxns.com itself or by someone else.</summary>
public enum ProviderOrigin
{
    FirstParty = 0,
    ThirdParty = 1,
}

/// <summary>What a provider can do. Flags so later providers can mix them.</summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Send = 1,
    Forward = 2,
    Inbound = 4,
}

/// <summary>Non-secret setup inputs from the owner console. The token is never rendered back.</summary>
public sealed record ProviderInputs(
    string Token = "",
    string AccountId = "",
    string ZoneId = "",
    string Subdomain = "",
    string FromAddress = "",
    string ForwardTo = "");

/// <summary>One thing setup will create on the provider. The plan lists exactly these.</summary>
public sealed record PlannedResource(string Kind, string Description);

/// <summary>What <see cref="IAlertProvider.ApplySetupAsync"/> may create. Nothing else is touched.</summary>
public sealed record ProviderPlan(string ProviderId, string Title, IReadOnlyList<PlannedResource> Resources, string? Note = null);

/// <summary>One remote thing setup created. Teardown deletes only recorded entries.</summary>
public sealed record CreatedResource(string Kind, string Id, string Description);

public sealed record ProviderApplyResult(
    bool Ok, string? Error, IReadOnlyList<CreatedResource> Created, string? FromAddress, string? Detail);

public sealed record ProviderVerifyResult(bool Ok, string Detail);

public sealed record ProviderTeardownResult(bool Ok, string? Error, int Removed);

/// <summary>Secrets plus the non-secret config a send needs. Never logged or rendered.</summary>
public sealed record ProviderSendContext(
    string AccountId, string ZoneId, string Subdomain, string FromAddress, string ForwardTo, string Token);

public sealed record ProviderTeardownContext(ProviderSendContext Context, IReadOnlyList<CreatedResource> Created);

/// <summary>Provider secrets as stored owner-only under <c>&lt;secrets&gt;/monitoring/</c>.</summary>
public sealed record ProviderSecrets(
    string? Token = null, string? AccountId = null, string? ZoneId = null, string? Subdomain = null,
    string? FromAddress = null, string? ForwardTo = null);

/// <summary>Non-secret provider state kept in <c>monitoring.json</c>. Never carries the token.</summary>
public sealed record ProviderSetup(
    string ProviderId,
    bool Configured,
    string AccountId,
    string ZoneId,
    string Subdomain,
    string FromAddress,
    bool ForwardingEnabled,
    IReadOnlyList<CreatedResource> Created,
    string? Detail,
    DateTimeOffset UpdatedAt);

/// <summary>Thrown by providers when a send fails. Messages never contain secrets.</summary>
public sealed class ProviderException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// Optional one-click integrations on top of the internal channels. The server stays fully
/// self-contained: providers never run unless the owner configures them, and plain SMTP and
/// webhook channels work untouched. Every adapter sends behind <see cref="IAlertChannel"/>, so
/// the throttle, the dispatcher and the severity/project filters apply unchanged.
/// </summary>
public interface IAlertProvider
{
    string Id { get; }
    string DisplayName { get; }
    ProviderOrigin Origin { get; }
    ProviderCapabilities Capabilities { get; }
    string Description { get; }

    string OriginLabel => Origin == ProviderOrigin.FirstParty ? "First party" : "Third party";

    string CapabilitiesLabel
    {
        get
        {
            var parts = new List<string>();
            if (Capabilities.HasFlag(ProviderCapabilities.Send)) parts.Add("send");
            if (Capabilities.HasFlag(ProviderCapabilities.Forward)) parts.Add("forward");
            if (Capabilities.HasFlag(ProviderCapabilities.Inbound)) parts.Add("inbound");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
    }

    /// <summary>Whether the provider can run on this server (e.g. sboxns.com needs a tunnel/DNS identity).</summary>
    (bool Available, string? Reason) CheckAvailability();

    /// <summary>Validates the setup form. Returns the error to show, or null.</summary>
    string? ValidateInputs(ProviderInputs inputs);

    /// <summary>Lists exactly what setup will create. Purely local: creates nothing, needs no network.</summary>
    Task<ProviderPlan> PlanSetupAsync(ProviderInputs inputs, CancellationToken cancellationToken);

    /// <summary>Validates credentials, creates the planned resources, records only what it created.</summary>
    Task<ProviderApplyResult> ApplySetupAsync(ProviderInputs inputs, CancellationToken cancellationToken);

    /// <summary>Sends one alert. Throws <see cref="ProviderException"/> on failure; the dispatcher reports it.</summary>
    Task SendAsync(AlertMessage message, ProviderSendContext context, CancellationToken cancellationToken);

    Task<ProviderVerifyResult> VerifyAsync(ProviderSendContext context, CancellationToken cancellationToken);

    /// <summary>Deletes only the recorded <see cref="CreatedResource"/> entries. Missing remote entries count as removed.</summary>
    Task<ProviderTeardownResult> TeardownAsync(ProviderTeardownContext context, CancellationToken cancellationToken);
}

/// <summary>Minimal store surface providers need. Implemented by <see cref="Monitoring.MonitoringStore"/>.</summary>
public interface IProviderStore
{
    ProviderSetup? GetProviderSetup(string providerId);
    ProviderSecrets ReadProviderSecrets(string providerId);
}

/// <summary>All registered providers. Later registrations win when two share an id (tests override).</summary>
public sealed class ProviderRegistry(IEnumerable<IAlertProvider> providers)
{
    private readonly IReadOnlyList<IAlertProvider> _providers = providers.ToList();

    public IReadOnlyList<IAlertProvider> All => _providers;

    public IAlertProvider? Find(string id)
    {
        IAlertProvider? match = null;
        foreach (var provider in _providers)
            if (string.Equals(provider.Id, id, StringComparison.Ordinal))
                match = provider;
        return match;
    }
}

/// <summary>An <see cref="IAlertChannel"/> backed by a provider, so provider sends share dispatcher routing.</summary>
public sealed class ProviderChannel(IAlertProvider provider, ProviderSendContext context) : IAlertChannel
{
    public Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
        => provider.SendAsync(message, context, cancellationToken);
}

/// <summary>Shared helpers for the provider adapters.</summary>
internal static class ProviderJson
{
    /// <summary>Reads a Cloudflare-style <c>{success, result, errors}</c> envelope without throwing on shape drift.</summary>
    public static bool TryResultId(string body, out string id)
    {
        id = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("id", out var raw))
                {
                    id = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.ToString();
                    return id.Length > 0;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    public static string ErrorCode(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var first = errors.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var message))
                    return message.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }
}
