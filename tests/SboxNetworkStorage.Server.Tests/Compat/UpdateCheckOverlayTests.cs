using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Server.Compat;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>
/// Release-feed overlay trust: overlays apply only from the signed feed, never from the unsigned
/// GitHub fallback, and expire after consecutive checks without one.
/// </summary>
public sealed class UpdateCheckOverlayTests
{
    private const string BlockedVersion = "9.9.9";

    private static string Feed(bool overlay) =>
        """{"version":"1.4.0","channel":"stable","migrationRequired":false,"security":false"""
        + (overlay
            ? ""","libraryCompat":{"blocked":[{"range":"9.9.9","reason":"test block","advisory":"https://github.com/sbox-cool/sbox-network-storage-server/issues/1"}]}}"""
            : "}");

    private static EffectiveConfig Config() => ConfigLoader.Load(
        Path.Combine(Path.GetTempPath(), "sbox-ns-overlay-tests-" + Guid.NewGuid().ToString("N")),
        Path.Combine(Path.GetTempPath(), "sbox-ns-overlay-tests-" + Guid.NewGuid().ToString("N")),
        new Dictionary<string, string> { ["updates.channel"] = "stable" }, _ => null);

    private static UpdateCheckService Service(EffectiveConfig config, LibraryCompatProvider provider)
        => new(config, new UpdateNoticeState(), provider, NullLogger<UpdateCheckService>.Instance);

    private sealed class FeedHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request.RequestUri!.ToString()));
    }

    private static ClientIdentity Id(string version)
    {
        Assert.True(ClientIdentity.TryParseLibraryVersion(version, out var parsed));
        return new ClientIdentity(parsed.ToString(), parsed, null, "game", null, null);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Overlay_applies_from_the_signed_feed()
    {
        using var http = new HttpClient(new FeedHandler(_ => Json(Feed(overlay: true))));
        var config = Config();
        var feed = new ReleaseFeed(http, config);
        var provider = new LibraryCompatProvider(LibraryMatrix.LoadEmbedded());

        var source = await feed.GetReleaseWithSourceAsync(null, CancellationToken.None);

        Assert.True(source.FromSignedFeed);
        Assert.NotNull(source.Release.LibraryCompat);
        await Service(config, provider).CheckOnceAsync(feed, CancellationToken.None);
        Assert.Equal(LibraryStatus.Blocked, provider.Current.Evaluate(Id(BlockedVersion)).Status);
        Assert.True(provider.Current.OverlayApplied);
    }

    [Fact]
    public async Task Overlay_never_applies_from_the_unsigned_github_fallback()
    {
        const string manifest = """{"version":"1.3.0","migrationRequired":false,"security":false,"libraryCompat":{"blocked":[{"range":"9.9.9","reason":"test block"}]}}""";
        using var http = new HttpClient(new FeedHandler(url =>
            url.Contains("sboxcool.com", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : url.Contains("api.github.com", StringComparison.Ordinal) ? Json("""{"tag_name":"v1.3.0","prerelease":false,"html_url":"https://example.invalid/r","assets":[{"name":"release.json","browser_download_url":"https://example.invalid/release.json"}]}""")
            : Json(manifest)));
        var config = Config();
        var feed = new ReleaseFeed(http, config);
        var provider = new LibraryCompatProvider(LibraryMatrix.LoadEmbedded());

        var source = await feed.GetReleaseWithSourceAsync(null, CancellationToken.None);

        Assert.False(source.FromSignedFeed);
        Assert.NotNull(source.Release.LibraryCompat); // The fallback carries one, and it must be ignored.
        await Service(config, provider).CheckOnceAsync(feed, CancellationToken.None);
        Assert.Equal(LibraryStatus.Supported, provider.Current.Evaluate(Id(BlockedVersion)).Status);
        Assert.False(provider.Current.OverlayApplied);
    }

    [Fact]
    public async Task Overlay_expires_after_consecutive_checks_without_one()
    {
        var withOverlay = true;
        using var http = new HttpClient(new FeedHandler(_ => Json(Feed(withOverlay))));
        var config = Config();
        var feed = new ReleaseFeed(http, config);
        var provider = new LibraryCompatProvider(LibraryMatrix.LoadEmbedded());
        var service = Service(config, provider);

        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.Equal(LibraryStatus.Blocked, provider.Current.Evaluate(Id(BlockedVersion)).Status);

        withOverlay = false;
        await service.CheckOnceAsync(feed, CancellationToken.None);
        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.Equal(LibraryStatus.Blocked, provider.Current.Evaluate(Id(BlockedVersion)).Status);

        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.Equal(LibraryStatus.Supported, provider.Current.Evaluate(Id(BlockedVersion)).Status);
        Assert.False(provider.Current.OverlayApplied);
    }

    [Fact]
    public async Task Empty_overlay_withdraws_immediately()
    {
        var body = """{"version":"1.4.0","channel":"stable","migrationRequired":false,"security":false,"libraryCompat":{}}""";
        var withOverlay = true;
        using var http = new HttpClient(new FeedHandler(_ => Json(withOverlay ? Feed(true) : body)));
        var config = Config();
        var feed = new ReleaseFeed(http, config);
        var provider = new LibraryCompatProvider(LibraryMatrix.LoadEmbedded());
        var service = Service(config, provider);

        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.True(provider.Current.OverlayApplied);

        withOverlay = false;
        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.Equal(LibraryStatus.Supported, provider.Current.Evaluate(Id(BlockedVersion)).Status);
        Assert.False(provider.Current.OverlayApplied);
    }

    [Fact]
    public async Task Failed_checks_count_toward_expiry()
    {
        var reachable = true;
        using var http = new HttpClient(new FeedHandler(_ => reachable
            ? Json(Feed(overlay: true))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var config = Config();
        var feed = new ReleaseFeed(http, config);
        var provider = new LibraryCompatProvider(LibraryMatrix.LoadEmbedded());
        var service = Service(config, provider);

        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.True(provider.Current.OverlayApplied);

        reachable = false;
        await service.CheckOnceAsync(feed, CancellationToken.None);
        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.True(provider.Current.OverlayApplied);
        await service.CheckOnceAsync(feed, CancellationToken.None);
        Assert.False(provider.Current.OverlayApplied);
    }

    [Fact]
    public async Task Oversized_feed_responses_are_rejected()
    {
        Assert.Equal(64 * 1024, ReleaseFeed.MaxResponseBytes);
        var padded = "{\"version\":\"1.4.0\",\"channel\":\"stable\",\"migrationRequired\":false,\"security\":false,\"pad\":\"" + new string('x', 70_000) + "\"}";
        using var http = new HttpClient(new FeedHandler(_ => Json(padded)));
        var feed = new ReleaseFeed(http, Config());

        await Assert.ThrowsAsync<InvalidDataException>(() => feed.GetFromFeedAsync(CancellationToken.None));
    }
}
