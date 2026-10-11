using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Compat;

/// <summary>
/// One version-keyed behavior switch. Principle: the server checks capabilities, not library versions; a shim is
/// the last resort for a library build that cannot be fixed, and it must say why and when it goes away.
/// </summary>
/// <param name="Id">Stable name the code checks with <see cref="LibraryCompatShims.Applies"/>.</param>
/// <param name="Range">Library versions it applies to (<see cref="VersionRange"/> grammar), or <c>legacy</c> for the no-version bucket.</param>
/// <param name="Reason">What the library does wrong and what the server does about it.</param>
/// <param name="Issue">Tracking issue, e.g. <c>sbox-cool/sbox-network-storage-server#123</c>.</param>
/// <param name="ExpiresInServer">First server version that must not carry the shim any more.</param>
public sealed record LibraryCompatShim(string Id, string Range, string Reason, string Issue, string ExpiresInServer);

/// <summary>
/// The only place the server switches behavior on the caller's library version. Empty at first. A shim whose
/// <see cref="LibraryCompatShim.ExpiresInServer"/> has been reached stops applying and fails
/// <c>LibraryCompatShimsTests</c>, so it is deleted instead of living on.
/// </summary>
public static class LibraryCompatShims
{
    public static IReadOnlyList<LibraryCompatShim> All { get; } = [];

    /// <summary>True when the shim named <paramref name="id"/> covers this client on the running server build.</summary>
    public static bool Applies(string id, ClientIdentity identity) => Applies(All, id, identity, BuildInfo.Version);

    public static bool Applies(IReadOnlyList<LibraryCompatShim> shims, string id, ClientIdentity identity, string serverVersion)
    {
        foreach (var shim in shims)
        {
            if (shim.Id == id && Covers(shim, identity) && !IsExpired(shim, serverVersion)) return true;
        }

        return false;
    }

    /// <summary>
    /// Expired once the server's major.minor.patch reaches <see cref="LibraryCompatShim.ExpiresInServer"/>
    /// (a pre-release of that version already counts). An unreadable version counts as expired.
    /// </summary>
    public static bool IsExpired(LibraryCompatShim shim, string serverVersion)
        => !SemanticVersion.TryParse(shim.ExpiresInServer, out var expires)
            || !SemanticVersion.TryParse(serverVersion, out var current)
            || (current.Major, current.Minor, current.Patch).CompareTo((expires.Major, expires.Minor, expires.Patch)) >= 0;

    private static bool Covers(LibraryCompatShim shim, ClientIdentity identity)
    {
        if (shim.Range == ClientIdentity.Legacy) return identity.IsLegacy;
        return identity.Library is { } version && VersionRange.TryParse(shim.Range, out var range) && range.Contains(version);
    }
}
