using System.Globalization;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// C# for calling a stored resource from s&amp;box game code, shared by the dashboard resource editor and the
/// coding-agent backend (<c>sbox-ns dev</c>), so both show the same call.
/// </summary>
internal static class OwnerGameSnippets
{
    /// <summary>
    /// <c>NetworkStorage.CallEndpoint</c> with an input object built from the endpoint's <c>input</c> schema,
    /// followed by the <c>TryGetLastEndpointError</c> check.
    /// </summary>
    public static string EndpointCall(string slug, JsonElement? definition)
    {
        var input = definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("input", out var schema)
            ? OwnerProjectScope.SchemaSkeleton(schema)
            : [];
        var args = input.Count == 0 ? "" : ", " + ObjectLiteral(input);
        var literal = JsonSerializer.Serialize(slug);
        return $$"""
            var result = await NetworkStorage.CallEndpoint( {{literal}}{{args}} );
            if ( !result.HasValue )
            {
                NetworkStorage.TryGetLastEndpointError( {{literal}}, out var code, out var message );
                Log.Warning( $"{{slug}} failed: {code} {message}" );
                return;
            }
            """;
    }

    public static bool RequiresSecretKey(JsonElement? definition)
        => definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("requiresSecretKey", out var secret)
            && secret.ValueKind == JsonValueKind.True;

    /// <summary>True when the collection declares <c>accessMode: public</c>, the only mode game clients may read and write directly.</summary>
    public static bool IsPublicCollection(JsonElement? definition)
        => definition is { ValueKind: JsonValueKind.Object } d && OwnerProjectScope.Text(d, "accessMode") == "public";

    /// <summary>True when the stored query row requires a secret key (the <c>requires_secret_key</c> column).</summary>
    public static bool RowRequiresSecretKey(JsonElement row)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty("requires_secret_key", out var secret)
            && secret.ValueKind == JsonValueKind.True;

    /// <summary>
    /// True when an endpoint only runs from other endpoints or workflows, never over HTTP:
    /// <c>exposure: internal</c> or a legacy <c>internal</c> or <c>internalOnly</c> flag.
    /// </summary>
    public static bool IsInternalEndpoint(JsonElement? definition)
        => definition is { ValueKind: JsonValueKind.Object } d
            && (string.Equals(OwnerProjectScope.Text(d, "exposure"), "internal", StringComparison.OrdinalIgnoreCase)
                || (d.TryGetProperty("internal", out var legacy) && legacy.ValueKind == JsonValueKind.True)
                || (d.TryGetProperty("internalOnly", out var internalOnly) && internalOnly.ValueKind == JsonValueKind.True));

    /// <summary>True when a public collection also allows deletes from game clients.</summary>
    public static bool AllowsClientDelete(JsonElement? definition)
        => definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("allowRecordDelete", out var allowed)
            && allowed.ValueKind == JsonValueKind.True;


    /// <summary>
    /// <c>GetDocument</c> and <c>SaveDocument</c> for a public collection: the player's own document for per-player
    /// collections, a named record for global ones. The saved object comes from the collection schema.
    /// </summary>
    public static string CollectionDocuments(string collectionId, bool global, JsonElement? definition)
    {
        var fields = definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("schema", out var schema)
            ? OwnerProjectScope.SchemaSkeleton(schema)
            : [];
        var literal = JsonSerializer.Serialize(collectionId);
        var document = global ? "var doc = \"main\"; // the global record ID" : "var doc = Game.SteamId.ToString();";
        return $$"""
            {{document}}
            var data = await NetworkStorage.GetDocument( {{literal}}, doc );
            await NetworkStorage.SaveDocument( {{literal}}, doc, {{ObjectLiteral(fields)}} );
            """;
    }

    /// <summary>
    /// <c>NetworkStorage.RunQuery</c> for a stored query. Results are shared between callers for the
    /// query's cache TTL, so game code only checks whether a result arrived.
    /// </summary>
    public static string QueryRun(string queryId)
    {
        var literal = JsonSerializer.Serialize(queryId);
        return $$"""
            var result = await NetworkStorage.RunQuery( {{literal}} );
            if ( !result.HasValue )
            {
                return;
            }
            """;
    }

    /// <summary>
    /// <c>NetworkStorage.GetGameValues</c>: the whole game-values payload in one call. Values are
    /// read-only from game code; edits happen in the dashboard or through editor sync.
    /// </summary>
    public static string GameValuesRead()
        => """
            var values = await NetworkStorage.GetGameValues();
            if ( !values.HasValue )
            {
                return;
            }
            """;

