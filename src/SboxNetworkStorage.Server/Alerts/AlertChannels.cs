using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>Posts a body to a webhook URL and returns the HTTP status. Network failures throw. Test fakes capture the call.</summary>
public interface IWebhookClient
{
    Task<int> PostAsync(string url, string contentType, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken);
}

/// <summary>Production <see cref="IWebhookClient"/>: 10-second timeout, redirects are not followed.</summary>
public sealed class HttpWebhookClient(IHttpClientFactory httpClientFactory) : IWebhookClient
{
    public const string ClientName = "alerts-webhook";

    public async Task<int> PostAsync(string url, string contentType, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(request, cancellationToken);
        return (int)response.StatusCode;
    }
}

/// <summary>A place alerts go. <see cref="SendAsync"/> throws on failure; the dispatcher catches and reports it.</summary>
public interface IAlertChannel
{
    Task SendAsync(AlertMessage message, CancellationToken cancellationToken);
}

/// <summary>Discord incoming webhook: one embed, caller text as code, no mentions.</summary>
public sealed class DiscordChannel(string webhookUrl, string username, IDiscordWebhookClient client) : IAlertChannel
{
    public async Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        using var payload = BuildPayload(message, username);
        await client.SendAsync(webhookUrl, payload, cancellationToken);
    }

    /// <summary>
    /// Builds the Discord embed. The webhook URL itself is never part of the payload. Route, project
    /// and message can carry caller-chosen text (paths, ids, a client's failure reason), so they are
    /// shown as code, where Discord renders no links or formatting, and no mention pings.
    /// </summary>
    public static JsonDocument BuildPayload(AlertMessage message, string username)
    {
        var text = AlertText.Truncate(message.Message, 1500);
        var description = message.MessageAsCode ? "```\n" + AlertText.Literal(text) + "\n```" : AlertText.Literal(text);
        var fields = message.Fields
            .Where(field => field.Chat)
            .Select(field => new
            {
                name = AlertText.Truncate(field.Name, 256),
                value = field.Code ? AlertText.InlineCode(AlertText.Truncate(field.Value, 250)) : AlertText.Truncate(field.Value, 256),
                inline = field.Inline,
            })
            .ToList();
        var payload = new
        {
            username = string.IsNullOrWhiteSpace(username) ? "sbox-ns" : username,
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = AlertText.Truncate(message.Title, 256),
                    description,
                    color = message.State == "resolved" ? 5763719 : message.Severity switch
                    {
                        AlertSeverity.Info => 3447003,
                        AlertSeverity.Warning => 16705372,
                        _ => 15548997,
                    },
                    timestamp = message.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    fields,
                },
            },
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload));
    }
}

/// <summary>Plain-text mail over any SMTP server.</summary>
public sealed class SmtpChannel(
    string host, int port, bool useTls, string username, string password, string from, IReadOnlyList<string> to,
    ISmtpTransport transport) : IAlertChannel
{
    public Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        var (subject, body) = BuildMessage(message);
        return transport.SendAsync(host, port, useTls, username, password, from, to, subject, body, cancellationToken);
    }

    /// <summary>Builds the plain-text digest. Secrets (passwords, webhook URLs) never appear in it.</summary>
    public static (string Subject, string Body) BuildMessage(AlertMessage message)
    {
        var body = new StringBuilder();
        foreach (var field in message.Fields) body.AppendLine($"{field.Name}: {field.Value}");
        body.AppendLine(CultureInfo.InvariantCulture, $"Time: {message.Timestamp:o}")
            .AppendLine(CultureInfo.InvariantCulture, $"Severity: {AlertMessage.SeverityName(message.Severity)}")
            .AppendLine()
            .AppendLine(message.Message);
        // Header injection: a subject is one line.
        var subject = message.Subject.ReplaceLineEndings(" ");
        return (subject, body.ToString());
    }
}

