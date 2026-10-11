using Microsoft.AspNetCore.Http;
using SboxNetworkStorage.Server.Compat;

namespace SboxNetworkStorage.Server.Tests.Compat;

/// <summary>S1: the per-request <see cref="ClientIdentity"/> and its legacy bucket.</summary>
public sealed class ClientIdentityTests
{
    private static ClientIdentity Parse(params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        return ClientIdentityContext.Get(context);
    }

    [Fact]
    public void NoHeadersIsTheLegacyBucket()
    {
        var identity = Parse();
        Assert.Equal("legacy", identity.LibraryVersion);
        Assert.True(identity.IsLegacy);
        Assert.Null(identity.LibraryRevision);
        Assert.Null(identity.ClientType);
        Assert.Null(identity.RevisionId);
        Assert.Null(identity.EngineVersion);
    }

    [Fact]
    public void AllHeadersAreParsed()
    {
        var identity = Parse(
            (ClientHeaders.LibraryVersion, "1.1.0"), (ClientHeaders.LibraryRevision, "310500"), (ClientHeaders.ClientType, "Editor"),
            (ClientHeaders.RevisionId, "41"), (ClientHeaders.EngineVersion, "26.10.02"));
        Assert.Equal("1.1.0", identity.LibraryVersion);
        Assert.False(identity.IsLegacy);
        Assert.Equal(310500, identity.LibraryRevision);
        Assert.Equal("editor", identity.ClientType);
        Assert.Equal(41, identity.RevisionId);
        Assert.Equal("26.10.02", identity.EngineVersion);
    }

    [Theory]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData(" 1.1.0 ", "1.1.0")]
    [InlineData("2.0.0-rc.1", "2.0.0-rc.1")]
    [InlineData("1.2.3+build.5", "1.2.3")]
    public void WellFormedVersionsAreAccepted(string header, string expected)
        => Assert.Equal(expected, Parse((ClientHeaders.LibraryVersion, header)).LibraryVersion);

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]                 // the old hardcoded constant
    [InlineData("1")]
    [InlineData("v1.1.0")]
    [InlineData("01.1.0")]
    [InlineData("1.1.0.0")]
    [InlineData("1.1.x")]
    [InlineData("not a version")]
    [InlineData("99999999999.0.0")]
    [InlineData("1.1.0\u0000")]
    [InlineData("<script>")]
    public void GarbageIsLegacyNotAnError(string header)
        => Assert.Equal("legacy", Parse((ClientHeaders.LibraryVersion, header)).LibraryVersion);

    [Fact]
    public void OverlongVersionIsLegacy()
        => Assert.Equal("legacy", Parse((ClientHeaders.LibraryVersion, "1.0.0-" + new string('a', 80))).LibraryVersion);

    [Theory]
    [InlineData("game", "game")]
    [InlineData("DEDICATED", "dedicated")]
    [InlineData("editor", "editor")]
    [InlineData("console", null)]
    [InlineData("", null)]
    public void ClientTypeIsOneOfTheKnownValues(string header, string? expected)
        => Assert.Equal(expected, Parse((ClientHeaders.ClientType, header)).ClientType);

    [Theory]
    [InlineData("309133", 309133L)]
    [InlineData("0", 0L)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    public void LibraryRevisionIsAnOptionalNonNegativeInteger(string header, long? expected)
        => Assert.Equal(expected, Parse((ClientHeaders.LibraryRevision, header)).LibraryRevision);

    [Theory]
    [InlineData("26.10.02", "26.10.02")]
    [InlineData("26.10.02+abc_1-x", "26.10.02+abc_1-x")]
    [InlineData("  26.10.02  ", "26.10.02")]
    [InlineData("26.10 02", null)]                         // space is outside [0-9A-Za-z._+-]
    [InlineData("26.10.02;DROP TABLE", null)]
    [InlineData("26.10.02\r\nx-evil: 1", null)]
    [InlineData("", null)]
    [InlineData("123456789012345678901234567890123", null)] // 33 characters
    [InlineData("12345678901234567890123456789012", "12345678901234567890123456789012")] // 32 characters
    public void EngineVersionIsSanitizedOrNull(string header, string? expected)
        => Assert.Equal(expected, Parse((ClientHeaders.EngineVersion, header)).EngineVersion);

    [Fact]
    public void GameRevisionHeaderIsParsed()
        => Assert.Equal(42, Parse((ClientHeaders.RevisionId, "42")).RevisionId);

    [Fact]
    public void IdentityIsResolvedOncePerRequestAndCached()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ClientHeaders.LibraryVersion] = "1.1.0";
        var first = ClientIdentityContext.Get(context);
        context.Request.Headers[ClientHeaders.LibraryVersion] = "9.9.9";
        Assert.Same(first, ClientIdentityContext.Get(context));
    }

    [Fact]
    public void BodyVersionFillsInWhenTheHeaderIsMissing()
    {
        var context = new DefaultHttpContext();
        ClientIdentityContext.ApplyBodyLibraryVersion(context, "1.1.0");
        Assert.Equal("1.1.0", ClientIdentityContext.Get(context).LibraryVersion);
    }

    [Fact]
    public void HeaderWinsOverBody()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ClientHeaders.LibraryVersion] = "1.2.0";
        ClientIdentityContext.ApplyBodyLibraryVersion(context, "1.1.0");
        Assert.Equal("1.2.0", ClientIdentityContext.Get(context).LibraryVersion);
    }

    [Theory]
    [InlineData("1.0")]       // the old constant
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    public void OldConstantAndGarbageInTheBodyStayLegacy(string? body)
    {
        var context = new DefaultHttpContext();
        ClientIdentityContext.ApplyBodyLibraryVersion(context, body);
        Assert.Equal("legacy", ClientIdentityContext.Get(context).LibraryVersion);
    }

    [Fact]
    public void JsonBodyPropertiesAreReadInOrderAndNonStringsIgnored()
    {
        var context = new DefaultHttpContext();
        using var number = System.Text.Json.JsonDocument.Parse("""{"libraryVersion":5,"library_version":"1.1.0"}""");
        ClientIdentityContext.ApplyBodyLibraryVersion(context, number.RootElement, "libraryVersion", "library_version");
        Assert.Equal("1.1.0", ClientIdentityContext.Get(context).LibraryVersion);

        var other = new DefaultHttpContext();
        using var array = System.Text.Json.JsonDocument.Parse("""["1.1.0"]""");
        ClientIdentityContext.ApplyBodyLibraryVersion(other, array.RootElement, "libraryVersion");
        ClientIdentityContext.ApplyBodyLibraryVersion(other, (System.Text.Json.JsonElement?)null, "libraryVersion");
        Assert.True(ClientIdentityContext.Get(other).IsLegacy);
    }
}
