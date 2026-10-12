using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Workspace;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Modules;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerModuleCard(
    CatalogModule Catalog,
    InstalledModule? Installed,
    PendingModuleUpgrade? PendingUpgrade);

public sealed record OwnerModulesModel(
    string ProjectId,
    string ProjectName,
    IReadOnlyList<OwnerModuleCard> Modules,
    string? Error = null,
    string? OpenDialog = null);

public sealed record OwnerModulePreviewModel(
    string ProjectId,
    string ProjectName,
    ModulePreview Preview,
    string? Error = null);

public sealed record OwnerReviewQueueModel(
    string ProjectId,
    string ProjectName,
    IReadOnlyList<JsonElement> Flags,
    long OpenCount,
    string? Detector,
    string? Severity,
    string? Error = null);

public sealed record OwnerInsightsModel(
    string ProjectId,
    string ProjectName,
    PlayerOverview? Overview,
    IReadOnlyList<JsonElement> Profiles,
    string Query,
    string? Error = null,
    string? OpenDialog = null);

public sealed record OwnerPlayerModel(
    string ProjectId,
    string ProjectName,
    string SteamId,
    InsightsService.PlayerProfileResult Profile);

public sealed record OwnerActionLogModel(
    string ProjectId,
    string ProjectName,
    IReadOnlyList<JsonElement> Entries,
    long Dropped,
    string? SteamId,
    string? Endpoint,
    string? Outcome,
    int RetentionDays);

