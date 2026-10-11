using Microsoft.AspNetCore.Http;
using SboxNetworkStorage.Server.Compatibility;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

public sealed class LibraryCompatibilityTests
{
    internal static LibraryMatrix Matrix() => new(1, null, "1.2.0", "1.3.0", "supported",
        [new("legacy", "<=309133", "supported")],
        [new("<1.1.0", new(2026, 1, 1), new(2025, 10, 1), new(2026, 4, 1), "1.1.0", "https://github.com/sbox-cool/sbox-network-storage-server/issues/57")],
        [new("1.2.1", "Known data-loss defect", "https://github.com/sbox-cool/sbox-network-storage-server/security/advisories/GHSA-example")], [], []);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero); }

    [Theory]
    [InlineData("legacy", "unknown")]
    [InlineData("garbage", "unknown")]
    [InlineData("1.0.0", "deprecated")]
    [InlineData("1.1.0", "outdated")]
    [InlineData("1.2.0", "supported")]
    [InlineData("1.2.1", "blocked")]
    [InlineData("1.3.0", "supported")]
    public void Status_precedence_preserves_supported_older_clients(string version, string expected)
        => Assert.Equal(expected, new LibraryCompatibility(Matrix(), new Clock()).StatusFor(version));

    [Fact]
    public void Raising_minimum_requires_a_completed_deprecation_and_never_blocks_legacy_implicitly()
    {
        var policy = new LibraryCompatibility(Matrix() with { Minimum = "1.1.0" }, new Clock());
        Assert.Equal("unsupported", policy.StatusFor("1.0.0"));
        Assert.Equal("unknown", policy.StatusFor("legacy"));
        Assert.Throws<InvalidDataException>(() => new LibraryCompatibility(Matrix() with { Minimum = "1.2.0" }, new Clock()));
        var future = Matrix() with { Minimum = "1.1.0", Deprecated = [Matrix().Deprecated[0] with { Sunset = new(2027, 1, 1) }] };
        Assert.Throws<InvalidDataException>(() => new LibraryCompatibility(future, new Clock()));
    }

    [Fact]
    public void Short_notice_and_unlinked_emergency_blocks_are_rejected()
    {
        var shortNotice = Matrix() with { Deprecated = [Matrix().Deprecated[0] with { Sunset = new(2026, 1, 2) }] };
        Assert.Throws<InvalidDataException>(() => new LibraryCompatibility(shortNotice, new Clock()));
        Assert.Throws<InvalidDataException>(() => new LibraryCompatibility(Matrix() with { Blocked = [new("1.2.1", "", "http://example.com")] }, new Clock()));
    }

    [Fact]
    public void Recorded_successor_release_cannot_be_backdated_to_shorten_support()
    {
        var releases = Matrix() with { Libraries = [new("1.1.0", null, "supported", 310500, new(2025, 10, 2), "0.5.0")] };
        Assert.Throws<InvalidDataException>(() => new LibraryCompatibility(releases, new Clock()));
        var aligned = releases with { Deprecated = [releases.Deprecated[0] with { SuccessorReleased = new(2025, 10, 2), Sunset = new(2026, 4, 2) }] };
        Assert.Equal("deprecated", new LibraryCompatibility(aligned, new Clock()).StatusFor("1.0.0"));
    }

    [Fact]
    public void Every_range_token_is_validated_before_a_game_request_can_match_it()
    {
        var badRange = Matrix() with { Deprecated = [Matrix().Deprecated[0] with { Range = ">1.0.0 ===2.0.0" }] };
        Assert.Throws<InvalidDataException>(() => new LibraryCompatibility(badRange, new Clock()));
    }

    [Fact]
    public void Header_takes_precedence_over_body_and_invalid_version_is_not_an_error()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-ns-library-version"] = "garbage";
        context.Request.Headers["x-ns-revision-id"] = "42";
        ClientIdentity.ReadHeaders(context);
        ClientIdentity.ReadBody(context, System.Text.Json.JsonSerializer.SerializeToElement(new { libraryVersion = "1.2.0" }));
        Assert.Equal("legacy", ClientIdentity.Get(context).LibraryVersion);
        Assert.Equal(42, ClientIdentity.Get(context).GameRevision);
        context.Request.Headers.Remove("x-ns-library-version");
        ClientIdentity.ReadBody(context, System.Text.Json.JsonSerializer.SerializeToElement(new { networkStorageVersion = "1.2.0" }));
        Assert.Equal("1.2.0", ClientIdentity.Get(context).LibraryVersion);
    }
}
