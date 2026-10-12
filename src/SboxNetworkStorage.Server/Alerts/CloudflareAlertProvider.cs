using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>
/// Third-party outbound alerts and forwarding through Cloudflare Email Sending (outbound) with
/// optional Email Routing (forwarding). Least-privilege API token scopes, all on the zone:
/// <list type="bullet">
/// <item>Email Sending, Edit (required: verify the token, manage sending subdomains, send).</item>
/// <item>Email Routing Rules, Edit (only when forwarding to an operator address is turned on).</item>
/// </list>
/// DNS is only read (the subdomain's required records and their status) so the owner can add
/// them at their DNS; this adapter creates no DNS records itself.
/// <para>Only the paths below are called, all under an injectable base URL
/// (default <c>https://api.cloudflare.com/client/v4</c>):</para>
/// <list type="bullet">
/// <item><c>GET /user/tokens/verify</c> (credential check, creates nothing).</item>
/// <item><c>POST /zones/{zone_id}/email/sending/subdomains {name}</c>.</item>
/// <item><c>GET /zones/{zone_id}/email/sending/subdomains/{subdomain_id}/dns</c> and <c>.../dns/status</c>.</item>
/// <item><c>POST /accounts/{account_id}/email/sending/send {from, to, subject, html, text}</c>.</item>
/// <item>Forwarding, only when asked: <c>POST /zones/{zone_id}/email/routing/addresses</c> and
/// <c>POST /zones/{zone_id}/email/routing/rules</c>.</item>
/// <item>Teardown: <c>DELETE</c> of the same subdomain, rule and address paths, only for recorded ids.</item>
/// </list>
/// </summary>
public sealed class CloudflareAlertProvider : IAlertProvider
{
    public const string ProviderId = "cloudflare";
    public static readonly string DefaultBaseUrl = "https://api.cloudflare.com/client/v4";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public CloudflareAlertProvider(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public string Id => ProviderId;
    public string DisplayName => "Cloudflare Email";
    public ProviderOrigin Origin => ProviderOrigin.ThirdParty;
    public ProviderCapabilities Capabilities => ProviderCapabilities.Send | ProviderCapabilities.Forward;
    public string Description => "Sends alerts through Cloudflare Email Sending and can forward mail through Email Routing. Needs a Cloudflare API token; nothing runs until you set it up.";
    public string BaseUrl { get; }

    public (bool Available, string? Reason) CheckAvailability() => (true, null);

    public string? ValidateInputs(ProviderInputs inputs)
    {
        inputs = Normalize(inputs);
        if (inputs.Token.Length == 0) return "Enter the Cloudflare API token.";
        if (inputs.Token.Length > 1024) return "The API token is too long.";
        if (inputs.AccountId.Length == 0) return "Enter the Cloudflare account ID.";
        if (inputs.ZoneId.Length == 0) return "Enter the Cloudflare zone ID.";
        if (inputs.AccountId.Length > 64 || inputs.ZoneId.Length > 64) return "The account ID or zone ID is too long.";
        if (!IsTokenChars(inputs.AccountId) || !IsTokenChars(inputs.ZoneId)) return "Account and zone IDs use letters, digits, hyphens and underscores.";
        if (!IsSubdomain(inputs.Subdomain)) return "Enter the sending subdomain as one label, for example mail (1-63 letters, digits or hyphens).";
        if (!MailAddress.TryCreate(inputs.FromAddress, out _)) return "Enter a valid sender address, for example alerts@mail.example.com.";
        if (inputs.ForwardTo.Length > 0 && !MailAddress.TryCreate(inputs.ForwardTo, out _)) return "Enter a valid forwarding address or leave it blank.";
        return null;
    }

    public Task<ProviderPlan> PlanSetupAsync(ProviderInputs inputs, CancellationToken cancellationToken)
    {
        inputs = Normalize(inputs);
        var resources = new List<PlannedResource>
        {
            new("subdomain", $"Create the Email Sending subdomain '{inputs.Subdomain}' in zone '{inputs.ZoneId}'."),
        };
        if (inputs.ForwardTo.Length > 0)
        {
            resources.Add(new("routing-address", $"Create the Email Routing destination address '{inputs.ForwardTo}'."));
            resources.Add(new("routing-rule", $"Create the Email Routing rule forwarding '{inputs.FromAddress}' to '{inputs.ForwardTo}'."));
        }

        resources.Add(new("console-channel", "Create the console channel 'Cloudflare Email' so severity and project filters apply."));
        return Task.FromResult(new ProviderPlan(ProviderId, "Cloudflare setup will create",
            resources,
            "DNS records shown after setup must be added at your DNS yourself; this setup only reads them back for verification."));
    }

    public async Task<ProviderApplyResult> ApplySetupAsync(ProviderInputs inputs, CancellationToken cancellationToken)
    {
        inputs = Normalize(inputs);
        var created = new List<CreatedResource>();
        try
        {
            using var verify = await SendAsync(HttpMethod.Get, "user/tokens/verify", inputs, null, cancellationToken);
            if (!verify.IsSuccessStatusCode)
                return Fail($"The API token was rejected (HTTP {(int)verify.StatusCode}). Check the token and try again.", created, inputs);

            using var subdomain = await SendAsync(HttpMethod.Post, $"zones/{inputs.ZoneId}/email/sending/subdomains",
                inputs, new { name = inputs.Subdomain }, cancellationToken);
            var subdomainBody = await subdomain.Content.ReadAsStringAsync(cancellationToken);
            if (!subdomain.IsSuccessStatusCode || !ProviderJson.TryResultId(subdomainBody, out var subdomainId))
                return Fail($"Cloudflare would not create the sending subdomain (HTTP {(int)subdomain.StatusCode}).", created, inputs);
            created.Add(new CreatedResource("subdomain", subdomainId, $"Email Sending subdomain '{inputs.Subdomain}'"));

            var detail = await ReadDnsStatusAsync(inputs, subdomainId, cancellationToken);

            if (inputs.ForwardTo.Length > 0)
            {
                using var address = await SendAsync(HttpMethod.Post, $"zones/{inputs.ZoneId}/email/routing/addresses",
                    inputs, new { email = inputs.ForwardTo }, cancellationToken);
                var addressBody = await address.Content.ReadAsStringAsync(cancellationToken);
                if (!address.IsSuccessStatusCode || !ProviderJson.TryResultId(addressBody, out var addressId))
                    return Fail($"Cloudflare would not create the routing address (HTTP {(int)address.StatusCode}).", created, inputs);
                created.Add(new CreatedResource("routing-address", addressId, $"Email Routing destination '{inputs.ForwardTo}'"));

                using var rule = await SendAsync(HttpMethod.Post, $"zones/{inputs.ZoneId}/email/routing/rules",
                    inputs, new { name = $"sbox-ns alerts {inputs.Subdomain}", address = inputs.ForwardTo }, cancellationToken);
                var ruleBody = await rule.Content.ReadAsStringAsync(cancellationToken);
                if (!rule.IsSuccessStatusCode || !ProviderJson.TryResultId(ruleBody, out var ruleId))
                    return Fail($"Cloudflare would not create the routing rule (HTTP {(int)rule.StatusCode}).", created, inputs);
                created.Add(new CreatedResource("routing-rule", ruleId, $"Email Routing rule to '{inputs.ForwardTo}'"));
            }

            return new ProviderApplyResult(true, null, created, inputs.FromAddress, detail);
        }
        catch (HttpRequestException)
        {
            return Fail("The Cloudflare API is unreachable. Check the server's network connection and try again.", created, inputs);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("The Cloudflare API timed out. Try again.", created, inputs);
        }
    }

    public async Task SendAsync(AlertMessage message, ProviderSendContext context, CancellationToken cancellationToken)
    {
        var inputs = new ProviderInputs(context.Token, context.AccountId, context.ZoneId, context.Subdomain, context.FromAddress, context.ForwardTo);
        var to = inputs.ForwardTo.Length > 0 ? inputs.ForwardTo : inputs.FromAddress;
        var text = new StringBuilder(message.Message);
        foreach (var field in message.Fields) text.Append('\n').Append(field.Name).Append(": ").Append(field.Value);
        var subject = message.Subject.Length <= 200 ? message.Subject : message.Subject[..200];
        try
        {
            using var response = await SendAsync(HttpMethod.Post, $"accounts/{inputs.AccountId}/email/sending/send",
                inputs, new { from = inputs.FromAddress, to = new[] { to }, subject, html = ToHtml(message, text.ToString()), text = text.ToString() }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"Cloudflare would not send the alert (HTTP {(int)response.StatusCode}).");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("Cloudflare Email Sending is unreachable; the alert was not sent.", ex);
        }
    }

    public async Task<ProviderVerifyResult> VerifyAsync(ProviderSendContext context, CancellationToken cancellationToken)
    {
        var inputs = new ProviderInputs(context.Token, context.AccountId, context.ZoneId, context.Subdomain, context.FromAddress, context.ForwardTo);
        try
        {
            using var verify = await SendAsync(HttpMethod.Get, "user/tokens/verify", inputs, null, cancellationToken);
            if (!verify.IsSuccessStatusCode)
                return new ProviderVerifyResult(false, $"The API token was rejected (HTTP {(int)verify.StatusCode}).");
            return new ProviderVerifyResult(true, "The API token is valid.");
        }
        catch (HttpRequestException)
        {
            return new ProviderVerifyResult(false, "The Cloudflare API is unreachable.");
        }
    }

    public async Task<ProviderTeardownResult> TeardownAsync(ProviderTeardownContext context, CancellationToken cancellationToken)
    {
        var inputs = new ProviderInputs(context.Context.Token, context.Context.AccountId, context.Context.ZoneId,
            context.Context.Subdomain, context.Context.FromAddress, context.Context.ForwardTo);
        var removed = 0;
        foreach (var resource in context.Created.AsEnumerable().Reverse())
        {
            var path = resource.Kind switch
            {
                "subdomain" => $"zones/{inputs.ZoneId}/email/sending/subdomains/{Uri.EscapeDataString(resource.Id)}",
                "routing-rule" => $"zones/{inputs.ZoneId}/email/routing/rules/{Uri.EscapeDataString(resource.Id)}",
                "routing-address" => $"zones/{inputs.ZoneId}/email/routing/addresses/{Uri.EscapeDataString(resource.Id)}",
                _ => null,
            };
            if (path is null) continue;
            try
            {
                using var response = await SendAsync(HttpMethod.Delete, path, inputs, null, cancellationToken);
                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound) removed++;
                else return new ProviderTeardownResult(false, $"Cloudflare would not delete {DescribeKind(resource.Kind)} (HTTP {(int)response.StatusCode}).", removed);
            }
            catch (HttpRequestException)
            {
                return new ProviderTeardownResult(false, "The Cloudflare API is unreachable; some resources may remain.", removed);
            }
        }

        return new ProviderTeardownResult(true, null, removed);
    }

