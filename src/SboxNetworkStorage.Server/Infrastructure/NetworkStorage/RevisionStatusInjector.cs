using System.Text.Json;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Server.Infrastructure.NetworkStorage;

/// <summary>
/// Builds the <c>_revisionStatus</c> object the unmodified client library
/// reads on every endpoint response
/// (<c>NetworkStoragePackageInfo.UpdateFromServerResponse</c>). Field names
/// are the library's exact keys: <c>isOutdatedRevision</c>,
/// <c>graceRemainingMinutes</c>, <c>graceExpired</c>, <c>action</c>,
/// <c>message</c>, <c>currentRevision</c>, <c>enforcementMode</c> and
/// <c>policy{gracePeriodMinutes, postGraceAction, forceEndpointUpgrade,
/// notifyMessage, showUpdateOptions, showPopupOnce, showDefaultMessage,
/// enforcementMode}</c>. Returns null when no policy is configured or the
/// client revision is unknown — then callers leave the body untouched so
/// legacy responses stay byte-identical.
/// </summary>
public static class RevisionStatusInjector
{
    public static Dictionary<string, object?>? Build(
        WorkspaceProject project,
        SboxNetworkStorage.Infrastructure.NetworkStorage.VersionPolicyEvaluator.Evaluation decision,
        long? clientRevision)
    {
        if (!SboxNetworkStorage.Infrastructure.NetworkStorage.VersionPolicyEvaluator.HasExplicitPolicy(project))
            return null;
        if (!clientRevision.HasValue || !decision.CurrentRevision.HasValue)
            return null;

        var mode = project.RevisionEnforcementMode;
        if (mode != "force_upgrade" && mode != "allow_continue")
            mode = "allow_continue";

        var graceMinutes = SboxNetworkStorage.Infrastructure.NetworkStorage.VersionPolicyEvaluator.GraceMinutesFor(project);
        var policy = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["gracePeriodMinutes"] = graceMinutes,
            ["postGraceAction"] = SboxNetworkStorage.Infrastructure.NetworkStorage.VersionPolicyEvaluator.AfterGraceActionFor(project),
            ["forceEndpointUpgrade"] = project.RevisionForceEndpointUpgrade == true,
            ["notifyMessage"] = string.IsNullOrWhiteSpace(project.RevisionNotifyMessage) ? null : project.RevisionNotifyMessage.Trim(),
            ["showUpdateOptions"] = project.RevisionShowUpdateOptions != false,
            ["showPopupOnce"] = project.RevisionShowPopupOnce != false,
            ["showDefaultMessage"] = project.RevisionShowDefaultMessage != false,
            ["enforcementMode"] = mode,
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["isOutdatedRevision"] = decision.IsOutdated,
            ["graceRemainingMinutes"] = decision.GraceRemainingMinutes,
            ["graceExpired"] = decision.GraceExpired,
            ["action"] = decision.IsOutdated ? decision.Action : "none",
            ["message"] = decision.Message,
            ["playerRevision"] = clientRevision.Value,
            ["currentRevision"] = decision.CurrentRevision.Value,
            ["enforcementMode"] = mode,
            ["policy"] = policy,
        };
    }

    /// <summary>
    /// Attaches <c>_revisionStatus</c> to a response body as a top-level
    /// property. <see cref="JsonElement"/> payloads, anonymous envelopes
    /// ({ ok, error }) and executor payloads all merge without renaming
    /// existing fields. Returns the original body when there is nothing to
    /// attach or the body cannot be represented as an object.
    /// </summary>
    public static object WithStatus(object body, Dictionary<string, object?>? status)
    {
        if (status is null) return body;
        if (body is Dictionary<string, object?> dictBody)
        {
            if (!dictBody.ContainsKey("_revisionStatus"))
                dictBody["_revisionStatus"] = status;
            return dictBody;
        }
        try
        {
            var raw = body is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(body);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return body;
            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            dict["_revisionStatus"] = status;
            return dict;
        }
        catch
        {
            return body;
        }
    }
}