/// <summary>
/// HTTP webhook with a versioned JSON payload (<c>generic</c>) or a preset for Slack-compatible
/// incoming webhooks, Microsoft Teams, ntfy and Gotify. Generic payloads can be signed with
/// HMAC-SHA256. Network errors, 429 and 5xx are retried with backoff; other 4xx are not.
/// </summary>
public sealed class WebhookChannel(
    string url, string preset, string? signingSecret, IWebhookClient client, TimeProvider time,
    IReadOnlyList<TimeSpan>? retryDelays = null) : IAlertChannel
{
    public const string PayloadSchema = "sbox-ns.alert/v1";
    public const string SignatureHeader = "X-Sbox-Ns-Signature";
    public const string TimestampHeader = "X-Sbox-Ns-Timestamp";
    public static readonly IReadOnlyList<string> Presets = ["generic", "slack", "teams", "ntfy", "gotify"];
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];

    public async Task SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        var (contentType, body, headers) = Build(message, preset, signingSecret, time.GetUtcNow());
        var delays = retryDelays ?? DefaultRetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            int status;
            try
            {
                status = await client.PostAsync(url, contentType, body, headers, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= delays.Count) throw new HttpRequestException(AlertText.Redact(ex.Message, url), null);
                await Task.Delay(delays[attempt], time, cancellationToken);
                continue;
            }

            if (status is >= 200 and < 300) return;
            var retryable = status == 429 || status >= 500;
            if (!retryable || attempt >= delays.Count)
                throw new HttpRequestException($"The webhook answered HTTP {status}.");
            await Task.Delay(delays[attempt], time, cancellationToken);
        }
    }

    public static (string ContentType, byte[] Body, IReadOnlyList<KeyValuePair<string, string>> Headers) Build(
        AlertMessage message, string preset, string? signingSecret, DateTimeOffset now)
    {
        var headers = new List<KeyValuePair<string, string>>();
        switch (preset)
        {
            case "slack":
                return ("application/json", Json(new { text = SlackText(message) }), headers);
            case "teams":
                return ("application/json", Json(new Dictionary<string, object>
                {
                    ["@type"] = "MessageCard",
                    ["@context"] = "https://schema.org/extensions",
                    ["summary"] = AlertText.Truncate(message.Title, 256),
                    ["title"] = AlertText.Truncate(message.Title, 256),
                    ["text"] = TeamsText(message),
                }), headers);
            case "ntfy":
                headers.Add(new("Title", AlertText.HeaderValue(message.Title)));
                headers.Add(new("Priority", message.Severity switch { AlertSeverity.Info => "3", AlertSeverity.Critical => "5", _ => "4" }));
                headers.Add(new("Tags", message.State == "resolved" ? "white_check_mark" : message.Severity >= AlertSeverity.Error ? "rotating_light" : "warning"));
                return ("text/plain; charset=utf-8", Encoding.UTF8.GetBytes(PlainText(message)), headers);
            case "gotify":
                return ("application/json", Json(new
                {
                    title = AlertText.Truncate(message.Title, 256),
                    message = PlainText(message),
                    priority = message.Severity switch { AlertSeverity.Info => 2, AlertSeverity.Warning => 5, AlertSeverity.Error => 7, _ => 9 },
                }), headers);
            default:
                var body = Json(new
                {
                    schema = PayloadSchema,
                    kind = message.Kind,
                    severity = AlertMessage.SeverityName(message.Severity),
                    state = message.State,
                    title = message.Title,
                    message = message.Message,
                    timestamp = message.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    projectId = message.ProjectId,
                    ruleId = message.RuleId,
                    correlationId = message.CorrelationId,
                    server = new { version = BuildInfo.Version },
                    fields = message.Fields.Select(field => new { name = field.Name, value = field.Value }),
                });
                if (!string.IsNullOrEmpty(signingSecret))
                {
                    var stamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    headers.Add(new(TimestampHeader, stamp));
                    headers.Add(new(SignatureHeader, "sha256=" + Sign(signingSecret, stamp, body)));
                }

                return ("application/json", body, headers);
        }
    }

    /// <summary>HMAC-SHA256 over <c>timestamp + "." + body</c>, lowercase hex. Receivers recompute and compare in constant time.</summary>
    public static string Sign(string secret, string timestamp, byte[] body)
    {
        var signed = new byte[timestamp.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(timestamp, signed);
        signed[timestamp.Length] = (byte)'.';
        body.CopyTo(signed, timestamp.Length + 1);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed));
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static string PlainText(AlertMessage message)
    {
        var text = new StringBuilder(AlertText.Truncate(message.Message, 2000)).AppendLine().AppendLine();
        foreach (var field in message.Fields.Where(f => f.Chat)) text.AppendLine($"{field.Name}: {field.Value}");
        return text.ToString().TrimEnd();
    }

    // Slack mrkdwn: escape &, < and > so caller text cannot form links or mentions.
    private static string SlackEscape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string SlackText(AlertMessage message)
    {
        var text = new StringBuilder("*").Append(SlackEscape(message.Title.Replace('*', '∗'))).AppendLine("*");
        var body = SlackEscape(AlertText.Literal(AlertText.Truncate(message.Message, 1500)));
        text.AppendLine(message.MessageAsCode ? "```" + body + "```" : body);
        foreach (var field in message.Fields.Where(f => f.Chat))
            text.Append(SlackEscape(field.Name)).Append(": ").AppendLine(field.Code ? AlertText.InlineCode(SlackEscape(field.Value)) : SlackEscape(field.Value));
        return text.ToString().TrimEnd();
    }

    // Teams renders markdown; code spans keep caller text from forming links.
    private static string TeamsText(AlertMessage message)
    {
        var text = new StringBuilder();
        var body = AlertText.Literal(AlertText.Truncate(message.Message, 1500));
        text.Append(message.MessageAsCode ? "`" + body.ReplaceLineEndings(" ") + "`" : System.Net.WebUtility.HtmlEncode(body)).Append("\n\n");
        foreach (var field in message.Fields.Where(f => f.Chat))
            text.Append(System.Net.WebUtility.HtmlEncode(field.Name)).Append(": ")
                .Append(field.Code ? AlertText.InlineCode(field.Value) : System.Net.WebUtility.HtmlEncode(field.Value)).Append("\n\n");
        return text.ToString().TrimEnd();
    }
}

/// <summary>Text helpers shared by the channels.</summary>
internal static class AlertText
{
    public static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength] + "…";

    // A backtick would close the code span or block early; the look-alike U+02CB keeps the text readable.
    public static string Literal(string value) => value.Replace('`', 'ˋ');

    public static string InlineCode(string value) => "`" + Literal(value) + "`";

    /// <summary>HTTP header values are one line of ASCII.</summary>
    public static string HeaderValue(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 200));
        foreach (var c in value.AsSpan(0, Math.Min(value.Length, 200)))
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        return builder.ToString();
    }

    /// <summary>Keeps a secret URL out of an error message shown in the console.</summary>
    public static string Redact(string text, string secretUrl)
    {
        if (string.IsNullOrEmpty(secretUrl)) return text;
        text = text.Replace(secretUrl, "[webhook URL]", StringComparison.Ordinal);
        return Uri.TryCreate(secretUrl, UriKind.Absolute, out var uri) && uri.PathAndQuery.Length > 1
            ? text.Replace(uri.PathAndQuery, "/[redacted]", StringComparison.Ordinal)
            : text;
    }
}
