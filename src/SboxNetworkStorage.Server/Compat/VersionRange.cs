using System.Globalization;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// A version constraint from the compatibility matrix: one or more space-separated comparators that must
/// all hold. A comparator is <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c> or <c>=</c> followed by a
/// version; a bare version is an exact match. Examples: <c>1.2.1</c>, <c>&lt;1.2.0</c>, <c>&gt;=1.1.0 &lt;1.3.0</c>.
/// The same grammar is used for library versions (SemVer), s&amp;box engine builds (<c>26.10.02</c>) and
/// package revisions (<c>&lt;=309133</c>); only the operand type differs.
/// </summary>
public sealed class VersionRange
{
    private readonly (string Operator, string Operand)[] _comparators;

    private VersionRange(string text, (string, string)[] comparators)
    {
        Text = text;
        _comparators = comparators;
    }

    public string Text { get; }

    public static bool TryParse(string? text, out VersionRange range)
    {
        range = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var comparators = new List<(string, string)>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var length = token.StartsWith("<=", StringComparison.Ordinal) || token.StartsWith(">=", StringComparison.Ordinal) ? 2
                : token[0] is '<' or '>' or '=' ? 1
                : 0;
            var op = length == 0 ? "=" : token[..length];
            var operand = token[length..];
            if (operand.Length == 0 || operand[0] is '<' or '>' or '=') return false;
            comparators.Add((op, operand));
        }

        if (comparators.Count == 0) return false;
        range = new VersionRange(text.Trim(), comparators.ToArray());
        return true;
    }

    /// <summary>True when every comparator parses as a library SemVer, so <see cref="Contains(SemanticVersion)"/> is meaningful.</summary>
    public bool IsLibraryRange => _comparators.All(c => ClientIdentity.TryParseLibraryVersion(c.Operand, out _));

    public bool IsEngineRange => _comparators.All(c => EngineBuild.TryParse(c.Operand, out _));

    public bool IsRevisionRange => _comparators.All(c => long.TryParse(c.Operand, NumberStyles.None, CultureInfo.InvariantCulture, out _));

    public bool Contains(SemanticVersion version)
        => Holds(c => ClientIdentity.TryParseLibraryVersion(c.Operand, out var operand) ? version.CompareTo(operand) : null);

    public bool ContainsRevision(long revision)
        => Holds(c => long.TryParse(c.Operand, NumberStyles.None, CultureInfo.InvariantCulture, out var operand) ? revision.CompareTo(operand) : null);

    public bool ContainsEngine(EngineBuild engine)
        => Holds(c => EngineBuild.TryParse(c.Operand, out var operand) ? engine.CompareTo(operand) : null);

    private bool Holds(Func<(string Operator, string Operand), int?> compare)
    {
        foreach (var comparator in _comparators)
        {
            if (compare(comparator) is not { } cmp) return false;
            var ok = comparator.Operator switch
            {
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                _ => cmp == 0,
            };
            if (!ok) return false;
        }

        return true;
    }

    public override string ToString() => Text;
}

/// <summary>An s&amp;box engine build such as <c>26.10.02</c>: dot-separated numbers, compared component by component.</summary>
public readonly record struct EngineBuild(long[] Parts) : IComparable<EngineBuild>
{
    public static bool TryParse(string? text, out EngineBuild build)
    {
        build = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var pieces = text.Trim().Split('.');
        var parts = new long[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (!long.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
        }

        build = new EngineBuild(parts);
        return true;
    }

    /// <summary>Missing trailing components count as zero, so <c>26.10</c> equals <c>26.10.0</c>.</summary>
    public int CompareTo(EngineBuild other)
    {
        var length = Math.Max(Parts.Length, other.Parts.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < Parts.Length ? Parts[i] : 0;
            var right = i < other.Parts.Length ? other.Parts[i] : 0;
            if (left != right) return left.CompareTo(right);
        }

        return 0;
    }
}
