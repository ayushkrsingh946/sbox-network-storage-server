using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>
/// Module ownership of live definitions. A definition belongs to a module when
/// its JSON carries <c>"module": "&lt;moduleId&gt;"</c>; module-installed names
/// are prefixed <c>&lt;moduleId&gt;_&lt;local&gt;</c> (IDs allow [a-zA-Z0-9_-],
/// so the separator is an underscore). Editor, Sync Tool and dashboard writes
/// must never delete, overwrite or rename an owned definition; only the module
/// lifecycle changes them.
/// </summary>
public static class ModuleOwnership
{
    /// <summary>Definition property marking module ownership.</summary>
    public const string Field = "module";

    public static string Prefix(string moduleId) => $"{moduleId}_";

    public static string Namespaced(string moduleId, string localName) => $"{moduleId}_{localName}";

    public static bool IsOwned(JsonElement definition)
        => definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty(Field, out var owner)
            && owner.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(owner.GetString());

    public static string? OwnerOf(JsonElement definition)
        => definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty(Field, out var owner)
            && owner.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(owner.GetString())
            ? owner.GetString()
            : null;

    public static bool OwnedBy(JsonElement definition, string moduleId)
        => string.Equals(OwnerOf(definition), moduleId, StringComparison.Ordinal);

    public static bool IsNamespaced(string? name, string moduleId)
        => name is not null && name.StartsWith(Prefix(moduleId), StringComparison.Ordinal);

    /// <summary>
    /// True when an incoming non-module write must leave the existing definition
    /// alone: the live row is owned and the writer is not its owning module.
    /// </summary>
    public static bool MustKeep(JsonElement? existing, string? writerModuleId = null)
    {
        if (existing is null) return false;
        var owner = OwnerOf(existing.Value);
        return owner is not null && !string.Equals(owner, writerModuleId, StringComparison.Ordinal);
    }
}