/// <summary>
/// Module catalog, lifecycle, integrity review queue and player insights.
/// Every state-changing action is a form POST with PRG + toast; destructive
/// actions use confirm dialogs with a typed-name check for uninstall.
/// </summary>
[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
public sealed class OwnerModulesController(
    INetworkStorageProjectService projects,
    IWorkspaceStore workspace,
    INetworkStorageStore store,
    IModuleDataStore modules,
    ModuleStateStore states,
    ModuleInstaller installer,
    PlayerBlockService blocks,
    InsightsService insights,
    InsightsSettingsProvider insightsSettings,
    IntegrityModuleService integrity,
    PlayerActionLogWriter actionLog,
    IAuditLogger audit) : Controller
{
    private const long Owner = NetworkStorageServices.LocalOwnerUserId;
    private const string Route = "/dashboard/projects/{projectId}/modules";

    [HttpGet(Route)]
    public async Task<IActionResult> Index(string projectId, [FromQuery] string? dialog, CancellationToken ct)
    {
        var model = await LoadAsync(projectId, ct);
        return model is null ? NotFound() : View("~/Views/Owner/Modules.cshtml", model with { OpenDialog = dialog });
    }

    [HttpGet(Route + "/{moduleId}")]
    public async Task<IActionResult> Preview(string projectId, string moduleId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        var preview = await installer.PreviewAsync(Owner, projectId, moduleId, ct);
        if (preview is null) return NotFound();
        return View("~/Views/Owner/ModulePreview.cshtml",
            new OwnerModulePreviewModel(projectId, project.Name, preview));
    }

    [HttpPost(Route + "/{moduleId}/install")]
    public async Task<IActionResult> Install(string projectId, string moduleId, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        var settings = Request.Form
            .Where(field => field.Key.StartsWith("setting.", StringComparison.Ordinal))
            .ToDictionary(field => field.Key["setting.".Length..], field => field.Value.ToString(),
                StringComparer.Ordinal);
        var result = await installer.InstallAsync(Owner, projectId, moduleId, settings, ct);
        if (!result.Ok)
        {
            OwnerFlash.Info(this, result.Error ?? "Install failed.");
            return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules/{Uri.EscapeDataString(moduleId)}");
        }
        integrity.InvalidateSettings(projectId);
        insightsSettings.Invalidate(projectId);
        OwnerFlash.Success(this, $"Module {moduleId} installed.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules");
    }

    [HttpPost(Route + "/{moduleId}/enable")]
    public async Task<IActionResult> Enable(string projectId, string moduleId, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (!await installer.SetEnabledAsync(Owner, projectId, moduleId, enabled: true, ct)) return NotFound();
        integrity.InvalidateSettings(projectId);
        insightsSettings.Invalidate(projectId);
        OwnerFlash.Success(this, $"Module {moduleId} enabled.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules");
    }

    [HttpPost(Route + "/{moduleId}/disable")]
    public async Task<IActionResult> Disable(string projectId, string moduleId, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (!await installer.SetEnabledAsync(Owner, projectId, moduleId, enabled: false, ct)) return NotFound();
        integrity.InvalidateSettings(projectId);
        insightsSettings.Invalidate(projectId);
        OwnerFlash.Success(this, $"Module {moduleId} disabled. Its endpoints answer ENDPOINT_DISABLED; data is kept.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules");
    }

    [HttpPost(Route + "/{moduleId}/uninstall")]
    public async Task<IActionResult> Uninstall(string projectId, string moduleId,
        [FromForm] string? typedName, [FromForm] bool keepData, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        var result = await installer.UninstallAsync(Owner, projectId, moduleId, typedName ?? string.Empty, keepData, ct);
        if (!result.Ok)
        {
            OwnerFlash.Info(this, result.Error ?? "Uninstall failed.");
            return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules?dialog=uninstall-{Uri.EscapeDataString(moduleId)}");
        }
        integrity.InvalidateSettings(projectId);
        insightsSettings.Invalidate(projectId);
        OwnerFlash.Success(this, keepData
            ? $"Module {moduleId} uninstalled; its collections stay as ordinary project collections."
            : $"Module {moduleId} uninstalled and its data deleted.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules");
    }

    [HttpPost(Route + "/{moduleId}/approve-upgrade")]
    public async Task<IActionResult> ApproveUpgrade(string projectId, string moduleId, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        var result = await installer.ApproveUpgradeAsync(Owner, projectId, moduleId, ct);
        if (!result.Ok)
        {
            OwnerFlash.Info(this, result.Error ?? "Upgrade failed.");
            return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules");
        }
        integrity.InvalidateSettings(projectId);
        insightsSettings.Invalidate(projectId);
        OwnerFlash.Success(this, $"Module {moduleId} upgraded to {result.Installed?.Version}.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules");
    }

    [HttpPost(Route + "/{moduleId}/settings")]
    public async Task<IActionResult> SaveSettings(string projectId, string moduleId, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        var state = await states.LoadAsync(Owner, projectId, ct);
        var installed = state.Modules.FirstOrDefault(module => module.Id == moduleId);
        if (installed is null) return NotFound();
        var catalog = ModuleCatalog.Load().FirstOrDefault(module => module.Id == moduleId);
        var settings = new Dictionary<string, string>(installed.Settings, StringComparer.Ordinal);
        foreach (var schema in catalog?.Settings ?? [])
        {
            var raw = Request.Form["setting." + schema.Key].ToString();
            if (!string.IsNullOrWhiteSpace(raw)) settings[schema.Key] = raw.Trim();
        }
        var updated = state.Modules.Select(module => module.Id == moduleId ? module with { Settings = settings } : module).ToList();
        await states.SaveAsync(Owner, projectId, new ModuleProjectState(updated, state.PendingUpgrades), ct);
        integrity.InvalidateSettings(projectId);
        insightsSettings.Invalidate(projectId);
        await OwnerProjectScope.AuditAsync(audit, projectId, "module.settings", new { module = moduleId }, ct);
        OwnerFlash.Success(this, $"Settings saved for {moduleId}.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules");
    }

    [HttpGet(Route + "/integrity/review")]
    public async Task<IActionResult> Review(string projectId, [FromQuery] string? detector,
        [FromQuery] string? severity, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        var flags = await modules.ListFlagsAsync(projectId, "open",
            string.IsNullOrWhiteSpace(detector) ? null : detector,
            string.IsNullOrWhiteSpace(severity) ? null : severity,
            steamId: null, 200, ct);
        var open = await modules.CountOpenFlagsAsync(projectId, ct);
        return View("~/Views/Owner/ModuleReview.cshtml",
            new OwnerReviewQueueModel(projectId, project.Name, flags, open, detector, severity));
    }

    [HttpPost(Route + "/integrity/flags/{flagId}/dismiss")]
    public async Task<IActionResult> DismissFlag(string projectId, string flagId,
        [FromForm] string? note, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (!await modules.UpdateFlagStateAsync(projectId, flagId, "dismissed", string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ct))
            return NotFound();
        await OwnerProjectScope.AuditAsync(audit, projectId, "integrity.flag.dismiss", new { flag = flagId, note }, ct);
        OwnerFlash.Success(this, "Flag dismissed.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules/integrity/review");
    }

    [HttpPost(Route + "/integrity/flags/{flagId}/confirm")]
    public async Task<IActionResult> ConfirmFlag(string projectId, string flagId,
        [FromForm] string? note, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (!await modules.UpdateFlagStateAsync(projectId, flagId, "confirmed", string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ct))
            return NotFound();
        await OwnerProjectScope.AuditAsync(audit, projectId, "integrity.flag.confirm", new { flag = flagId, note }, ct);
        OwnerFlash.Success(this, "Flag confirmed.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules/integrity/review");
    }

    [HttpPost(Route + "/integrity/block")]
    public async Task<IActionResult> BlockPlayer(string projectId, [FromForm] string? steamId,
        [FromForm] string? scope, [FromForm] string? reason, [FromForm] int? hours, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(steamId) || !PlayerBlockService.IsValidScope(scope))
        {
            OwnerFlash.Info(this, "Choose a player and a block scope (writes or all).");
            return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules/integrity/review");
        }
        var expiresAt = hours is > 0
            ? DateTimeOffset.UtcNow.AddHours(hours.Value).ToUnixTimeMilliseconds()
            : (long?)null;
        await blocks.BlockAsync(projectId, steamId.Trim(), scope!, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            expiresAt, "owner-dashboard", ct);
        await OwnerProjectScope.AuditAsync(audit, projectId, "integrity.player.block",
            new { player = steamId, scope, reason, hours }, ct);
        OwnerFlash.Success(this, $"Player {steamId} blocked from {(scope == "all" ? "all requests" : "writes")}.");
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules/integrity/review");
    }

    [HttpPost(Route + "/integrity/unblock")]
    public async Task<IActionResult> UnblockPlayer(string projectId, [FromForm] string? steamId, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(steamId))
        {
            await blocks.UnblockAsync(projectId, steamId.Trim(), ct);
            await OwnerProjectScope.AuditAsync(audit, projectId, "integrity.player.unblock", new { player = steamId }, ct);
            OwnerFlash.Success(this, $"Player {steamId} unblocked.");
        }
        return Redirect($"{OwnerProjectScope.ProjectUrl(projectId)}/modules/integrity/review");
    }

    [HttpGet(Route + "/insights")]
    public async Task<IActionResult> Insights(string projectId, [FromQuery] string? q, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        var overview = await insights.OverviewAsync(projectId, ct);
        var profiles = string.IsNullOrWhiteSpace(q)
            ? []
            : await insights.SearchAsync(projectId, q, ct);
        return View("~/Views/Owner/ModuleInsights.cshtml",
            new OwnerInsightsModel(projectId, project.Name, overview, profiles, q ?? string.Empty));
    }

    [HttpGet(Route + "/insights/players/{steamId}")]
    public async Task<IActionResult> Player(string projectId, string steamId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        var profile = await insights.ProfileAsync(projectId, steamId, ct);
        if (profile is null) return NotFound();
        return View("~/Views/Owner/ModulePlayer.cshtml",
            new OwnerPlayerModel(projectId, project.Name, steamId, profile));
    }

    [HttpGet(Route + "/insights/log")]
    public async Task<IActionResult> ActionLog(string projectId, [FromQuery] string? steamId,
        [FromQuery] string? endpoint, [FromQuery] string? outcome, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return NotFound();
        var settings = await insightsSettings.ForProjectAsync(projectId, ct);
        var entries = await insights.ActionLogAsync(projectId,
            string.IsNullOrWhiteSpace(steamId) ? null : steamId.Trim(),
            string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim(),
            fromUnixMs: null, toUnixMs: null, 200, ct);
        return View("~/Views/Owner/ModuleActionLog.cshtml", new OwnerActionLogModel(projectId, project.Name,
            entries, actionLog.DroppedFor(projectId), steamId, endpoint, outcome,
            settings?.ActionLogRetentionDays ?? InsightsSettings.DefaultRetentionDays));
    }

    [HttpGet(Route + "/insights/log.csv")]
    public async Task<IActionResult> ActionLogCsv(string projectId, [FromQuery] string? steamId,
        [FromQuery] string? endpoint, [FromQuery] string? outcome, CancellationToken ct)
    {
        if (await OwnerProjectScope.ResolveAsync(projects, projectId, ct) is null) return NotFound();
        var entries = await insights.ActionLogAsync(projectId,
            string.IsNullOrWhiteSpace(steamId) ? null : steamId.Trim(),
            string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim(),
            string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim(),
            fromUnixMs: null, toUnixMs: null, 1000, ct);
        var csv = InsightsService.ToCsv(entries.Select(InsightsService.ToRow));
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv",
            $"sbox-ns-{projectId}-action-log.csv");
    }

    private async Task<OwnerModulesModel?> LoadAsync(string projectId, CancellationToken ct)
    {
        var project = await OwnerProjectScope.ResolveAsync(projects, projectId, ct);
        if (project is null) return null;
        var catalog = ModuleCatalog.Load();
        var state = await states.LoadAsync(Owner, projectId, ct);
        var cards = catalog.Select(module => new OwnerModuleCard(
            module,
            state.Modules.FirstOrDefault(installed => installed.Id == module.Id),
            state.PendingUpgrades.FirstOrDefault(pending => pending.Id == module.Id))).ToList();
        return new OwnerModulesModel(projectId, project.Name, cards);
    }
}