    /// <summary>
    /// An endpoint step that runs a workflow, with params filled from the workflow's <c>params</c>
    /// (or <c>input</c> schema): each declared default, else a value of the declared type.
    /// Shape matches the tested award-xp endpoint, which calls the level_from_xp workflow this way.
    /// </summary>
    public static string WorkflowStep(string workflowId, JsonElement? definition)
    {
        var parameters = ParamSkeleton(definition);
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("- id: run_" + workflowId);
        lines.AppendLine("  type: workflow");
        lines.AppendLine("  workflow: " + workflowId);
        if (parameters.Count == 0)
            lines.Append("  params: {}");
        else
        {
            lines.AppendLine("  params:");
            foreach (var pair in parameters)
                lines.Append("    " + pair.Key + ": " + YamlScalar(pair.Value));
        }
        return lines.ToString().TrimEnd();
    }

    /// <summary>
    /// A condition route that calls an internal endpoint from another endpoint. Internal endpoints never
    /// run over HTTP, so game code cannot call them; shape matches the tested open-chest endpoint.
    /// </summary>
    public static string EndpointRunRoute(string slug)
        => "action: run\nflow: " + slug;

    /// <summary>
    /// A sample object for a workflow's <c>params</c> map (or <c>input</c> schema): each declared
    /// default, else a value of the declared type.
    /// </summary>
    public static Dictionary<string, object?> ParamSkeleton(JsonElement? definition)
    {
        var sample = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (definition is not { ValueKind: JsonValueKind.Object } workflow)
            return sample;
        if (workflow.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var parameter in parameters.EnumerateObject())
            {
                if (parameter.Value.ValueKind == JsonValueKind.Object && parameter.Value.TryGetProperty("default", out var fallback))
                    sample[parameter.Name] = fallback.Clone();
                else
                    sample[parameter.Name] = parameter.Value.ValueKind == JsonValueKind.Object
                        ? OwnerProjectScope.Text(parameter.Value, "type") switch
                        {
                            "number" or "integer" => 0,
                            "boolean" => false,
                            "array" => Array.Empty<object>(),
                            "object" => new Dictionary<string, object?>(),
                            _ => "",
                        }
                        : "";
            }
            return sample;
        }
        if (workflow.TryGetProperty("input", out var schema))
            return OwnerProjectScope.SchemaSkeleton(schema);
        return sample;
    }

    private static string YamlScalar(object? value) => value switch
    {
        null => "null\n",
        string s => JsonSerializer.Serialize(s) + "\n",
        bool b => (b ? "true" : "false") + "\n",
        JsonElement { ValueKind: JsonValueKind.String } e => JsonSerializer.Serialize(e.GetString()) + "\n",
        JsonElement { ValueKind: JsonValueKind.True } => "true\n",
        JsonElement { ValueKind: JsonValueKind.False } => "false\n",
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText() + "\n",
        JsonElement { ValueKind: JsonValueKind.Array } or Array => "[]\n",
        JsonElement { ValueKind: JsonValueKind.Object } or IDictionary<string, object?> => "{}\n",
        JsonElement => "null\n",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) + "\n",
        _ => "null\n",
    };


    private static string ObjectLiteral(IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0) return "new { }";
        if (values.Keys.All(IsIdentifier))
            return "new { " + string.Join(", ", values.Select(pair => $"{pair.Key} = {Literal(pair.Value)}")) + " }";
        // Property names that are not C# identifiers need a dictionary.
        return "new Dictionary<string, object> { " + string.Join(", ", values.Select(pair => $"[{JsonSerializer.Serialize(pair.Key)}] = {Literal(pair.Value)}")) + " }";
    }

    private static string Literal(object? value) => value switch
    {
        null => "(object)null",
        string s => JsonSerializer.Serialize(s),
        bool b => b ? "true" : "false",
        JsonElement { ValueKind: JsonValueKind.String } e => JsonSerializer.Serialize(e.GetString()),
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        JsonElement { ValueKind: JsonValueKind.Array } or Array => "new object[0]",
        JsonElement { ValueKind: JsonValueKind.Object } or IDictionary<string, object?> => "new { }",
        JsonElement => "(object)null",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => "(object)null",
    };

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return !Keywords.Contains(name);
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };
}
