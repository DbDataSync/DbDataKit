using DbDataKit.Dialect;

namespace DbDataKit.DataModel;

/// <summary>Who must approve a row change (plan 15, section 3).</summary>
public enum ApprovalLevel { None, Steward, Owner }

/// <summary>How an entity keeps its past: not at all, who changed what and when, or versioned rows with valid-from and valid-to.</summary>
public enum HistoryMode { None, Audit, EffectiveDated }

/// <summary>What an entity is to the semantic layer, when it says (plan 15, section 3).</summary>
public enum EntityRole { Table, Dimension, Mapping, Calendar }

/// <summary>An attribute that points at another entity's key, in this model or in another one.</summary>
public sealed record AttributeReference(string? Model, string Entity, string? Attribute);

/// <summary>A column of an entity and the rules that belong to a single value. <see cref="Type"/> is the text as written; <see cref="Canonical"/> is what it means.</summary>
public sealed record ModelAttribute(
    string Name,
    string Type,
    CanonicalType Canonical,
    bool Required = false,
    bool Unique = false,
    string? Default = null,
    string? Pattern = null,
    decimal? Min = null,
    decimal? Max = null,
    IReadOnlyList<string>? Allowed = null,
    AttributeReference? References = null,
    string? Title = null);

/// <summary>The kinds of rule that span rows, a small closed set (plan 15, section 3).</summary>
public enum RuleKind { SumEquals, NoOverlap, NoCycle }

/// <summary>A rule across rows. Which fields are used depends on <see cref="Kind"/>: <c>sum_equals</c> uses attribute, group_by, value and tolerance; <c>no_overlap</c> uses group_by; <c>no_cycle</c> uses attribute (the one that points to the parent).</summary>
public sealed record ModelRule(RuleKind Kind, string? Attribute, IReadOnlyList<string> GroupBy, decimal? Value, decimal Tolerance);

public sealed record ModelEntity(
    string Name,
    IReadOnlyList<string> Key,
    IReadOnlyList<ModelAttribute> Attributes,
    HistoryMode History = HistoryMode.None,
    EntityRole Role = EntityRole.Table,
    IReadOnlyList<ModelRule>? Rules = null,
    string? Title = null)
{
    public ModelAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);
}

/// <summary>How a model's instances are kept apart (plan 26 section 6.2): not at all (one copy), each in a schema of its own on the model's connection, or each on a connection of its own. Sharing the tables and splitting rows by instance id is planned (Z12b).</summary>
public enum InstanceMode { None, Schema, Connection }

/// <summary>A model: entities with attributes, keys, references and rules, stored on the named connection.</summary>
public sealed record DataModel(string Name, string? Title, string Connection, ApprovalLevel Approval, IReadOnlyList<ModelEntity> Entities, InstanceMode Instances = InstanceMode.None)
{
    public const string FormatId = "model/1";

    public ModelEntity? Entity(string name) => Entities.FirstOrDefault(e => e.Name == name);

    /// <summary>The longest schema name both engines take (PostgreSQL's).</summary>
    private const int SchemaLimit = 63;

    /// <summary>The schema an instance's tables are in: the model's name, or for a model that keeps its instances in schemas, the name and the instance (<c>territories__acme</c>). Null with a reason when the instance cannot be named.</summary>
    public string? SchemaOf(string? instance, out string? problem)
    {
        problem = null;
        if (instance is null or "default" || Instances != InstanceMode.Schema) return Name;
        var schema = $"{Name}__{instance}";
        if (schema.Length > SchemaLimit) { problem = $"The schema name `{schema}` is {schema.Length} characters; the limit is {SchemaLimit}. Use a shorter instance name."; return null; }
        return schema;
    }
}
