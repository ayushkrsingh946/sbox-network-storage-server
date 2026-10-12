using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SboxNetworkStorage.Server.Alerts;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Monitoring;
using SboxNetworkStorage.Server.Tests.Hosting;
using SboxNetworkStorage.Server.Tests.Support;
using Xunit;

namespace SboxNetworkStorage.Server.Tests.Monitoring;

/// <summary>
/// Optional providers (issue #72, section 4): plans, apply/teardown accounting, secret hygiene and
/// dispatcher routing. Every remote call goes to a fake handler; no network is used.
/// </summary>
public sealed class AlertProvidersTests : IDisposable
{
    private const string Token = "SECRET-TOKEN-123";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sbox-ns-provider-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _http = new();

    public AlertProvidersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task CloudflarePlan_ListsExactlyWhatSetupCreates()
    {
        var provider = Cloudflare();
        var inputs = new ProviderInputs(Token, "acct", "zone", "mail", "alerts@mail.example.com", "ops@example.com");

        var plan = await provider.PlanSetupAsync(inputs, CancellationToken.None);

        Assert.Equal(["subdomain", "routing-address", "routing-rule", "console-channel"],
            plan.Resources.Select(r => r.Kind));
        Assert.Contains("mail", plan.Resources[0].Description);
        Assert.Contains("ops@example.com", plan.Resources[2].Description);

        var bare = await provider.PlanSetupAsync(inputs with { ForwardTo = "" }, CancellationToken.None);
        Assert.Equal(["subdomain", "console-channel"], bare.Resources.Select(r => r.Kind));
    }

    [Fact]
    public async Task CloudflareApply_RecordsOnlyCreatedResources()
    {
        var provider = Cloudflare();
        _http.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/user/tokens/verify" => CfOk(null),
            var path when path.EndsWith("/email/sending/subdomains") => CfOk("sub_1"),
            var path when path.EndsWith("/email/routing/addresses") => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var inputs = new ProviderInputs(Token, "acct", "zone", "mail", "alerts@mail.example.com", "ops@example.com");

        var result = await provider.ApplySetupAsync(inputs, CancellationToken.None);

        Assert.False(result.Ok);
        var created = Assert.Single(result.Created);
        Assert.Equal(("subdomain", "sub_1"), (created.Kind, created.Id));
        Assert.Equal("alerts@mail.example.com", result.FromAddress);
        var paths = _http.Requests.Select(r => r.Path).ToList();
        Assert.DoesNotContain("/zones/zone/email/routing/rules", paths);
        Assert.DoesNotContain(Token, string.Join("\n", _http.Requests.Select(r => r.Body)));
    }

    [Fact]
    public async Task CloudflareTeardown_DeletesOnlyRecorded_AndIsIdempotentOn404()
    {
        var provider = Cloudflare();
        _http.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/user/tokens/verify" => CfOk(null),
            var path when path.EndsWith("/email/sending/subdomains") => CfOk("sub_1"),
            var path when path.EndsWith("/email/routing/addresses") => CfOk("addr_1"),
            var path when path.EndsWith("/email/routing/rules") => CfOk("rule_1"),
            var path when path.EndsWith("/dns/status") => CfOk(null),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var inputs = new ProviderInputs(Token, "acct", "zone", "mail", "alerts@mail.example.com", "ops@example.com");
        var applied = await provider.ApplySetupAsync(inputs, CancellationToken.None);
        Assert.True(applied.Ok);
        Assert.Equal(3, applied.Created.Count);

        var context = new ProviderTeardownContext(
            new ProviderSendContext("acct", "zone", "mail", "alerts@mail.example.com", "ops@example.com", Token),
            applied.Created);
        _http.Requests.Clear();
        var torn = await provider.TeardownAsync(context, CancellationToken.None);

        Assert.True(torn.Ok);
        Assert.Equal(3, torn.Removed);
        var deletes = _http.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.Path).ToList();
        Assert.Equal([
            "/zones/zone/email/routing/rules/rule_1",
            "/zones/zone/email/routing/addresses/addr_1",
            "/zones/zone/email/sending/subdomains/sub_1",
        ], deletes);

