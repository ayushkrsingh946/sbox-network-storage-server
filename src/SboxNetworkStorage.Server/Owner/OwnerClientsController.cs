using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Server.Compatibility;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerClientsModel(WorkspaceProject Project, ClientVersionReport Report, string? CandidateMinimum, string? Error = null);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerClientsController(INetworkStorageProjectService projects, ClientVersionsQuery clients, LibraryPolicyCache cache, IAuditLogger audit) : Controller
{
    private const string Route = "/dashboard/projects/{projectId}/clients";
    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] int days = 30, [FromQuery] string? minimum = null, CancellationToken ct = default)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        if (days is < 1 or > 90 || !string.IsNullOrWhiteSpace(minimum) && !ClientIdentity.TryVersion(minimum, out _)) return BadRequest("Use days 1–90 and a SemVer minimum.");
        minimum = string.IsNullOrWhiteSpace(minimum) ? project.LibraryMinimum : minimum;
        return View("~/Views/Owner/Clients.cshtml", new OwnerClientsModel(project, await clients.ReadAsync(projectId, days, minimum, ct), minimum));
    }
    [HttpPost(Route)]
    public async Task<IActionResult> Save(string projectId, [FromForm] string? minimum, [FromForm] int? alertPercent, [FromForm] bool acknowledge, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        minimum = string.IsNullOrWhiteSpace(minimum) ? null : minimum.Trim();
        if (minimum is not null && !ClientIdentity.TryVersion(minimum, out _) || alertPercent is < 1 or > 100) return BadRequest("Use a SemVer minimum and alert percentage 1–100 (blank disables).");
        if (minimum is not null && minimum != project.LibraryMinimum && !acknowledge) return BadRequest("Preview the impact and acknowledge that this blocks older and unidentified published games.");
        await projects.UpdateProjectSettingsAsync(NetworkStorageServices.LocalOwnerUserId, projectId, "library", new()
        {
            ["libraryMinimum"] = minimum ?? "", ["libraryAlertPercent"] = alertPercent?.ToString(CultureInfo.InvariantCulture) ?? ""
        }, ct);
        cache.Invalidate(projectId);
        await audit.LogActionAsync(new AuditLogRequest(projectId, "1", "project.library-policy", new { type = "owner-dashboard" },
            new { id = projectId }, new { minimum, alertPercent }, new { project.LibraryMinimum, project.LibraryAlertPercent }, new { minimum, alertPercent }), ct);
        return Redirect($"/dashboard/projects/{Uri.EscapeDataString(projectId)}/clients");
    }
}
