using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// First-party email sending and forwarding through the server's sboxns.com name, using the
/// existing tunnel or DNS identity. Client side only: this repo defines and tests the
/// <c>sboxns.email/v1</c> wire contract below, but the service-side endpoint (and any pricing
/// or quotas) is out of scope here and is NOT claimed to exist. Until it does, every operation
/// reports "service not available" and everything else keeps working.
/// <para>Proposed contract, all under an injectable base URL (default below):</para>
/// <list type="bullet">
/// <item><c>GET {base}/v1/status</c> answers <c>{"schema":"sboxns.email/v1","available":true}</c>.</item>
/// <item><c>POST {base}/v1/forwarding {"hostname","address","forward_to"}</c> answers <c>{"schema":"sboxns.email/v1","id"}</c>.</item>
/// <item><c>GET {base}/v1/forwarding/{id}</c> answers the forwarding record.</item>
/// <item><c>POST {base}/v1/send {"from","to","subject","text"}</c> sends one alert.</item>
/// <item><c>DELETE {base}/v1/forwarding/{id}</c> removes it; 404 means already gone.</item>
/// </list>
/// A breaking change gets a new <c>schema</c> value; this client sends and accepts only
/// <c>sboxns.email/v1</c>.
/// </summary>
public sealed class SboxNsAlertProvider : IAlertProvider
{
    public const string ProviderId = "sboxns";
    public const string ContractSchema = "sboxns.email/v1";
    public static readonly string DefaultBaseUrl = "https://sboxns.com/api/network-storage/email";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly EffectiveConfig _config;

    /// <param name="hostnameOverride">Test hook for the tunnel/DNS hostname; production reads the config.</param>
    public SboxNsAlertProvider(HttpClient http, EffectiveConfig config, string? baseUrl = null, string? hostnameOverride = null)
    {
        _http = http;
        _config = config;
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        HostnameOverride = hostnameOverride;
    }

    public string Id => ProviderId;
    public string DisplayName => "sboxns.com email";
    public ProviderOrigin Origin => ProviderOrigin.FirstParty;
    public ProviderCapabilities Capabilities => ProviderCapabilities.Send | ProviderCapabilities.Forward;
    public string Description => "Sends alerts as your sboxns.com name and forwards replies to you. Proposed service; the sboxns.com side is not part of this repo and may not exist yet.";
    public string BaseUrl { get; }
    internal string? HostnameOverride { get; }

    public (bool Available, string? Reason) CheckAvailability()
        => Hostname() is { } name
            ? (true, null)
            : (false, "Needs a tunnel or DNS identity first (tunnel enable or dns enable).");

    public string? ValidateInputs(ProviderInputs inputs)
    {
        inputs = Normalize(inputs);
        if (inputs.ForwardTo.Length == 0) return "Enter the operator address the mail forwards to.";
        if (!MailAddress.TryCreate(inputs.ForwardTo, out _)) return "Enter a valid forwarding address.";
        if (inputs.Token.Length > 1024) return "The API token is too long.";
        return null;
    }

    public Task<ProviderPlan> PlanSetupAsync(ProviderInputs inputs, CancellationToken cancellationToken)
    {
        inputs = Normalize(inputs);
        var address = Address(inputs);
        return Task.FromResult(new ProviderPlan(ProviderId, "sboxns.com setup will create",
        [
            new("forwarding-address", $"Create the forwarding address '{address}' delivered to '{inputs.ForwardTo}'."),
            new("console-channel", "Create the console channel 'sboxns.com email' so severity and project filters apply."),
        ],
            "Proposed sboxns.email/v1 service; setup reports 'service not available' until the service side exists."));
    }

