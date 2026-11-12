using System.Text.Json.Nodes;
using DbDataKit.Access;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// What a publish says about the rules tables of a definition's <c>access</c> section (plan 22a GP02, QR2). A rules table is to be built by a model Build published (QG2); the group can only check that when it holds the Build model's published
/// metadata (<c>build-metadata</c>, plan 27a KN13), and the Gateway does not host that kind yet, so the publish succeeds and its answer carries a warning for each rules table naming what was and was not checked.
/// </summary>
internal static class AccessPublishNotes
{
    /// <summary>The warning of a Store or entity model that has row rules (plan 22a QR6).</summary>
    public const string EntityWritesWarning = "Row rules limit reads only, not writes: a person may still change a row they cannot read through the Store's own roles and approvals.";

    /// <summary>The warnings of a publish whose definition has this section, or null when it has no rules table.</summary>
    /// <param name="readOnDataSource">Whether the publish read the table on the data source (a semantic model's does; an entity model's publish reads no table).</param>
    public static JsonArray? Warnings(AccessSection? access, bool readOnDataSource)
    {
        if (access is null) return null;
        var tables = access.RulesTables().Keys.Order(StringComparer.Ordinal).ToList();
        if (tables.Count == 0) return null;
        var checkedText = readOnDataSource ? "It was checked that the data source reads the table and the columns the rules name, nothing more." : "Nothing was read on the data source.";
        var notes = tables.Select(t => (JsonNode)$"The rules table '{t}' is not known to be built by a published Build model: the group holds no build-metadata to check it against (plan 27a KN13). {checkedText}").ToList();
        // QR6 (owner, 2026-11-12): an entity model's row rules limit reads only
        if (!readOnDataSource && access.Row.Count > 0) notes.Add(EntityWritesWarning);
        return new JsonArray([.. notes]);
    }
}
