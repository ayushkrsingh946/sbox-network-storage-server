using System.Text.Json;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Compatibility;

public sealed record LibraryEntry(string Version, string? Revisions, string Status,
    long? Revision = null, DateOnly? Released = null, string? MinServer = null);
public sealed record LibraryDeprecation(string Range, DateOnly Announced, DateOnly SuccessorReleased, DateOnly Sunset, string Replacement, string Issue);
public sealed record LibraryBlock(string Range, string Reason, string Advisory);
public sealed record LibraryTestEvidence(string Server, string ServerCommit, string Library, long? Revision, string Engine,
    string[] Database, string Result, string[] Evidence);
public sealed record LibraryIncident(long Revision, string Engine, string Scope, string Reason, string[] Evidence);
public sealed record LibraryMatrix(int SchemaVersion, string? Minimum, string? Recommended, string? Latest, string Legacy,
    LibraryEntry[] Libraries, LibraryDeprecation[] Deprecated, LibraryBlock[] Blocked, LibraryTestEvidence[] Tested, LibraryIncident[] KnownBroken);

/// <summary>Immutable, offline compatibility policy shipped with the release. No unsigned remote kill switch.</summary>
public sealed class LibraryCompatibility
{
    public const string MatrixUrl = "https://github.com/sbox-cool/sbox-network-storage-server/blob/main/compat/library-matrix.json";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public LibraryMatrix Matrix { get; }
    private readonly TimeProvider _time;

    public LibraryCompatibility(TimeProvider time) : this(LoadEmbedded(), time) { }
    public LibraryCompatibility(LibraryMatrix matrix, TimeProvider time)
    {
        Validate(matrix);
        Matrix = matrix;
        _time = time;
        if (matrix.Minimum is { } minimum) ValidateRemoval("<" + minimum);
    }

    private static LibraryMatrix LoadEmbedded()
    {
        using var source = typeof(LibraryCompatibility).Assembly.GetManifestResourceStream("SboxNetworkStorage.Server.Compatibility.library-matrix.json")
            ?? throw new InvalidOperationException("Embedded library compatibility matrix is missing.");
        return JsonSerializer.Deserialize<LibraryMatrix>(source, Json) ?? throw new InvalidDataException("Empty compatibility matrix.");
    }

    public string StatusFor(string libraryVersion)
    {
        if (!ClientIdentity.TryVersion(libraryVersion, out var version)) return "unknown";
        if (Matrix.Blocked.Any(block => Matches(block.Range, version))) return "blocked";
        if (Matrix.Minimum is { } minimum && version.CompareTo(Parse(minimum)) < 0) return "unsupported";
        if (Matrix.Deprecated.Any(rule => Matches(rule.Range, version))) return "deprecated";
        return Matrix.Recommended is { } recommended && version.CompareTo(Parse(recommended)) < 0 ? "outdated" : "supported";
    }

    public string? SunsetFor(string libraryVersion)
        => ClientIdentity.TryVersion(libraryVersion, out var version)
            ? Matrix.Deprecated.FirstOrDefault(rule => Matches(rule.Range, version))?.Sunset.ToString("yyyy-MM-dd") : null;

    public LibraryBlock? BlockFor(string version)
        => ClientIdentity.TryVersion(version, out var parsed) ? Matrix.Blocked.FirstOrDefault(rule => Matches(rule.Range, parsed)) : null;

    public object Advertisement(string serverVersion) => new
    {
        library = new { Matrix.Minimum, Matrix.Recommended, Matrix.Latest, Matrix.Legacy, Matrix.Deprecated, Matrix.Blocked,
            tested = Matrix.Tested.Where(row => row.Server == serverVersion).ToArray() },
        matrixUrl = MatrixUrl
    };

