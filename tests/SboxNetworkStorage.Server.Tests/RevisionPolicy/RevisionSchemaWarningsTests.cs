using System.Text.Json;
using SboxNetworkStorage.Server.Owner;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Phase 4 (game-version-policy, 8.3): the Versions page warns when a newer
/// revision changed a collection schema in a non-additive way (removed
/// collections or removed fields). Records are shared across revisions.
/// </summary>
public sealed class RevisionSchemaWarningsTests
{
    private static JsonElement Collection(string name, string schemaJson) =>
        JsonDocument.Parse($"{{\"collection_id\":\"{name}\",\"name\":\"{name}\",\"definition_json\":{schemaJson}}}").RootElement.Clone();

    [Fact]
    public void IdenticalCollections_NoWarning()
    {
        var live = new[] { Collection("players", "{\"schema\":{\"properties\":{\"gold\":{},\"name\":{}}}}") };
        Assert.Null(RevisionSchemaWarnings.Compare(live, live));
    }

    [Fact]
    public void AddedCollectionOrField_NoWarning()
    {
        var old = new[] { Collection("players", "{\"schema\":{\"properties\":{\"gold\":{}}}}") };
        var live = new[]
        {
            Collection("players", "{\"schema\":{\"properties\":{\"gold\":{},\"level\":{}}}}"),
            Collection("scores", "{\"schema\":{\"properties\":{\"score\":{}}}}"),
        };
        Assert.Null(RevisionSchemaWarnings.Compare(old, live));
    }

    [Fact]
    public void RemovedCollection_Warns()
    {
        var old = new[] { Collection("players", "{}"), Collection("legacy", "{}") };
        var live = new[] { Collection("players", "{}") };
        var warning = RevisionSchemaWarnings.Compare(old, live);
        Assert.NotNull(warning);
        Assert.Contains("legacy", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedField_Warns()
    {
        var old = new[] { Collection("players", "{\"schema\":{\"properties\":{\"gold\":{},\"gems\":{}}}}") };
        var live = new[] { Collection("players", "{\"schema\":{\"properties\":{\"gold\":{}}}}") };
        var warning = RevisionSchemaWarnings.Compare(old, live);
        Assert.NotNull(warning);
        Assert.Contains("players.gems", warning, StringComparison.Ordinal);
    }
}
