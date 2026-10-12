using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Compat;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerVersionResource(string Kind, string Id, string Name, int Count);
public sealed record OwnerDiffLine(char Op, string Text);
/// <summary>One retained published revision for the Versions page.</summary>
public sealed record OwnerRetainedRevision(
    long RevisionId,
    DateTimeOffset PublishedAtUtc,
    // "live", "grace" or "expired".
    string State,
    DateTimeOffset? GraceEndsAtUtc,
    long Players24h,
    string? SchemaWarning);
public sealed record OwnerVersionsModel(string ProjectId, string ProjectName, IReadOnlyList<OwnerVersionResource> Resources,
    string? Kind, string? Id, IReadOnlyList<ResourceVersionEntry> Versions, ResourceVersionEntry? Selected,
    string? SelectedText, string? CurrentText, IReadOnlyList<OwnerDiffLine>? Diff, string? Error = null,
    IReadOnlyList<OwnerRetainedRevision>? Retained = null);

/// <summary>
/// Version history for endpoints and workflows. Every save (dashboard, Sync Tool PUT, PATCH, sync push, restore)
/// records a snapshot through <see cref="ManagementProjectObjects.RecordVersionAsync"/>; this page lists, compares
/// and restores them. The retained-revisions section (game-version-policy)
/// lists published game revisions with support state and lets the owner end
/// or extend grace periods.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerVersionsController(INetworkStorageProjectService projects, INetworkStorageStore store,
    ManagementMutationHandler mutations, IAuditLogger audit, IWorkspaceStore workspace,
    ClientVersionsQuery clients, TimeProvider time) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/versions";
    private const int MaxDiffLines = 2000;

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] string? kind, [FromQuery] string? id, [FromQuery] string? v,
        CancellationToken ct)
    {
        var model = await LoadAsync(projectId, kind, id, v, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Versions.cshtml", model);
    }

    [HttpPost(Route + "/restore")]
    public async Task<IActionResult> Restore(string projectId, [FromForm] string? kind, [FromForm] string? id, [FromForm] string? v, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, kind, id, v, ct);
        if (model?.Selected is null || model.Kind is null || model.Id is null) return NotFound();
        var definition = JsonNode.Parse(model.Selected.Definition.GetRawText())!.AsObject();
        definition["id"] = model.Id;
        var result = await mutations.SaveOwnerResourceAsync(OwnerProjectScope.Owner, projectId, model.Kind,
            JsonSerializer.SerializeToElement(definition), ct, versionSource: "restore");
        if (result.StatusCode >= 400)
        {
            Response.StatusCode = result.StatusCode;
            return View("~/Views/Owner/Versions.cshtml", model with { Error = JsonSerializer.Serialize(result.Body) });
        }
        await OwnerProjectScope.AuditAsync(audit, projectId, "resource.restore", new { kind = model.Kind, id = model.Id, version = model.Selected.Name }, ct);
        OwnerFlash.Success(this, "Version restored.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/versions?kind={model.Kind}&id={Uri.EscapeDataString(model.Id)}");
    }

    /// <summary>Ends a revision's grace period immediately (confirm + audit).</summary>
    [HttpPost(Route + "/grace/end")]
    public async Task<IActionResult> EndGrace(string projectId, [FromForm] long? revisionId, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (!revisionId.HasValue) return await RetainedErrorAsync(projectId, "Choose a revision to end grace for.", ct);
        if (!OwnerConfirm.IsConfirmed(Request)) return OwnerConfirm.Page(this);
        if (!await RevisionSnapshots.EndGraceAsync(workspace, OwnerProjectScope.Owner, projectId, revisionId.Value, ct))
            return await RetainedErrorAsync(projectId, $"Revision {revisionId} has no retained snapshot.", ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "revision.grace.end", new { revisionId }, ct);
        OwnerFlash.Success(this, $"Grace period for revision {revisionId} ended.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/versions");
    }

    /// <summary>Extends a revision's grace period by N minutes (confirm + audit).</summary>
    [HttpPost(Route + "/grace/extend")]
    public async Task<IActionResult> ExtendGrace(string projectId, [FromForm] long? revisionId, [FromForm] int? extendMinutes, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (!revisionId.HasValue || extendMinutes is not (>= 1 and <= 10080))
            return await RetainedErrorAsync(projectId, "Choose a revision and an extension of 1-10080 minutes.", ct);
        if (!OwnerConfirm.IsConfirmed(Request)) return OwnerConfirm.Page(this);
        var until = time.GetUtcNow().AddMinutes(extendMinutes.Value);
        if (!await RevisionSnapshots.ExtendGraceAsync(workspace, OwnerProjectScope.Owner, projectId, revisionId.Value, until, ct))
            return await RetainedErrorAsync(projectId, $"Revision {revisionId} has no retained snapshot.", ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "revision.grace.extend", new { revisionId, extendMinutes, until }, ct);
        OwnerFlash.Success(this, $"Grace period for revision {revisionId} extended by {extendMinutes} minutes.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/versions");
    }

    private async Task<IActionResult> RetainedErrorAsync(string projectId, string error, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, null, null, null, ct);
        if (model is null) return NotFound();
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("~/Views/Owner/Versions.cshtml", model with { Error = error });
    }

    private async Task<OwnerVersionsModel?> LoadAsync(string projectId, string? kind, string? id, string? versionName, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return null;
        var endpointRows = await store.ListEndpointsAsync(projectId, ct);
        var workflowRows = await store.ListWorkflowsAsync(projectId, ct);
        var resources = new List<OwnerVersionResource>();
        foreach (var (rowKind, rows) in new[] { ("endpoint", endpointRows), ("workflow", workflowRows) })
        {
            foreach (var row in rows)
            {
                if (OwnerProjectScope.Text(row, rowKind + "_id") is not { } rowId) continue;
                var count = await ManagementProjectObjects.CountVersionsAsync(store, OwnerProjectScope.Owner, projectId, rowKind, rowId, ct);
                resources.Add(new OwnerVersionResource(rowKind, rowId,
                    OwnerProjectScope.Text(row, rowKind == "endpoint" ? "slug" : "name") ?? rowId, count));
            }
        }
        var retained = await LoadRetainedAsync(projectId, project, ct);
        var empty = new OwnerVersionsModel(projectId, project.Name, resources, null, null, [], null, null, null, null, null, retained);
        if (kind is not ("endpoint" or "workflow") || id is null) return empty;
        if (!resources.Any(resource => resource.Kind == kind && resource.Id == id)) return null;

        var history = await ManagementProjectObjects.ListVersionsAsync(store, OwnerProjectScope.Owner, projectId, kind, id, ct);
        var currentRow = (kind == "endpoint" ? endpointRows : workflowRows).First(row => OwnerProjectScope.Text(row, kind + "_id") == id);
        var current = OwnerProjectScope.JsonColumn(currentRow, "definition_json");
        var currentText = current is { } live ? OwnerResourceSource.DisplayText(live) : null;
        var selected = versionName is null ? history.FirstOrDefault() : history.FirstOrDefault(entry => entry.Name == versionName);
        if (versionName is not null && selected is null) return null;
        var selectedText = selected is null ? null : OwnerResourceSource.DisplayText(selected.Definition);
        return empty with
        {
            Kind = kind, Id = id, Versions = history, Selected = selected, SelectedText = selectedText, CurrentText = currentText,
            Diff = selectedText is null || currentText is null ? null : Diff(selectedText, currentText),
        };
    }

    /// <summary>
    /// Retained published revisions: publish time, support state (live, in
    /// grace until a time, expired), distinct players per revision in the
    /// last 24 hours (client-version sketches over heartbeats/requests), and
    /// a warning when a newer revision changed a collection schema in a
    /// non-additive way. Best-effort: failures yield an empty list, never a
    /// failed page. Player records are shared across revisions, so schema
    /// changes must stay backward-compatible.
    /// </summary>
    private async Task<IReadOnlyList<OwnerRetainedRevision>> LoadRetainedAsync(
        string projectId, SboxNetworkStorage.Domain.Workspace.WorkspaceProject project, CancellationToken ct)
    {
        try
        {
            var now = time.GetUtcNow();
            var package = await workspace.GetProjectResourceAsync<Dictionary<string, JsonElement>>(
                OwnerProjectScope.Owner, projectId, "game-package.json", ct);
            long? live = package is not null && package.TryGetValue("currentRevisionId", out var cur)
                && cur.ValueKind == JsonValueKind.Number && cur.TryGetInt64(out var curId) ? curId : null;
            var index = await RevisionSnapshots.ReadIndexAsync(workspace, OwnerProjectScope.Owner, projectId, ct);
            live ??= index.LiveRevisionId;
            if (!live.HasValue) return [];

            var entries = index.Revisions.OrderByDescending(e => e.RevisionId).ToList();
            if (!entries.Any(e => e.RevisionId == live.Value))
                entries.Insert(0, new RevisionIndexEntry(live.Value, index.LivePublishedAtUtc ?? now, index.LivePublishedAtUtc ?? now));

            Dictionary<long, long> players = new();
            try
            {
                var report = await clients.ReadAsync(projectId, 1, null, ct);
                foreach (var group in report.Versions.Where(v => v.GameRevision.HasValue).GroupBy(v => v.GameRevision!.Value))
                    players[group.Key] = group.Sum(v => v.EstimatedPlayers);
            }
            catch { /* players stay unknown (0) rather than failing the page */ }

            IReadOnlyList<JsonElement> liveCollections = [];
            try { liveCollections = await store.ListCollectionsAsync(projectId, ct); }
            catch { /* schema warnings stay empty rather than failing the page */ }

            var rows = new List<OwnerRetainedRevision>();
            foreach (var entry in entries)
            {
                string state;
                DateTimeOffset? endsAt = null;
                if (entry.RevisionId == live.Value)
                {
                    state = "live";
                }
                else
                {
                    var decision = VersionPolicyEvaluator.Evaluate(project, index, entry.RevisionId, now, live.Value);
                    state = decision.GraceExpired ? "expired" : "grace";
                    endsAt = decision.GraceEndsAt;
                }
                string? warning = null;
                if (entry.RevisionId != live.Value)
                {
                    try
                    {
                        var snapshot = await RevisionSnapshots.ReadSnapshotAsync(
                            workspace, OwnerProjectScope.Owner, projectId, entry.RevisionId, ct);
                        if (snapshot is not null)
                            warning = RevisionSchemaWarnings.Compare(snapshot.Collections, liveCollections);
                    }
                    catch { /* no warning rather than a failed page */ }
                }
                rows.Add(new OwnerRetainedRevision(entry.RevisionId, entry.PublishedAtUtc, state, endsAt,
                    players.TryGetValue(entry.RevisionId, out var count) ? count : 0, warning));
            }
            return rows;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Line diff (longest common subsequence) from the selected version to the live definition.</summary>
    private static IReadOnlyList<OwnerDiffLine>? Diff(string before, string after)
    {
        var a = before.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var b = after.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (a.Length > MaxDiffLines || b.Length > MaxDiffLines) return null;
        var lengths = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lengths[i, j] = a[i] == b[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var lines = new List<OwnerDiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { lines.Add(new(' ', a[x])); x++; y++; }
            else if (lengths[x + 1, y] >= lengths[x, y + 1]) lines.Add(new('-', a[x++]));
            else lines.Add(new('+', b[y++]));
        }
        while (x < a.Length) lines.Add(new('-', a[x++]));
        while (y < b.Length) lines.Add(new('+', b[y++]));
        return lines;
    }
}

/// <summary>
/// Non-additive collection schema changes between a retained revision and
/// live: removed collections, or removed top-level fields. Pure and
/// unit-testable. Records are shared across revisions, so these changes can
/// break old builds still in grace.
/// </summary>
public static class RevisionSchemaWarnings
{
    public static string? Compare(IReadOnlyList<JsonElement> snapshotCollections, IReadOnlyList<JsonElement> liveCollections)
    {
        var live = Index(liveCollections);
        var old = Index(snapshotCollections);
        var removedCollections = old.Keys.Where(name => !live.ContainsKey(name)).OrderBy(name => name).ToList();
        var removedFields = new List<string>();
        foreach (var (name, oldFields) in old)
        {
            if (!live.TryGetValue(name, out var liveFields)) continue;
            foreach (var field in oldFields.Where(f => !liveFields.Contains(f)).OrderBy(f => f))
                removedFields.Add($"{name}.{field}");
        }
        var parts = new List<string>();
        if (removedCollections.Count > 0) parts.Add("removed collections: " + string.Join(", ", removedCollections));
        if (removedFields.Count > 0) parts.Add("removed fields: " + string.Join(", ", removedFields));
        return parts.Count == 0 ? null : "Non-additive schema change since this revision — " + string.Join("; ", parts) + ". Old builds in grace share live player data.";
    }

    private static Dictionary<string, HashSet<string>> Index(IReadOnlyList<JsonElement> rows)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var name = row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            result[name!] = Fields(DefinitionOf(row));
        }
        return result;
    }

    private static JsonElement DefinitionOf(JsonElement row)
    {
        if (row.TryGetProperty("definition_json", out var def))
        {
            if (def.ValueKind == JsonValueKind.Object) return def;
            if (def.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(def.GetString()))
            {
                try { return JsonDocument.Parse(def.GetString()!).RootElement.Clone(); }
                catch (JsonException) { }
            }
        }
        return row;
    }

    private static HashSet<string> Fields(JsonElement definition)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (definition.ValueKind != JsonValueKind.Object) return fields;
        if (definition.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var prop in props.EnumerateObject()) fields.Add(prop.Name);
            else
                foreach (var prop in schema.EnumerateObject()) fields.Add(prop.Name);
        }
        if (definition.TryGetProperty("fields", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString())) fields.Add(item.GetString()!);
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString())) fields.Add(n.GetString()!);
            }
        return fields;
    }
}
