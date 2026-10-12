using System.Text.Json;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;

/// <summary>
/// Emission contract for data-plane hook events. Emission call sites fill
/// <c>Data</c> with these well-known keys; subscribers read only what they
/// need. Building the payload never throws: failures yield an empty payload
/// rather than breaking the request.
/// </summary>
public static class ModuleHookEventData
{
    public const string Outcome = "outcome";
    public const string ErrorCode = "errorCode";
    public const string ChangedFields = "changedFields";
    public const string Body = "body";
    public const string Field = "field";
    public const string Delta = "delta";
    public const string Reason = "reason";
    public const string RevisionKnown = "known";

    /// <summary>Changed-field paths from an endpoint definition's write/delete steps.</summary>
    public static IReadOnlyList<string> ChangedFieldsOf(JsonElement? definition)
    {
        var fields = new List<string>();
        try
        {
            if (!definition.HasValue || definition.Value.ValueKind != JsonValueKind.Object) return fields;
            if (!definition.Value.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                return fields;
            foreach (var step in steps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object) continue;
                if (!step.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                if (type.GetString() is not ("write" or "delete")) continue;
                if (!step.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) continue;
                foreach (var op in ops.EnumerateArray())
                {
                    if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("path", out var path)
                        && path.ValueKind == JsonValueKind.String && path.GetString() is { } field
                        && !fields.Contains(field, StringComparer.Ordinal))
                        fields.Add(field);
                }
            }
        }
        catch
        {
            // Emission must never break the request.
        }
        return fields;
    }

    public static Dictionary<string, object?> ForEndpointExecution(JsonElement? definition, bool ok, string? errorCode)
        => new(StringComparer.Ordinal)
        {
            [Outcome] = ok ? "ok" : errorCode ?? "error",
            [ErrorCode] = errorCode,
            [ChangedFields] = ChangedFieldsOf(definition),
        };
}
