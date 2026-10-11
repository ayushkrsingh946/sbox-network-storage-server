using System.Globalization;
using System.Text.Json;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Compatibility;

/// <summary>Self-reported compatibility metadata; never an authentication or authorization claim.</summary>
public sealed partial record ClientIdentity(string LibraryVersion, long? LibraryRevision, string ClientType, long? GameRevision)
{
    private static readonly object Key = new();
    private static readonly ClientIdentity Legacy = new("legacy", null, "unknown", null);
    private static readonly object PlayerKey = new();
    private static readonly string[] VersionFields = ["networkStorageVersion", "libraryVersion", "library_version"];

    public static string? PlayerId(HttpContext context)
        => context.Request.Headers["x-steam-id"].FirstOrDefault() ?? context.Request.Query["steamId"].FirstOrDefault()
            ?? (context.Items.TryGetValue(PlayerKey, out var player) ? player as string : null);

    public static ClientIdentity Get(HttpContext context)
        => context.Items.TryGetValue(Key, out var identity) ? (ClientIdentity)identity! : Legacy;

    public static void ReadHeaders(HttpContext context)
    {
        var headers = context.Request.Headers;
        context.Items[Key] = new ClientIdentity(Normalize(headers["x-ns-library-version"].ToString()),
            Revision(headers["x-ns-library-revision"].ToString()),
            headers["x-ns-client-type"].ToString() is "game" or "editor" or "dedicated" ? headers["x-ns-client-type"].ToString() : "unknown",
            Revision(headers["x-ns-revision-id"].ToString()));
    }

    /// <summary>Called after existing handlers parse their body: no extra request buffering or JSON parse.</summary>
    public static void ReadBody(HttpContext context, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return;
        if (body.TryGetProperty("steamId", out var steam) && steam.ValueKind == JsonValueKind.String)
            context.Items[PlayerKey] = steam.GetString();
        var identity = Get(context);
        var clientType = identity.ClientType;
        var gameRevision = identity.GameRevision;
        var libraryVersion = identity.LibraryVersion;
        if (clientType == "unknown" && body.TryGetProperty("clientType", out var type) && type.ValueKind == JsonValueKind.String
            && type.GetString() is "game" or "editor" or "dedicated") clientType = type.GetString()!;
        if (gameRevision is null && body.TryGetProperty("isPublishedGameBundle", out var published) && published.ValueKind == JsonValueKind.True
            && body.TryGetProperty("revisionId", out var revision) && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var bodyRevision) && bodyRevision > 0)
            gameRevision = bodyRevision;
        if (!context.Request.Headers.ContainsKey("x-ns-library-version"))
        {
            foreach (var name in VersionFields)
            {
                if (body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    libraryVersion = Normalize(value.GetString());
                    break;
                }
            }
        }
        if (libraryVersion != identity.LibraryVersion || clientType != identity.ClientType || gameRevision != identity.GameRevision)
            context.Items[Key] = identity with { LibraryVersion = libraryVersion, ClientType = clientType, GameRevision = gameRevision };
    }

    public static string Normalize(string? value)
        => TryVersion(value, out var version) ? version.ToString() : "legacy";

    public static bool TryVersion(string? value, out SemanticVersion version)
    {
        version = null!;
        // Request headers are untrusted and cardinality-bounded by the analytics collector.
        if (value is null || value.Length > 64 || !VersionPattern().IsMatch(value)) return false;
        return SemanticVersion.TryParse(value, out version);
    }

    private static long? Revision(string value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0 ? revision : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex VersionPattern();
}
