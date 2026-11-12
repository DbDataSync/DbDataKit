using System.Text.RegularExpressions;
using DbDataKit.Access;
using DbDataKit.Config.Yaml;
using DbDataKit.Dialect;

namespace DbDataKit.DataModel;

/// <summary>
/// What a model says about its entities to the access reader (plan 22a GP01): the entities are the datasets, their attributes the columns, the model's storage connection the source. A rules table is <c>schema.table</c> on that connection,
/// built by a published Build model; the model's own tables are never one (a Store table is never read directly).
/// </summary>
internal sealed partial class ModelAccessTarget(string model, string connection, IReadOnlyList<ModelEntity> entities, CacheSection? cache) : IAccessTarget
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\.[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex QualifiedName();

    public AccessDataset? Dataset(string name) => entities.FirstOrDefault(e => e.Name == name) is { } entity
        ? new AccessDataset(entity.Name, connection, entity.Attributes.ToDictionary(a => a.Name, a => KindOf(a.Canonical.Kind), StringComparer.Ordinal), "store")
        : null;

    public IReadOnlyList<string> DatasetNames => [.. entities.Select(e => e.Name)];

    public AccessDataset? RulesTable(string reference, AccessDataset filtered) => QualifiedName().IsMatch(reference) ? new AccessDataset(reference, connection, null) : null;

    public string? WhyNotRulesTable(AccessDataset table)
    {
        var parts = table.Name.Split('.');
        var inModelSchema = parts[0] == model || parts[0].StartsWith(model + "__", StringComparison.Ordinal);
        return inModelSchema && entities.Any(e => string.Equals(e.Name, parts[1], StringComparison.OrdinalIgnoreCase))
            ? "it is a table of this model's own entities, and a Store table is never read directly: a Build model derives the rules table from it"
            : null;
    }

    public string? WhyCannotFilter(AccessDataset dataset) => null;

    public bool CacheSharedAcrossCallers => cache is { Enabled: true, Scope: CacheScope.Shared };

    private static AccessColumnKind KindOf(CanonicalKind kind) => kind switch
    {
        CanonicalKind.String => AccessColumnKind.Text,
        CanonicalKind.Int8 or CanonicalKind.Int16 or CanonicalKind.Int32 or CanonicalKind.Int64 or CanonicalKind.Decimal or CanonicalKind.Float or CanonicalKind.Double => AccessColumnKind.Number,
        CanonicalKind.Boolean => AccessColumnKind.Boolean,
        CanonicalKind.Date or CanonicalKind.Time or CanonicalKind.Timestamp => AccessColumnKind.Date,
        _ => AccessColumnKind.Other,
    };
}