    public async Task<ProviderApplyResult> ApplySetupAsync(ProviderInputs inputs, CancellationToken cancellationToken)
    {
        inputs = Normalize(inputs);
        var created = new List<CreatedResource>();
        var status = await StatusAsync(inputs, cancellationToken);
        if (status is null)
            return new ProviderApplyResult(false, NotAvailable(), created, Address(inputs), null);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "v1/forwarding", inputs,
                new { hostname = Hostname(), address = Address(inputs), forward_to = inputs.ForwardTo }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new ProviderApplyResult(false, NotAvailable(), created, Address(inputs), null);
            if (!response.IsSuccessStatusCode || !TryId(body, out var id))
                return new ProviderApplyResult(false, "The sboxns.com email service refused the forwarding address.", created, Address(inputs), null);
            created.Add(new CreatedResource("forwarding-address", id, $"Forwarding address '{Address(inputs)}'"));
            return new ProviderApplyResult(true, null, created, Address(inputs),
                "Forwarding address created. Mail delivery starts once the sboxns.com service activates it.");
        }
        catch (HttpRequestException)
        {
            return new ProviderApplyResult(false, NotAvailable(), created, Address(inputs), null);
        }
    }

    public async Task SendAsync(AlertMessage message, ProviderSendContext context, CancellationToken cancellationToken)
    {
        var inputs = new ProviderInputs(context.Token, context.AccountId, context.ZoneId, context.Subdomain, context.FromAddress, context.ForwardTo);
        var to = inputs.ForwardTo.Length > 0 ? inputs.ForwardTo : inputs.FromAddress;
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "v1/send", inputs,
                new { from = inputs.FromAddress, to = new[] { to }, subject = message.Subject, text = Text(message) }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new ProviderException(NotAvailable());
            if (!response.IsSuccessStatusCode)
                throw new ProviderException("The sboxns.com email service refused the alert.");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(NotAvailable(), ex);
        }
    }

    public async Task<ProviderVerifyResult> VerifyAsync(ProviderSendContext context, CancellationToken cancellationToken)
    {
        var inputs = new ProviderInputs(context.Token, context.AccountId, context.ZoneId, context.Subdomain, context.FromAddress, context.ForwardTo);
        var status = await StatusAsync(inputs, cancellationToken);
        return status is null
            ? new ProviderVerifyResult(false, NotAvailable())
            : new ProviderVerifyResult(true, $"The sboxns.com email service answers for '{Hostname()}'.");
    }

    public async Task<ProviderTeardownResult> TeardownAsync(ProviderTeardownContext context, CancellationToken cancellationToken)
    {
        var inputs = new ProviderInputs(context.Context.Token, context.Context.AccountId, context.Context.ZoneId,
            context.Context.Subdomain, context.Context.FromAddress, context.Context.ForwardTo);
        var removed = 0;
        foreach (var resource in context.Created.AsEnumerable().Reverse())
        {
            if (resource.Kind != "forwarding-address") continue;
            try
            {
                using var response = await SendAsync(HttpMethod.Delete, $"v1/forwarding/{Uri.EscapeDataString(resource.Id)}", inputs, null, cancellationToken);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) removed++;
                else return new ProviderTeardownResult(false, "The sboxns.com email service refused to remove the forwarding address.", removed);
            }
            catch (HttpRequestException)
            {
                return new ProviderTeardownResult(false, NotAvailable(), removed);
            }
        }

        return new ProviderTeardownResult(true, null, removed);
    }

    internal static string NotAvailable()
        => "The sboxns.com email service is not available. Everything else keeps working; try again later.";

    private string Address(ProviderInputs inputs)
    {
        var host = Hostname() ?? "your sboxns.com name";
        var local = inputs.Subdomain.Length > 0 ? inputs.Subdomain : "alerts";
        return $"{local}@{host}";
    }

    private string? Hostname()
    {
        if (HostnameOverride is { Length: > 0 } forced) return forced;
        try
        {
            if (_config.GetBoolean("tunnel.enabled") && _config.GetString("tunnel.hostname") is { Length: > 0 } tunnel) return tunnel;
            if (_config.GetBoolean("dns.enabled") && _config.GetString("dns.hostname") is { Length: > 0 } dns) return dns;
        }
        catch (KeyNotFoundException)
        {
        }

        return null;
    }

    private async Task<JsonElement?> StatusAsync(ProviderInputs inputs, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "v1/status", inputs, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement.Clone();
            return root.TryGetProperty("schema", out var schema) && schema.GetString() == ContractSchema ? root : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ProviderInputs inputs, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{BaseUrl}/{path}");
        if (inputs.Token.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", inputs.Token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static bool TryId(string body, out string id)
    {
        id = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("schema", out var schema) && schema.GetString() != ContractSchema) return false;
            if (root.TryGetProperty("id", out var raw))
            {
                id = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.ToString();
                return id.Length > 0;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static string Text(AlertMessage message)
    {
        var text = new StringBuilder(message.Message);
        foreach (var field in message.Fields) text.Append('\n').Append(field.Name).Append(": ").Append(field.Value);
        return text.ToString();
    }

    internal static ProviderInputs Normalize(ProviderInputs inputs) => inputs with
    {
        Token = inputs.Token.Trim(),
        Subdomain = inputs.Subdomain.Trim().ToLowerInvariant(),
        ForwardTo = inputs.ForwardTo.Trim(),
    };
}
