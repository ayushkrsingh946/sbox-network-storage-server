using System.Globalization;
using System.Text.Json;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// How a library build stands against this server. Ordered from least to most strict, so "stricter" is
/// a plain comparison. <see cref="Unknown"/> is the wire status for a client that did not say which library it is
/// (<see cref="ClientIdentity.Legacy"/>); it never comes out of a version range.
/// </summary>
public enum LibraryStatus
{
    Unknown = -1,
    Supported = 0,
    Outdated = 1,
    Deprecated = 2,
    Unsupported = 3,
    Blocked = 4,
}

public static class LibraryStatusNames
{
    public static string ToWire(this LibraryStatus status) => status switch
    {
        LibraryStatus.Supported => "supported",
        LibraryStatus.Outdated => "outdated",
        LibraryStatus.Deprecated => "deprecated",
        LibraryStatus.Unsupported => "unsupported",
        LibraryStatus.Blocked => "blocked",
        _ => "unknown",
    };

    public static bool TryParse(string? text, out LibraryStatus status)
    {
        status = LibraryStatus.Supported;
        switch (text)
        {
            case "supported": return true;
            case "outdated": status = LibraryStatus.Outdated; return true;
            case "deprecated": status = LibraryStatus.Deprecated; return true;
            case "unsupported": status = LibraryStatus.Unsupported; return true;
            case "blocked": status = LibraryStatus.Blocked; return true;
            default: return false;
        }
    }
}

/// <summary>One known library build or bucket in <c>compat/library-matrix.json</c> <c>libraries[]</c>.</summary>
/// <param name="Version">SemVer, or <c>legacy</c> for every build that sends no version (revisions up to 309133).</param>
/// <param name="Status"><c>supported</c>, <c>deprecated</c>, <c>unsupported</c> or <c>known-broken</c>.</param>
/// <param name="Engine">For <c>known-broken</c>: the engine builds it breaks on, e.g. <c>&gt;=26.10</c>.</param>
public sealed record MatrixLibrary(
    string Version,
    long? Revision,
    string? Revisions,
    string? Released,
    string? MinServer,
    string Status,
    string? Sunset,
    string? Engine,
    string? Reason,
    string? Issue,
    string? Notes);

/// <summary>A version range with the date or reason attached: <c>deprecated[]</c> and <c>blocked[]</c>.</summary>
public sealed record MatrixRule(string Range, string? Sunset, string? Reason, string? Advisory);

/// <summary>The verdict for one request: the status plus the extra header values that go with it.</summary>
public sealed record LibraryCompatResult(LibraryStatus Status, string? Latest, string? Sunset, string? Reason, string? Advisory);