        // Everything is already gone remotely: still a success, nothing else is touched.
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        _http.Requests.Clear();
        var again = await provider.TeardownAsync(context, CancellationToken.None);
        Assert.True(again.Ok);
        Assert.Equal(3, again.Removed);
        Assert.Equal(3, _http.Requests.Count);
    }

    [Fact]
    public async Task FailingProvider_YieldsDeliveryErrorWithoutThrowing()
    {
        var (store, provider) = CloudflareWithStore();
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var dispatcher = Dispatcher(store, provider);

        var deliveries = await dispatcher.SendAsync(
            new AlertMessage(AlertMessage.TestKind, AlertSeverity.Error, "Title", "Subject", "Message",
                [new AlertField("Rule", "r")], DateTimeOffset.UtcNow), null, CancellationToken.None);

        var delivery = Assert.Single(deliveries);
        Assert.False(delivery.Ok);
        Assert.Contains("500", delivery.Error);
    }

    [Fact]
    public async Task ProviderSend_RespectsSeverityAndProjectFilters()
    {
        var store = Store();
        var provider = Cloudflare();
        store.SaveProviderSetup(CloudflareSetup(configured: true), CloudflareSecrets());
        store.SaveChannel(new ChannelDefinition("c1", "CF critical", "cloudflare", "generic",
            AlertSeverity.Critical, "proj-a", true), null);
        var dispatcher = Dispatcher(store, provider);
        _http.Respond = _ => CfOk("msg");

        await dispatcher.SendAsync(Alert(AlertSeverity.Error, "proj-b"), null, CancellationToken.None);
        await dispatcher.SendAsync(Alert(AlertSeverity.Critical, "proj-b"), null, CancellationToken.None);
        Assert.Empty(_http.Requests);

        await dispatcher.SendAsync(Alert(AlertSeverity.Critical, "proj-a"), null, CancellationToken.None);
        var send = Assert.Single(_http.Requests);
        Assert.EndsWith("/accounts/acct/email/sending/send", send.Path);
        Assert.Contains("alerts@mail.example.com", send.Body);
        Assert.Contains("ops@example.com", send.Body);
        Assert.Contains("[sbox-ns]", send.Body);
        Assert.DoesNotContain(Token, send.Body);
    }

    [Fact]
    public async Task Sboxns_ReportsNotAvailable_On404OrUnreachable()
    {
        var sboxns = Sboxns(status: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var inputs = new ProviderInputs("", "", "", "alerts", "", "ops@example.com");

        var apply = await sboxns.ApplySetupAsync(inputs, CancellationToken.None);
        Assert.False(apply.Ok);
        Assert.Contains("not available", apply.Error);
        Assert.Empty(apply.Created);

        var verify = await sboxns.VerifyAsync(SboxnsContext(), CancellationToken.None);
        Assert.False(verify.Ok);
        Assert.Contains("not available", verify.Detail);

        var sendError = await Assert.ThrowsAsync<ProviderException>(() =>
            sboxns.SendAsync(Alert(AlertSeverity.Error, null), SboxnsContext(), CancellationToken.None));
        Assert.Contains("not available", sendError.Message);

        var down = Sboxns(status: _ => throw new HttpRequestException("no route"));
        var downApply = await down.ApplySetupAsync(inputs, CancellationToken.None);
        Assert.False(downApply.Ok);
        Assert.Contains("not available", downApply.Error);

        // Without a tunnel or DNS identity the provider stays unavailable; the plan still lists exact resources.
        var bare = new SboxNsAlertProvider(new HttpClient(_http), Config(), "http://sboxns.test/");
        Assert.False(bare.CheckAvailability().Available);
        var plan = await bare.PlanSetupAsync(inputs, CancellationToken.None);
        Assert.Equal(["forwarding-address", "console-channel"], plan.Resources.Select(r => r.Kind));

        var homed = new SboxNsAlertProvider(new HttpClient(_http), Config(), hostnameOverride: "demo.sboxns.com");
        Assert.True(homed.CheckAvailability().Available);
    }

    [Fact]
    public async Task OwnerPages_SetUpAProvider_TokenNeverAppearsInHtmlFilesOrHistory()
    {
        using var root = new SqliteHostFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IAlertProvider>(new CloudflareAlertProvider(new HttpClient(_http), "http://cloudflare.test/"))));
        _http.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/user/tokens/verify" => CfOk(null),
            var path when path.EndsWith("/email/sending/subdomains") => CfOk("sub_1"),
            var path when path.EndsWith("/email/routing/addresses") => CfOk("addr_1"),
            var path when path.EndsWith("/email/routing/rules") => CfOk("rule_1"),
            _ => CfOk(null),
        };
        await OwnerHttp.CreateOwnerAsync(factory);
        using var client = await OwnerHttp.LoggedInClientAsync(factory);
        var page = await client.GetStringAsync("/dashboard/server/alerts");
        Assert.Contains("Providers", page);
        Assert.Contains("Third party", page);
        Assert.Contains("First party", page);

        var form = OwnerHttp.Form(
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)), ("providerId", "cloudflare"), ("token", Token),
            ("accountId", "acct"), ("zoneId", "zone"), ("subdomain", "mail"),
            ("fromAddress", "alerts@mail.example.com"), ("forwardTo", "ops@example.com"));
        using (var preview = await client.PostAsync("/dashboard/server/alerts/providers/plan", form))
        {
            var html = await preview.Content.ReadAsStringAsync();
            Assert.Contains("Create the Email Sending subdomain", html);
            Assert.DoesNotContain(Token, html);
        }

        using (var apply = await client.PostAsync("/dashboard/server/alerts/providers/apply", OwnerHttp.Form(
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)), ("providerId", "cloudflare"), ("token", Token),
            ("accountId", "acct"), ("zoneId", "zone"), ("subdomain", "mail"),
            ("fromAddress", "alerts@mail.example.com"), ("forwardTo", "ops@example.com"))))
        {
            Assert.Equal(HttpStatusCode.Redirect, apply.StatusCode);
        }

        page = await client.GetStringAsync("/dashboard/server/alerts");
        Assert.Contains("Cloudflare Email", page);
        Assert.Contains("Token saved.", page);
        Assert.DoesNotContain(Token, page);
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(factory.Config.StateDirectory, "monitoring.json")));
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(factory.Config.StateDirectory, "monitoring-history.json")));
        Assert.True(File.Exists(Path.Combine(factory.Config.SecretsDirectory, "monitoring", "provider-cloudflare.json")));

        // Teardown removes only the three recorded resources, then forgets the setup.
        _http.Requests.Clear();
        using (var teardown = await client.PostAsync("/dashboard/server/alerts/providers/teardown", OwnerHttp.Form(
            ("__RequestVerificationToken", OwnerHttp.Csrf(page)), ("id", "cloudflare"))))
        {
            Assert.Equal(HttpStatusCode.Redirect, teardown.StatusCode);
        }

        Assert.Equal(3, _http.Requests.Count(r => r.Method == HttpMethod.Delete));
        page = await client.GetStringAsync("/dashboard/server/alerts");
        Assert.DoesNotContain(Token, page);
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(factory.Config.StateDirectory, "monitoring.json")));
    }

    private CloudflareAlertProvider Cloudflare() => new(new HttpClient(_http), "http://cloudflare.test/");

    private SboxNsAlertProvider Sboxns(Func<HttpRequestMessage, HttpResponseMessage> status)
    {
        _http.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/v1/status")
            ? status(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        return new SboxNsAlertProvider(new HttpClient(_http), Config(), "http://sboxns.test/", hostnameOverride: "demo.sboxns.com");
    }

    private (MonitoringStore Store, CloudflareAlertProvider Provider) CloudflareWithStore()
    {
        var store = Store();
        store.SaveProviderSetup(CloudflareSetup(configured: true), CloudflareSecrets());
        store.SaveChannel(new ChannelDefinition("c1", "CF mail", "cloudflare", "generic", AlertSeverity.Info, null, true), null);
        return (store, Cloudflare());
    }

    private MonitoringStore Store() => new(Config(), NullLogger<MonitoringStore>.Instance);

    private AlertDispatcher Dispatcher(MonitoringStore store, params IAlertProvider[] providers) => new(
        new AlertOptions(false, "", "sbox-ns", false, "", 587, "", "", "", [], true),
        store, new NoDiscord(), new NoSmtp(), new NoWebhooks(), new ManualTime(), NullLogger<AlertDispatcher>.Instance)
    {
        ProviderStore = store,
        Providers = new ProviderRegistry(providers),
    };

    private static ProviderSetup CloudflareSetup(bool configured) => new("cloudflare", configured, "acct", "zone", "mail",
        "alerts@mail.example.com", true, [new CreatedResource("subdomain", "sub_1", "Email Sending subdomain 'mail'")], null, DateTimeOffset.UtcNow);

    private static ProviderSecrets CloudflareSecrets() => new(Token: Token, AccountId: "acct", ZoneId: "zone",
        Subdomain: "mail", FromAddress: "alerts@mail.example.com", ForwardTo: "ops@example.com");

    private static ProviderSendContext SboxnsContext() => new("", "", "alerts", "alerts@demo.sboxns.com", "ops@example.com", "");

    private EffectiveConfig Config() => ConfigLoader.Load(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"), environment: _ => null);

    private static AlertMessage Alert(AlertSeverity severity, string? projectId) => new(
        AlertMessage.RuleKind, severity, "Title", "[sbox-ns] Firing",
        "Message", [new AlertField("Rule", "r")], DateTimeOffset.UtcNow, ProjectId: projectId);

    private static HttpResponseMessage CfOk(string? id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(id is null ? """{"success":true}""" : "{\"success\":true,\"result\":{\"id\":\"" + id + "\"}}",
            Encoding.UTF8, "application/json"),
    };

    public sealed record RecordedRequest(HttpMethod Method, string Path, string Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer now: providers dispose their requests after sending.
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            lock (Requests) Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, body));
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class NoDiscord : IDiscordWebhookClient
    {
        public Task SendAsync(string webhookUrl, JsonDocument payload, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class NoSmtp : ISmtpTransport
    {
        public Task SendAsync(string host, int port, bool useTls, string username, string password, string from, IReadOnlyList<string> to,
            string subject, string body, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class NoWebhooks : IWebhookClient
    {
        public Task<int> PostAsync(string url, string contentType, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
