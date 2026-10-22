using SboxNetworkStorage.Application.NetworkStorage.AuthSessions;
using SboxNetworkStorage.Server.Tests.Support;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Tests.NetworkStorage;

public sealed class NetworkStorageAuthSessionServiceTests
{
    private const string Secret = "unit-test-auth-session-secret";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-06-16T00:00:00Z");

    private static NetworkStorageAuthSessionService Service(out FixedTimeProvider time, string secret = Secret, IAuthSessionRevocationStore? revocations = null)
    {
        time = new FixedTimeProvider(FixedNow);
        return new NetworkStorageAuthSessionService(new FixedSecret(secret), revocations ?? new InMemoryAuthSessionRevocationStore(), time);
    }

    [Fact]
    public async Task Create_ProducesToken_ThatValidates()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        Assert.True(created.Ok);
        Assert.NotNull(created.Token);
        Assert.StartsWith("sbox_sess_", created.Token);
        Assert.Contains('.', created.Token);
        Assert.Equal(3600, created.TtlSeconds);
        Assert.Equal("76561198000000001", created.Session!.SteamId);
        Assert.Equal(42, created.Session.UserId);
        Assert.Null(created.Session.RevokedAt);

        var validated = await service.ValidateAsync("proj-1", created.Token!);
        Assert.True(validated.Ok);
        Assert.Equal("76561198000000001", validated.Session!.SteamId);
        Assert.Equal(42, validated.Session.UserId);
    }

    [Fact]
    public async Task Validate_RejectsTokenForDifferentProject()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var result = await service.ValidateAsync("proj-2", created.Token!);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public async Task Validate_RejectsExpiredToken()
    {
        var service = Service(out var time);
        var created = service.Create(42, "proj-1", "76561198000000001", 60);

        time.Now = FixedNow.AddSeconds(61);
        var result = await service.ValidateAsync("proj-1", created.Token!);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_EXPIRED", result.Code);
    }

    [Fact]
    public async Task Validate_RejectsTamperedSignature()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);
        var last = created.Token![^1];
        var tampered = created.Token[..^1] + (last == 'A' ? 'B' : 'A');

        var result = await service.ValidateAsync("proj-1", tampered);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public async Task Validate_RejectsSteamIdMismatch_WhenSteamIdSupplied()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var result = await service.ValidateAsync("proj-1", created.Token!, steamId: "76561198000000002");

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_STEAMID_MISMATCH", result.Code);
    }

    [Fact]
    public async Task Validate_RejectsMalformedToken()
    {
        var service = Service(out _);
        Assert.Equal("AUTH_SESSION_INVALID", (await service.ValidateAsync("proj-1", "not-a-session-token")).Code);
        Assert.Equal("AUTH_SESSION_INVALID", (await service.ValidateAsync("proj-1", "sbox_sess_nodothere")).Code);
        Assert.Equal("AUTH_SESSION_INVALID", (await service.ValidateAsync("proj-1", "")).Code);
    }

    [Fact]
    public async Task Validate_RejectsTokenSignedWithDifferentSecret()
    {
        var minter = Service(out _, secret: "secret-A");
        var created = minter.Create(42, "proj-1", "76561198000000001", 3600);

        var verifier = new NetworkStorageAuthSessionService(new FixedSecret("secret-B"), new InMemoryAuthSessionRevocationStore(), new FixedTimeProvider(FixedNow));
        var result = await verifier.ValidateAsync("proj-1", created.Token!);

        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public async Task Refresh_MintsNewTokenForSamePlayer()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var refreshed = await service.RefreshAsync("proj-1", created.Token!, 1800);

        Assert.True(refreshed.Ok);
        Assert.NotNull(refreshed.Token);
        Assert.NotEqual(created.Token, refreshed.Token);
        Assert.Equal(1800, refreshed.TtlSeconds);
        Assert.Equal("76561198000000001", refreshed.Session!.SteamId);
        Assert.Equal(42, refreshed.Session.UserId);
        Assert.True((await service.ValidateAsync("proj-1", refreshed.Token!)).Ok);
    }

    [Fact]
    public async Task Refresh_KeepsSessionFamily()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var refreshed = await service.RefreshAsync("proj-1", created.Token!, 1800);

        Assert.Equal(created.Session!.Id, refreshed.Session!.Id);
    }

    [Fact]
    public async Task Refresh_RejectsInvalidToken()
    {
        var service = Service(out _);
        var result = await service.RefreshAsync("proj-1", "sbox_sess_bogus.sig", 3600);
        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public async Task Revoke_ReturnsSessionWithRevokedTimestamp()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var result = await service.RevokeAsync("proj-1", created.Token!);

        Assert.True(result.Ok);
        Assert.NotNull(result.Session!.RevokedAt);
    }

    [Fact]
    public async Task Revoke_MakesValidateAndRefreshFail()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        Assert.True((await service.RevokeAsync("proj-1", created.Token!)).Ok);

        var validated = await service.ValidateAsync("proj-1", created.Token!);
        Assert.False(validated.Ok);
        Assert.Equal("AUTH_SESSION_REVOKED", validated.Code);

        var refreshed = await service.RefreshAsync("proj-1", created.Token!, 1800);
        Assert.False(refreshed.Ok);
        Assert.Equal("AUTH_SESSION_REVOKED", refreshed.Code);
        Assert.Null(refreshed.Token);
    }

    [Fact]
    public async Task Revoke_OfRefreshedToken_RevokesWholeFamily()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);
        var refreshed = await service.RefreshAsync("proj-1", created.Token!, 1800);

        Assert.True((await service.RevokeAsync("proj-1", refreshed.Token!)).Ok);

        Assert.Equal("AUTH_SESSION_REVOKED", (await service.ValidateAsync("proj-1", created.Token!)).Code);
        Assert.Equal("AUTH_SESSION_REVOKED", (await service.ValidateAsync("proj-1", refreshed.Token!)).Code);
    }

    [Fact]
    public async Task Revoke_IsIdempotent()
    {
        var service = Service(out _);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        Assert.True((await service.RevokeAsync("proj-1", created.Token!)).Ok);
        Assert.True((await service.RevokeAsync("proj-1", created.Token!)).Ok);
    }

    [Fact]
    public async Task Revoke_RejectsEmptyToken()
    {
        var service = Service(out _);
        var result = await service.RevokeAsync("proj-1", "");
        Assert.False(result.Ok);
        Assert.Equal("AUTH_SESSION_INVALID", result.Code);
    }

    [Fact]
    public async Task Revoke_MarkerInAnotherProject_DoesNotRevokeSession()
    {
        var store = new InMemoryAuthSessionRevocationStore();
        var service = Service(out _, revocations: store);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        await store.RevokeAsync("proj-2", created.Session!.Id, FixedNow.ToUnixTimeMilliseconds(), long.MaxValue, default);

        Assert.True((await service.ValidateAsync("proj-1", created.Token!)).Ok);
    }

    [Fact]
    public async Task Validate_PropagatesStoreFailure()
    {
        var service = Service(out _, revocations: new ThrowingRevocationStore());
        var created = Service(out _).Create(42, "proj-1", "76561198000000001", 3600);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAsync("proj-1", created.Token!));
    }

    [Fact]
    public async Task Refresh_RevokeCommittedDuringMint_FailsWithoutToken()
    {
        var store = new RevokeAfterFirstCheckStore(new InMemoryAuthSessionRevocationStore());
        var service = Service(out _, revocations: store);
        var created = service.Create(42, "proj-1", "76561198000000001", 3600);

        var refreshed = await service.RefreshAsync("proj-1", created.Token!, 1800);

        Assert.False(refreshed.Ok);
        Assert.Equal("AUTH_SESSION_REVOKED", refreshed.Code);
        Assert.Null(refreshed.Token);
    }

    [Fact]
    public async Task Revoke_PurgesMarkersPastRetention()
    {
        var store = new InMemoryAuthSessionRevocationStore();
        var service = Service(out var time, revocations: store);
        var first = service.Create(42, "proj-1", "76561198000000001", 3600);

        await service.RevokeAsync("proj-1", first.Token!);
        Assert.Equal(1, store.Count);

        time.Now = FixedNow.AddSeconds(NetworkStorageAuthSessionService.MaxTtlSeconds + NetworkStorageAuthSessionService.RevocationGraceSeconds + 1);
        var second = service.Create(42, "proj-1", "76561198000000002", 3600);
        await service.RevokeAsync("proj-1", second.Token!);

        Assert.Equal(1, store.Count);
    }

    [Theory]
    [InlineData(10, 60)]      // below min clamps up
    [InlineData(3600, 3600)]  // within range
    [InlineData(999999, 86400)] // above max clamps down
    public void NormalizeTtl_ClampsToBounds(int input, int expected)
        => Assert.Equal(expected, NetworkStorageAuthSessionService.NormalizeTtl(input));

    private sealed class FixedSecret(string secret) : IAuthSessionSecretProvider
    {
        public string GetSecret() => secret;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Fails every store call, so tests can prove store errors reach the caller instead of being swallowed.</summary>
    private sealed class ThrowingRevocationStore : IAuthSessionRevocationStore
    {
        public Task RevokeAsync(string projectId, string sessionId, long revokedAtUnixMs, long purgeAfterUnixMs, CancellationToken ct)
            => throw new InvalidOperationException("store unavailable");

        public Task<bool> IsRevokedAsync(string projectId, string sessionId, CancellationToken ct)
            => throw new InvalidOperationException("store unavailable");

        public Task<int> PurgeExpiredAsync(long nowUnixMs, CancellationToken ct)
            => throw new InvalidOperationException("store unavailable");

        public Task<IReadOnlyList<AuthSessionRevocation>> ListAsync(string projectId, CancellationToken ct)
            => throw new InvalidOperationException("store unavailable");
    }

    /// <summary>
    /// Reports "not revoked" on the first check while a revoke commits right after it. Models a revoke that lands
    /// between a refresh's validate read and its post-mint re-check.
    /// </summary>
    private sealed class RevokeAfterFirstCheckStore(InMemoryAuthSessionRevocationStore inner) : IAuthSessionRevocationStore
    {
        private int _checks;

        public Task RevokeAsync(string projectId, string sessionId, long revokedAtUnixMs, long purgeAfterUnixMs, CancellationToken ct)
            => inner.RevokeAsync(projectId, sessionId, revokedAtUnixMs, purgeAfterUnixMs, ct);

        public async Task<bool> IsRevokedAsync(string projectId, string sessionId, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _checks) == 1)
            {
                await inner.RevokeAsync(projectId, sessionId, 0, long.MaxValue, ct);
                return false;
            }
            return await inner.IsRevokedAsync(projectId, sessionId, ct);
        }

        public Task<int> PurgeExpiredAsync(long nowUnixMs, CancellationToken ct)
            => inner.PurgeExpiredAsync(nowUnixMs, ct);

        public Task<IReadOnlyList<AuthSessionRevocation>> ListAsync(string projectId, CancellationToken ct)
            => inner.ListAsync(projectId, ct);
    }
}