/// <summary>
/// The library compatibility policy: <c>compat/library-matrix.json</c>, embedded in the binary. Pure data and
/// pure functions, so every status can be table-tested without a server. Raw <see cref="Tested"/> rows are
/// passed through to <c>/v3/server-info</c> unchanged.
/// </summary>
public sealed record LibraryMatrix(
    string? Minimum,
    string? Recommended,
    IReadOnlyList<MatrixLibrary> Libraries,
    IReadOnlyList<MatrixRule> Deprecated,
    IReadOnlyList<MatrixRule> Blocked,
    IReadOnlyList<JsonElement> Tested)
{
    public const string ResourceName = "SboxNetworkStorage.Server.Compat.library-matrix.json";
    public const string MatrixUrl = "https://github.com/sbox-cool/sbox-network-storage-server/blob/main/compat/library-matrix.json";

    /// <summary>
    /// Deprecations added by the release-feed overlay (<see cref="Merge"/>). Kept apart from the shipped
    /// <see cref="Deprecated"/> rules so a feed date never replaces or undercuts an announced sunset.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<MatrixRule> FeedDeprecated { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The matrix compiled into this binary.</summary>
    public static LibraryMatrix LoadEmbedded()
    {
        using var stream = typeof(LibraryMatrix).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return Parse(stream);
    }

    public static LibraryMatrix Parse(Stream json) => Normalize(JsonSerializer.Deserialize<LibraryMatrix>(json, JsonOptions));

    public static LibraryMatrix Parse(string json) => Normalize(JsonSerializer.Deserialize<LibraryMatrix>(json, JsonOptions));

    /// <summary>Fills missing arrays and rejects a matrix whose versions or ranges this server cannot read.</summary>
    private static LibraryMatrix Normalize(LibraryMatrix? parsed)
    {
        var matrix = (parsed ?? throw new InvalidDataException("The library matrix is empty.")) with
        {
            Libraries = parsed.Libraries ?? [],
            Deprecated = parsed.Deprecated ?? [],
            Blocked = parsed.Blocked ?? [],
            Tested = parsed.Tested ?? [],
        };

        if (matrix.Minimum is not null && !ClientIdentity.TryParseLibraryVersion(matrix.Minimum, out _))
            throw new InvalidDataException($"minimum '{matrix.Minimum}' is not a SemVer version.");
        if (matrix.Recommended is not null && !ClientIdentity.TryParseLibraryVersion(matrix.Recommended, out _))
            throw new InvalidDataException($"recommended '{matrix.Recommended}' is not a SemVer version.");
        foreach (var rule in matrix.Deprecated.Concat(matrix.Blocked))
        {
            if (!VersionRange.TryParse(rule.Range, out var range) || !range.IsLibraryRange)
                throw new InvalidDataException($"Range '{rule.Range}' is not a library version range.");
        }

        foreach (var rule in matrix.Deprecated)
        {
            if (!IsDate(rule.Sunset)) throw new InvalidDataException($"deprecated range '{rule.Range}' needs a sunset date (yyyy-MM-dd).");
        }

        foreach (var library in matrix.Libraries)
        {
            if (library.Version != ClientIdentity.Legacy && !ClientIdentity.TryParseLibraryVersion(library.Version, out _))
                throw new InvalidDataException($"Library version '{library.Version}' is not SemVer or 'legacy'.");
            if (!IsEntryStatus(library.Status)) throw new InvalidDataException($"Library '{library.Version}' has unknown status '{library.Status}'.");
            if (library.Engine is not null && (!VersionRange.TryParse(library.Engine, out var engine) || !engine.IsEngineRange))
                throw new InvalidDataException($"Library '{library.Version}' engine range '{library.Engine}' is not an engine build range.");
        }

        return matrix;
    }

    private static bool IsEntryStatus(string status) => status is "supported" or "deprecated" or "unsupported" or "known-broken";

    private static bool IsDate(string? text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>The status of the legacy bucket (builds that send no version). Supported when the matrix has no legacy entry.</summary>
    public MatrixLibrary? LegacyEntry => Libraries.FirstOrDefault(l => l.Version == ClientIdentity.Legacy);

    public LibraryStatus LegacyStatus => LegacyEntry is { } entry && LibraryStatusNames.TryParse(entry.Status, out var status) ? status : LibraryStatus.Supported;

    /// <summary>The newest SemVer library the matrix knows, never below <see cref="Recommended"/>.</summary>
    public string? Latest
    {
        get
        {
            SemanticVersion? best = null;
            foreach (var library in Libraries)
            {
                if (library.Status != "supported" || !ClientIdentity.TryParseLibraryVersion(library.Version, out var version)) continue;
                if (best is null || version.CompareTo(best) > 0) best = version;
            }

            if (Recommended is not null && ClientIdentity.TryParseLibraryVersion(Recommended, out var recommended)
                && (best is null || recommended.CompareTo(best) > 0))
            {
                best = recommended;
            }

            return best?.ToString();
        }
    }

    /// <summary>Evaluates one client against the matrix. A request without a library version is <see cref="LibraryStatus.Unknown"/>.</summary>
    public LibraryCompatResult Evaluate(ClientIdentity identity)
    {
        var latest = Latest;
        if (identity.Library is not { } version)
        {
            var legacy = LegacyEntry;
            return LegacyStatus switch
            {
                LibraryStatus.Supported => new LibraryCompatResult(LibraryStatus.Unknown, null, null, null, null),
                var status => new LibraryCompatResult(status, latest, status == LibraryStatus.Deprecated ? legacy?.Sunset : null, legacy?.Reason, null),
            };
        }

        var verdict = new LibraryCompatResult(LibraryStatus.Supported, null, null, null, null);
        void Consider(LibraryStatus status, string? sunset, string? reason, string? advisory)
        {
            if (status > verdict.Status)
            {
                verdict = new LibraryCompatResult(status, null, sunset, reason, advisory);
            }
            else if (status == verdict.Status && status == LibraryStatus.Deprecated
                && sunset is not null && (verdict.Sunset is null || string.CompareOrdinal(sunset, verdict.Sunset) < 0))
            {
                // Several deprecation rules match: the earliest date is the one that matters.
                verdict = verdict with { Sunset = sunset };
            }
        }

        if (Minimum is not null && ClientIdentity.TryParseLibraryVersion(Minimum, out var minimum) && version.CompareTo(minimum) < 0)
            Consider(LibraryStatus.Unsupported, null, $"Library {version} is older than the minimum {minimum}.", null);
        if (Recommended is not null && ClientIdentity.TryParseLibraryVersion(Recommended, out var recommended) && version.CompareTo(recommended) < 0)
            Consider(LibraryStatus.Outdated, null, null, null);
        foreach (var rule in Deprecated)
        {
            if (VersionRange.TryParse(rule.Range, out var range) && range.Contains(version)) Consider(LibraryStatus.Deprecated, rule.Sunset, rule.Reason, null);
        }

        foreach (var rule in Blocked)
        {
            if (VersionRange.TryParse(rule.Range, out var range) && range.Contains(version)) Consider(LibraryStatus.Blocked, null, rule.Reason, rule.Advisory);
        }

        foreach (var library in Libraries)
        {
            if (library.Version == ClientIdentity.Legacy) continue;
            var sameBuild = ClientIdentity.TryParseLibraryVersion(library.Version, out var entryVersion) && entryVersion.CompareTo(version) == 0
                || library.Revision is { } revision && revision == identity.LibraryRevision;
            if (!sameBuild) continue;
            switch (library.Status)
            {
                case "deprecated": Consider(LibraryStatus.Deprecated, library.Sunset, library.Reason, null); break;
                case "unsupported": Consider(LibraryStatus.Unsupported, null, library.Reason, null); break;
                case "known-broken" when KnownBrokenApplies(library, identity.EngineVersion): Consider(LibraryStatus.Unsupported, null, library.Reason, null); break;
            }
        }

        // A feed deprecation only dates a version the shipped matrix does not already deprecate, so an announced
        // sunset can never be shortened remotely (several feed rules: the earliest feed date applies).
        if (verdict.Status != LibraryStatus.Deprecated)
        {
            foreach (var rule in FeedDeprecated)
            {
                if (VersionRange.TryParse(rule.Range, out var range) && range.Contains(version)) Consider(LibraryStatus.Deprecated, rule.Sunset, rule.Reason, null);
            }
        }

        // The latest version is reported to anything that should move: outdated or worse. Sunset only goes with deprecated.
        return verdict.Status >= LibraryStatus.Outdated
            ? verdict with { Latest = latest, Sunset = verdict.Status == LibraryStatus.Deprecated ? verdict.Sunset : null }
            : verdict;
    }

    private static bool KnownBrokenApplies(MatrixLibrary library, string? engineVersion)
    {
        if (library.Engine is null) return true;
        return VersionRange.TryParse(library.Engine, out var range)
            && EngineBuild.TryParse(engineVersion, out var engine)
            && range.ContainsEngine(engine);
    }

    /// <summary>The <c>compat</c> block of <c>/v3/server-info</c>.</summary>
    public object ToServerInfo() => new Dictionary<string, object?>
    {
        ["library"] = new Dictionary<string, object?>
        {
            ["minimum"] = Minimum,
            ["recommended"] = Recommended,
            ["latest"] = Latest,
            ["legacy"] = LegacyStatus.ToWire(),
            ["deprecated"] = Deprecated.Concat(FeedDeprecated).Select(r => Compact(("range", r.Range), ("sunset", r.Sunset), ("reason", r.Reason))).ToArray(),
            ["blocked"] = Blocked.Select(r => Compact(("range", r.Range), ("reason", r.Reason), ("advisory", r.Advisory))).ToArray(),
            ["knownBroken"] = Libraries.Where(l => l.Status == "known-broken")
                .Select(l => Compact(("version", l.Version), ("revision", l.Revision), ("engine", l.Engine), ("reason", l.Reason), ("issue", l.Issue))).ToArray(),
            ["tested"] = Tested,
        },
        ["matrixUrl"] = MatrixUrl,
    };

    private static Dictionary<string, object?> Compact(params (string Key, object? Value)[] values)
        => values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value);

    /// <summary>
    /// Applies a <c>libraryCompat</c> overlay from the release feed. The result is never looser than this matrix:
    /// <c>minimum</c> and <c>recommended</c> can only rise, <c>blocked</c> and <c>deprecated</c> only gain entries
    /// (a repeated range keeps its shipped sunset, and a feed date never applies to a version the shipped matrix
    /// already deprecates), and the legacy status can only get stricter. Anything the overlay says that would loosen
    /// the matrix, or that this server cannot read, is ignored.
    /// </summary>
    public LibraryMatrix Merge(LibraryCompatOverlay? overlay)
    {
        if (overlay is null) return this;

        var minimum = Higher(Minimum, overlay.Minimum);
        var recommended = Higher(Recommended, overlay.Recommended);

        var blocked = Blocked.ToList();
        foreach (var rule in overlay.Blocked ?? [])
        {
            if (IsLibraryRange(rule.Range) && !blocked.Any(b => b.Range == rule.Range)) blocked.Add(rule with { Sunset = null });
        }

        var feedDeprecated = new List<MatrixRule>();
        foreach (var rule in overlay.Deprecated ?? [])
        {
            if (!IsLibraryRange(rule.Range) || !IsDate(rule.Sunset)) continue;
            if (Deprecated.Any(d => d.Range == rule.Range) || feedDeprecated.Any(d => d.Range == rule.Range)) continue;
            feedDeprecated.Add(rule with { Advisory = null });
        }

        var libraries = Libraries.ToList();
        // Blocking the whole legacy bucket would be a fleet-wide outage, so the overlay can only deprecate or
        // un-support it; blocks are per version range.
        if (LibraryStatusNames.TryParse(overlay.Legacy, out var legacyStatus) && legacyStatus > LegacyStatus
            && legacyStatus is LibraryStatus.Deprecated or LibraryStatus.Unsupported
            && (legacyStatus != LibraryStatus.Deprecated || IsDate(overlay.LegacySunset)))
        {
            var index = libraries.FindIndex(l => l.Version == ClientIdentity.Legacy);
            var entry = index >= 0 ? libraries[index] : new MatrixLibrary(ClientIdentity.Legacy, null, null, null, null, "supported", null, null, null, null, null);
            entry = entry with { Status = legacyStatus.ToWire(), Sunset = legacyStatus == LibraryStatus.Deprecated ? overlay.LegacySunset : null };
            if (index >= 0) libraries[index] = entry;
            else libraries.Add(entry);
        }

        return this with { Minimum = minimum, Recommended = recommended, Libraries = libraries, Blocked = blocked, FeedDeprecated = feedDeprecated };
    }

    private static bool IsLibraryRange(string? text) => VersionRange.TryParse(text, out var range) && range.IsLibraryRange;

    /// <summary>The higher of two SemVer strings; an unreadable overlay value never replaces the current one.</summary>
    private static string? Higher(string? current, string? candidate)
    {
        if (!ClientIdentity.TryParseLibraryVersion(candidate, out var next)) return current;
        if (!ClientIdentity.TryParseLibraryVersion(current, out var existing)) return current is null ? next.ToString() : current;
        return next.CompareTo(existing) > 0 ? next.ToString() : current;
    }
}

