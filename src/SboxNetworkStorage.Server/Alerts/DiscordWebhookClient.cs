using System.Net.Http.Json;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>Posts a JSON payload to a Discord webhook URL. Test fakes capture the call.</summary>
public interface IDiscordWebhookClient
{
    Task SendAsync(string webhookUrl, JsonDocument payload, CancellationToken cancellationToken);
}

/// <summary>Production <see cref="IDiscordWebhookClient"/> over the shared HTTP stack.</summary>
public sealed class HttpDiscordWebhookClient(IHttpClientFactory httpClientFactory) : IDiscordWebhookClient
{
    public async Task SendAsync(string webhookUrl, JsonDocument payload, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("alerts-discord");
        using var response = await client.PostAsync(webhookUrl, JsonContent.Create(payload.RootElement), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