    public void ValidateRemoval(string range)
    {
        var rule = Matrix.Deprecated.SingleOrDefault(rule => rule.Range == range)
            ?? throw new InvalidDataException("A support removal needs an announced deprecation entry.");
        if (DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime) < rule.Sunset)
            throw new InvalidDataException("Library support cannot be removed before the deprecation sunset.");
    }

    public static void Validate(LibraryMatrix matrix)
    {
        if (matrix.SchemaVersion != 1 || matrix.Legacy != "supported") throw new InvalidDataException("Matrix schema must be 1; legacy games remain supported.");
        foreach (var value in new[] { matrix.Minimum, matrix.Recommended, matrix.Latest }) if (value is not null) _ = Parse(value);
        if (matrix.Minimum is { } min && matrix.Recommended is { } rec && Parse(min).CompareTo(Parse(rec)) > 0
            || matrix.Recommended is { } recommended && matrix.Latest is { } latest && Parse(recommended).CompareTo(Parse(latest)) > 0)
            throw new InvalidDataException("Expected minimum <= recommended <= latest.");
        foreach (var library in matrix.Libraries)
        {
            if (library.Version != "legacy") _ = Parse(library.Version);
            if (library.MinServer is not null) _ = Parse(library.MinServer);
            if (library.Revision is <= 0) throw new InvalidDataException("Library package revisions must be positive.");
        }
        foreach (var rule in matrix.Deprecated)
        {
            ValidateRange(rule.Range);
            if (rule.Sunset < rule.Announced.AddDays(90) || rule.Sunset < rule.SuccessorReleased.AddMonths(6))
                throw new InvalidDataException("Deprecation must allow 90 days notice and six months after the successor release.");
            if (string.IsNullOrWhiteSpace(rule.Replacement) || !Https(rule.Issue)) throw new InvalidDataException("Deprecation requires a replacement and linked issue.");
            var successor = matrix.Libraries.FirstOrDefault(library => library.Version == rule.Replacement && library.Released is not null);
            if (successor?.Released is { } released && released != rule.SuccessorReleased)
                throw new InvalidDataException("Deprecation successor date must match the recorded library release.");
        }
        foreach (var block in matrix.Blocked)
        {
            ValidateRange(block.Range);
            if (string.IsNullOrWhiteSpace(block.Reason) || !Https(block.Advisory)) throw new InvalidDataException("A library block needs a concrete reason and HTTPS advisory.");
        }
        foreach (var test in matrix.Tested)
        {
            if (!ClientIdentity.TryVersion(test.Server, out _) || test.ServerCommit.Length != 40 || !test.ServerCommit.All(Uri.IsHexDigit)
                || test.Library != "legacy" && !ClientIdentity.TryVersion(test.Library, out _)
                || string.IsNullOrWhiteSpace(test.Engine) || test.Database.Length == 0 || test.Database.Any(db => db is not ("sqlite" or "postgres"))
                || test.Result is not ("pass" or "fail" or "ci-only") || test.Evidence.Length == 0 || test.Evidence.Any(link => !Https(link)))
                throw new InvalidDataException("Test evidence needs immutable server SHA, version pair, engine, databases, result and evidence links.");
        }
    }

    private static bool Https(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    private static SemanticVersion Parse(string value) => ClientIdentity.TryVersion(value, out var version) ? version : throw new InvalidDataException($"Invalid library version: {value}");
    public static void ValidateRange(string range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Length > 256) throw new InvalidDataException("Invalid library version range.");
        foreach (var token in range.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var opLength = token.StartsWith("<=") || token.StartsWith(">=") ? 2
                : token.StartsWith('<') || token.StartsWith('>') || token.StartsWith('=') ? 1 : 0;
            _ = Parse(token[opLength..]);
        }
    }
    public static bool Matches(string range, SemanticVersion version)
    {
        foreach (var token in range.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var op = token.StartsWith("<=") || token.StartsWith(">=") ? token[..2]
                : token.StartsWith('<') || token.StartsWith('>') || token.StartsWith('=') ? token[..1] : "";
            var cmp = version.CompareTo(Parse(token[op.Length..]));
            if (!(op switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, ">=" => cmp >= 0, "" or "=" => cmp == 0, _ => false })) return false;
        }
        return true;
    }
}
