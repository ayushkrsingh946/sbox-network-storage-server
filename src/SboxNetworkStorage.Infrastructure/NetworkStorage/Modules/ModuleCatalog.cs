using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
/// Fixed registries for the declarative module system. Modules are manifests +
/// authored definitions compiled into the binary; they never download or run
/// code. Hooks are C# subscribers, panels are Razor partials under
/// <c>Views/Owner/Modules/</c>, both from these closed lists.
/// </summary>
public static class ModuleRegistries
{
    /// <summary>Server hooks a module may subscribe to (offline dispatch, never on the request path).</summary>
    public static readonly IReadOnlyList<string> Hooks =
    [
        "endpoint.executed", "record.changed", "ledger.delta",
        "request.rejected", "revision.reported", "heartbeat",
    ];

    /// <summary>Permissions a module may request. Module background actions only run when granted.</summary>
    public static readonly IReadOnlyList<string> Permissions =
    [
        "read-player-data", "write-player-data", "read-analytics",
        "flag-players", "block-requests", "send-alerts",
    ];

    /// <summary>Dashboard panels a module may add (Razor partial names under Views/Owner/Modules/).</summary>
    public static readonly IReadOnlyList<string> Panels =
    [
        "review-queue", "player-overview", "player-profile", "action-log",
    ];

    public static bool IsHook(string value) => Hooks.Contains(value, StringComparer.Ordinal);
    public static bool IsPermission(string value) => Permissions.Contains(value, StringComparer.Ordinal);
    public static bool IsPanel(string value) => Panels.Contains(value, StringComparer.Ordinal);
}

/// <summary>One raw authored definition inside a module: its kind and source document.</summary>
public sealed record ModuleDefinitionSource(string Kind, string LocalId, JsonElement Source);

/// <summary>Validated module manifest plus its authored definitions.</summary>
public sealed record CatalogModule(
    string Id,
    string Version,
    string MinServerVersion,
    string Name,
    string Description,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Hooks,
    IReadOnlyList<string> Panels,
    IReadOnlyList<ModuleSettingSchema> Settings,
    IReadOnlyList<ModuleDefinitionSource> Definitions,
    bool Available,
    string? UnavailableReason);

public sealed record ModuleSettingSchema(string Key, string Type, string Default, string Description);

