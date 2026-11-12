namespace DbDataKit.Access;

/// <summary>The kind of a column's values, as far as a rule needs it: text can be masked, and a rule's two columns must hold the same kind.</summary>
public enum AccessColumnKind { Unknown, Text, Number, Boolean, Date, Other }

/// <summary>A dataset of the definition the section is in: a table of a catalog, an entity of a model, or a rules table. <see cref="Source"/> is where it lives (a catalog source, a model's connection); two datasets are in the same target when it is equal.</summary>
/// <param name="Name">The name the definition calls it by.</param>
/// <param name="Source">Where it lives; equal for two datasets in one target.</param>
/// <param name="SourceKind">What the source is (<c>sqlserver</c>, <c>postgres</c>, <c>ssas</c>), when the host says.</param>
/// <param name="Columns">The columns it declares with their kinds, or null when it declares none (then no column of it is checked).</param>
public sealed record AccessDataset(string Name, string Source, IReadOnlyDictionary<string, AccessColumnKind>? Columns, string? SourceKind = null);

/// <summary>What a host tells the loader about the definition the section is in, so the section is checked against it (plan 22a GP01). A host that has nothing to check against passes none, and only the shape is read.</summary>
public interface IAccessTarget
{
    /// <summary>The datasets rules can be about, by name; null when the name is not one.</summary>
    AccessDataset? Dataset(string name);

    /// <summary>The names of the datasets, for a suggestion in a message.</summary>
    IReadOnlyList<string> DatasetNames { get; }

    /// <summary>The rules table a rule names (<paramref name="reference"/>) for the dataset it filters; null when the definition has no such table.</summary>
    AccessDataset? RulesTable(string reference, AccessDataset filtered);

    /// <summary>Why a table cannot be a rules table (a dimension, a measure or a relationship reads it, so people could browse who sees what); null when it can.</summary>
    string? WhyNotRulesTable(AccessDataset table);

    /// <summary>Why rules cannot filter a dataset (its source is one that no filter can reach); null when they can.</summary>
    string? WhyCannotFilter(AccessDataset dataset);

    /// <summary>Whether the definition's cache setting lets callers share an entry (<c>cache.scope: shared</c>); rules and a shared entry cannot go together.</summary>
    bool CacheSharedAcrossCallers { get; }
}
