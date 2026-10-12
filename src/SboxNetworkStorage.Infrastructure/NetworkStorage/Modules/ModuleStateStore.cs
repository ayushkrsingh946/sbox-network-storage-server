using System.Text.Json;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>One installed module's persisted state.</summary>
public sealed record InstalledModule(
    string Id,
    string Version,
    bool Enabled,
    Dictionary<string, string> Settings,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Collections,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> Workflows,
    IReadOnlyList<string> Queries,
    IReadOnlyList<string> ValuesItems,
    string InstalledAt);

/// <summary>A catalog upgrade held until the owner approves its new permissions.</summary>
public sealed record PendingModuleUpgrade(string Id, string Version, IReadOnlyList<string> Permissions);

/// <summary>Persisted <c>modules.json</c> project resource. Absent means nothing is installed.</summary>
public sealed record ModuleProjectState(
    IReadOnlyList<InstalledModule> Modules,
    IReadOnlyList<PendingModuleUpgrade> PendingUpgrades)
{
    public static ModuleProjectState Empty { get; } = new([], []);
}

/// <summary>
/// Reads and writes the <c>modules.json</c> project resource. All methods are
/// tolerant: a missing or corrupt resource reads as empty, and saves overwrite.
/// </summary>
public sealed class ModuleStateStore(IWorkspaceStore workspace)
{
    public const string ResourcePath = "modules.json";

    /// <summary>
    /// Reads <c>modules.json</c>. Absent (or a non-object document) reads as
    /// empty — nothing is installed. A read failure THROWS so callers fail
    /// closed: ownership checks must refuse and lifecycle writes must abort
    /// rather than act on unknown state.
    /// </summary>
    public async Task<ModuleProjectState> LoadAsync(long userId, string projectId, CancellationToken ct)
    {
        var doc = await workspace.GetProjectResourceAsync<JsonElement>(userId, projectId, ResourcePath, ct);
        if (doc.ValueKind != JsonValueKind.Object) return ModuleProjectState.Empty;
        return Parse(doc);
    }

    public Task SaveAsync(long userId, string projectId, ModuleProjectState state, CancellationToken ct)
        => workspace.PutProjectResourceAsync(userId, projectId, ResourcePath, state, ct);

    /// <summary>Module id → disabled, for the endpoint executor's access gate.</summary>
    public static Func<string, bool> DisabledLookup(ModuleProjectState state)
    {
        var disabled = new HashSet<string>(state.Modules.Where(module => !module.Enabled).Select(module => module.Id),
            StringComparer.Ordinal);
        return disabled.Contains;
    }

    /// <summary>Live definition names owned by modules, per kind, for sync-merge preservation.</summary>
    public static Dictionary<string, HashSet<string>> OwnedNames(ModuleProjectState state)
    {
        var owned = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var module in state.Modules)
        {
            Add(owned, "collection", module.Collections);
            Add(owned, "endpoint", module.Endpoints);
            Add(owned, "workflow", module.Workflows);
            Add(owned, "query", module.Queries);
        }
        return owned;
    }

    private static void Add(Dictionary<string, HashSet<string>> owned, string kind, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;
        if (!owned.TryGetValue(kind, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            owned[kind] = set;
        }
        foreach (var name in names) set.Add(name);
    }
    private static ModuleProjectState Parse(JsonElement doc)
    {
        var modules = new List<InstalledModule>();
        if (TryProperty(doc, "modules", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Text(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                modules.Add(new InstalledModule(
                    id,
                    Text(item, "version") ?? "0.0.0",
                    !TryProperty(item, "enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
                    Strings(item, "settings"),
                    Names(item, "permissions"),
                    Names(item, "collections"),
                    Names(item, "endpoints"),
                    Names(item, "workflows"),
                    Names(item, "queries"),
                    Names(item, "valuesItems"),
                    Text(item, "installedAt") ?? string.Empty));
            }
        }
        var pending = new List<PendingModuleUpgrade>();
        if (TryProperty(doc, "pendingUpgrades", out var upgrades) && upgrades.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in upgrades.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Text(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                pending.Add(new PendingModuleUpgrade(id, Text(item, "version") ?? "0.0.0", Names(item, "permissions")));
            }
        }
        return new ModuleProjectState(modules, pending);
    }

    /// <summary>Case-insensitive property lookup: state may be written by serialized records (PascalCase) or by hand (camelCase).</summary>
    private static bool TryProperty(JsonElement element, string camelName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(camelName, out value))
            return true;
        var pascal = char.ToUpperInvariant(camelName[0]) + camelName[1..];
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(pascal, out value))
            return true;
        value = default;
        return false;
    }

    private static string? Text(JsonElement element, string name)
        => TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> Names(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static Dictionary<string, string> Strings(JsonElement element, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryProperty(element, name, out var map) || map.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                result[property.Name] = value;
        }
        return result;
    }
}