    private static string DescribeKind(string kind) => kind switch
    {
        "subdomain" => "the sending subdomain",
        "routing-rule" => "the routing rule",
        "routing-address" => "the routing address",
        _ => "the resource",
    };

    private async Task<string?> ReadDnsStatusAsync(ProviderInputs inputs, string subdomainId, CancellationToken cancellationToken)
    {
        try
        {
            using var status = await SendAsync(HttpMethod.Get,
                $"zones/{inputs.ZoneId}/email/sending/subdomains/{Uri.EscapeDataString(subdomainId)}/dns/status",
                inputs, null, cancellationToken);
            if (!status.IsSuccessStatusCode) return "The subdomain was created; its DNS records could not be read back.";
            var body = await status.Content.ReadAsStringAsync(cancellationToken);
            return $"The subdomain was created. Add the shown DNS records at your DNS; status: {SummarizeDns(body)}.";
        }
        catch (HttpRequestException)
        {
            return "The subdomain was created; its DNS records could not be read back.";
        }
    }

    private static string SummarizeDns(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ToString().Length > 200 ? "see the Cloudflare dashboard" : json.RootElement.ToString();
        }
        catch (JsonException)
        {
            return "see the Cloudflare dashboard";
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ProviderInputs inputs, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{BaseUrl}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", inputs.Token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private ProviderApplyResult Fail(string error, List<CreatedResource> created, ProviderInputs inputs)
        => new(false, error, created, inputs.FromAddress, created.Count == 0 ? null : "Some resources were created before the failure; tearing down removes them.");

    internal static ProviderInputs Normalize(ProviderInputs inputs) => inputs with
    {
        Token = inputs.Token.Trim(),
        AccountId = inputs.AccountId.Trim(),
        ZoneId = inputs.ZoneId.Trim(),
        Subdomain = inputs.Subdomain.Trim().ToLowerInvariant(),
        FromAddress = inputs.FromAddress.Trim(),
        ForwardTo = inputs.ForwardTo.Trim(),
    };

    private static bool IsTokenChars(string value) => value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static bool IsSubdomain(string value)
        => value.Length is >= 1 and <= 63 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string ToHtml(AlertMessage message, string text)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br/>", StringComparison.Ordinal);
        return $"<p><strong>{System.Net.WebUtility.HtmlEncode(message.Title)}</strong></p><p>{encoded}</p>";
    }
}