/// <summary>
/// The optional <c>libraryCompat</c> object of the release feed: a way to make a status stricter on servers that
/// have not been updated (for example to block a library with a security problem). See <see cref="LibraryMatrix.Merge"/>.
/// </summary>
public sealed record LibraryCompatOverlay(
    string? Minimum = null,
    string? Recommended = null,
    string? Legacy = null,
    string? LegacySunset = null,
    IReadOnlyList<MatrixRule>? Deprecated = null,
    IReadOnlyList<MatrixRule>? Blocked = null);

/// <summary>The matrix this server evaluates against: the embedded one, tightened by the latest feed overlay.</summary>
public sealed class LibraryCompatProvider
{
    private volatile LibraryMatrix _effective;

    public LibraryCompatProvider() : this(LibraryMatrix.LoadEmbedded()) { }

    public LibraryCompatProvider(LibraryMatrix embedded)
    {
        Embedded = embedded;
        _effective = embedded;
    }

    public LibraryMatrix Embedded { get; }

    public LibraryMatrix Current => _effective;

    /// <summary>Replaces the overlay (null removes it). Always re-merged onto the embedded matrix, so overlays never accumulate.</summary>
    public void SetOverlay(LibraryCompatOverlay? overlay) => _effective = Embedded.Merge(overlay);
}
