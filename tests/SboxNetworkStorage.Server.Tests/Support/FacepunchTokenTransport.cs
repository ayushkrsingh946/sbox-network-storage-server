using System.Net;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Tests.Support;

/// <summary>
/// Stands in for Facepunch's public token service (<c>https://public.facepunch.com/sbox/auth/token</c>).
/// Each known token resolves to the Steam ID that owns it; any other token is rejected with 401.
/// Counts calls so tests can prove how many upstream verifications a request caused.
/// </summary>
public sealed class FacepunchTokenTransport(IReadOnlyDictionary<string, string> owners) : HttpMessageHandler
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (request.Method != HttpMethod.Post || request.RequestUri?.ToString() != "https://public.facepunch.com/sbox/auth/token")
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var token = body.RootElement.GetProperty("token").GetString() ?? string.Empty;
        if (!owners.TryGetValue(token, out var owner))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"Status\":\"ok\",\"SteamId\":{owner}}}", Encoding.UTF8, "application/json"),
        };
    }
}