/// <summary>
/// Loads the embedded module catalog. Each module is two embedded resources:
/// <c>&lt;id&gt;.module.yml</c> (manifest) and <c>&lt;id&gt;.definitions.yml</c>
/// (authored definitions in the existing authoring format). A module that fails
/// validation is reported unavailable; every other module still loads.
/// </summary>
public static partial class ModuleCatalog
{
    public const string ResourcePrefix = "SboxNetworkStorage.Infrastructure.Modules.";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.None, 100)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.None, 100)]
    private static partial Regex VersionPattern();

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static IReadOnlyList<CatalogModule> Load(Assembly? assembly = null)
    {
        assembly ??= typeof(ModuleCatalog).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".module.yml", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        return names.Select(name => LoadOne(assembly, name)).ToList();
    }

    private static CatalogModule LoadOne(Assembly assembly, string manifestName)
    {
        var moduleId = manifestName[ResourcePrefix.Length..^".module.yml".Length];
        try
        {
            using var manifestStream = assembly.GetManifestResourceStream(manifestName);
            if (manifestStream is null) return Unavailable(moduleId, $"Manifest resource {manifestName} is missing.");
            using var definitionsStream = assembly.GetManifestResourceStream(ResourcePrefix + moduleId + ".definitions.yml");
            return ParseModule(moduleId,
                manifestYaml: ReadAll(manifestStream),
                definitionsYaml: definitionsStream is null ? null : ReadAll(definitionsStream));
        }
        catch (Exception ex)
        {
            return Unavailable(moduleId, $"Manifest failed to parse: {ex.Message}");
        }
    }

    internal static CatalogModule Unavailable(string moduleId, string reason) => new(
        moduleId, "0.0.0", "0.0.0", moduleId, string.Empty,
        [], [], [], [], [], false, reason);

    /// <summary>Validates one module from manifest + definitions text. Used by the loader and unit tests.</summary>
    internal static CatalogModule ParseModule(string moduleId, string manifestYaml, string? definitionsYaml)
    {
        try
        {
            var manifest = ToJsonElement(manifestYaml, coerceScalars: false);
            if (manifest.ValueKind != JsonValueKind.Object) return Unavailable(moduleId, "The manifest must be a YAML mapping.");
            var id = GetString(manifest, "id") ?? moduleId;
            if (!IdPattern().IsMatch(id)) return Unavailable(moduleId, $"Invalid module id '{id}'.");
            if (!string.Equals(id, moduleId, StringComparison.Ordinal)) return Unavailable(moduleId, $"Manifest id '{id}' does not match file '{moduleId}'.");
            var version = GetString(manifest, "version");
            if (version is null || !VersionPattern().IsMatch(version)) return Unavailable(moduleId, $"Invalid semantic version '{version}'.");
            var minServer = GetString(manifest, "minServerVersion") ?? "0.0.0";
            if (!VersionPattern().IsMatch(minServer)) return Unavailable(moduleId, $"Invalid minServerVersion '{minServer}'.");
            var name = GetString(manifest, "name");
            if (string.IsNullOrWhiteSpace(name)) return Unavailable(moduleId, "The manifest needs a name.");
            var description = GetString(manifest, "description") ?? string.Empty;
            var permissions = GetStrings(manifest, "permissions");
            var unknownPermission = permissions.FirstOrDefault(permission => !ModuleRegistries.IsPermission(permission));
            if (unknownPermission is not null) return Unavailable(moduleId, $"Unknown permission '{unknownPermission}'.");
            var hooks = GetStrings(manifest, "hooks");
            var unknownHook = hooks.FirstOrDefault(hook => !ModuleRegistries.IsHook(hook));
            if (unknownHook is not null) return Unavailable(moduleId, $"Unknown hook '{unknownHook}'.");
            var panels = GetStrings(manifest, "panels");
            var unknownPanel = panels.FirstOrDefault(panel => !ModuleRegistries.IsPanel(panel));
            if (unknownPanel is not null) return Unavailable(moduleId, $"Unknown panel '{unknownPanel}'.");
            var settings = ReadSettings(manifest);

            if (definitionsYaml is null) return Unavailable(moduleId, "Definitions resource is missing.");
            var definitionsDoc = ToJsonElement(definitionsYaml);
            if (definitionsDoc.ValueKind != JsonValueKind.Array) return Unavailable(moduleId, "Definitions must be a YAML list.");
            var definitions = new List<ModuleDefinitionSource>();
            foreach (var entry in definitionsDoc.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) return Unavailable(moduleId, "Each definition needs kind and source.");
                var kind = GetString(entry, "kind");
                if (kind is not ("collection" or "endpoint" or "workflow" or "query" or "game-values"))
                    return Unavailable(moduleId, $"Unknown definition kind '{kind}'.");
                if (!entry.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
                    return Unavailable(moduleId, $"Definition '{kind}' needs an object source.");
                var localId = GetString(source, "id")
                    ?? (kind == "endpoint" ? GetString(source, "slug") : GetString(source, "name"));
                if (kind != "game-values" && string.IsNullOrWhiteSpace(localId))
                    return Unavailable(moduleId, $"Definition '{kind}' needs an id (or slug or name).");
                definitions.Add(new ModuleDefinitionSource(kind, localId ?? kind, source.Clone()));
            }

            return new CatalogModule(id, version, minServer, name, description,
                permissions, hooks, panels, settings, definitions, true, null);
        }
        catch (Exception ex)
        {
            return Unavailable(moduleId, $"Manifest failed to parse: {ex.Message}");
        }
    }

    private static IReadOnlyList<ModuleSettingSchema> ReadSettings(JsonElement manifest)
    {
        var settings = new List<ModuleSettingSchema>();
        if (!manifest.TryGetProperty("settings", out var list) || list.ValueKind != JsonValueKind.Array)
            return settings;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var key = GetString(item, "key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            settings.Add(new ModuleSettingSchema(key,
                GetString(item, "type") ?? "string",
                GetString(item, "default") ?? string.Empty,
                GetString(item, "description") ?? string.Empty));
        }
        return settings;
    }

    private static string ReadAll(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JsonElement ToJsonElement(string yaml, bool coerceScalars = true)
    {
        var parsed = Yaml.Deserialize<object?>(yaml);
        var normalized = NormalizeYamlValue(parsed, coerceScalars);
        return JsonSerializer.SerializeToElement(normalized);
    }

    internal static object? NormalizeYamlValue(object? value, bool coerceScalars = true) => value switch
    {
        null => null,
        IDictionary<object, object?> map => map.ToDictionary(
            entry => entry.Key?.ToString() ?? string.Empty, entry => NormalizeYamlValue(entry.Value, coerceScalars),
            StringComparer.Ordinal),
        System.Collections.IEnumerable list when value is not string => list.Cast<object?>().Select(item => NormalizeYamlValue(item, coerceScalars)).ToList(),
        // YamlDotNet hands scalars to object targets as strings; restore the
        // editor source compiler's scalar semantics for definitions (same as
        // OwnerYamlDefinitions). Manifests keep raw strings so version numbers
        // and setting defaults are never retyped.
        string s when coerceScalars && bool.TryParse(s, out var b) => b,
        string s when coerceScalars && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        string s when coerceScalars && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
        _ => value,
    };

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }

    /// <summary>Compares semantic versions (a &lt; 0, a == 0, a &gt; 0). Inputs are validated manifests.</summary>
    public static int CompareVersions(string a, string b)
    {
        var left = a.Split('.').Select(int.Parse).ToArray();
        var right = b.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < 3; i++)
        {
            var compared = left[i].CompareTo(right[i]);
            if (compared != 0) return compared;
        }
        return 0;
    }
}
