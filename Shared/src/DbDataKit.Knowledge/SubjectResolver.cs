using DbDataKit.Config.Yaml;

namespace DbDataKit.Knowledge;

/// <summary>What the catalog says about a subject (plan 27a KN02).</summary>
public enum SubjectStatus
{
    /// <summary>The object is there now.</summary>
    Exists,

    /// <summary>The object was there and is gone (a model dropped): its items are orphaned.</summary>
    Gone,

    /// <summary>The catalog has never heard of it: an item about it is refused when it is saved.</summary>
    Unknown,
}

/// <summary>
/// What a host knows about the objects knowledge can be about: the semantic models and their members, the Build projects, the Store models, connections, apps, integrations, tags and properties. A host answers from the lineage graph's stable
/// identities and the definitions it holds; <c>product</c> and <c>install</c> never need it (<see cref="SubjectResolver"/>).
/// </summary>
public interface ISubjectCatalog
{
    /// <summary>The status of a subject of any type but <c>product</c> and <c>install</c>, with its narrowing (a measure, a folder): a narrowed subject exists when the object exists and has that member.</summary>
    SubjectStatus Status(KnowledgeSubject subject);
}

/// <summary>A catalog held in memory: what a test, a command over a checked-out project or a host that has the names to hand uses.</summary>
public sealed class SubjectCatalog : ISubjectCatalog
{
    private readonly Dictionary<string, SubjectStatus> _known = new(StringComparer.Ordinal);

    /// <summary>Says an object (or one member of it) exists: <c>Add("semantic-model", "Sales")</c> and <c>Add("semantic-model", "Sales", "measure", "revenue")</c>. A member's object is added with it.</summary>
    public SubjectCatalog Add(string type, string name, string narrowingKind = "", string narrowingName = "")
    {
        _known[Key(type, name, "", "")] = SubjectStatus.Exists;
        if (narrowingKind.Length > 0) _known[Key(type, name, narrowingKind, narrowingName)] = SubjectStatus.Exists;
        return this;
    }

    /// <summary>Says an object that existed is gone (with its members).</summary>
    public SubjectCatalog Retire(string type, string name)
    {
        foreach (var key in _known.Keys.Where(k => k == Key(type, name, "", "") || k.StartsWith(Key(type, name, "", "") + "/", StringComparison.Ordinal)).ToList())
            _known[key] = SubjectStatus.Gone;
        return this;
    }

    public SubjectStatus Status(KnowledgeSubject subject)
    {
        var self = _known.GetValueOrDefault(Key(subject.Type, subject.Name, "", ""), SubjectStatus.Unknown);
        if (subject.NarrowingKind.Length == 0 || self != SubjectStatus.Exists) return self;
        return _known.GetValueOrDefault(Key(subject.Type, subject.Name, subject.NarrowingKind, subject.NarrowingName), SubjectStatus.Unknown);
    }

    private static string Key(string type, string name, string kind, string narrowing) => $"{type}:{name}" + (kind.Length > 0 ? $"/{kind}:{narrowing}" : "");
}

/// <summary>
/// Subjects and orphans (plan 27a KN02, plan 27 section 3.5c item 5): a subject is checked against the catalog when an item is saved, and an item whose subjects are all gone is <b>orphaned</b>: it reaches nobody and is listed for its owner.
/// <c>product</c> and <c>install</c> resolve with no catalog. An item about several subjects (any of them puts it in scope) is orphaned only when none is left.
/// </summary>
public static class SubjectResolver
{
    public const string UnknownSubject = "DBD-KNW-010";

    public static SubjectStatus Status(ISubjectCatalog catalog, KnowledgeSubject subject) =>
        subject.Type is "product" or "install" ? SubjectStatus.Exists : catalog.Status(subject);

    /// <summary>The errors of an item being saved: each subject the catalog has never heard of, at the subject's own line and column. A subject that is gone is not an error here (the item is orphaned, see <see cref="IsOrphaned"/>).</summary>
    public static IReadOnlyList<ConfigDiagnostic> CheckOnSave(KnowledgeItem item, ISubjectCatalog catalog)
    {
        var errors = new List<ConfigDiagnostic>();
        foreach (var subject in item.About)
            if (Status(catalog, subject) == SubjectStatus.Unknown)
                errors.Add(DefaultDiagnosticCatalog.Instance.Create(UnknownSubject, item.File, Math.Max(subject.Line, 1), Math.Max(subject.Column, 1),
                    $"The subject `{subject.Key}` is not in the catalog: nothing of that name has ever been published or created here.",
                    "Check the spelling against `semantic describe` or the project's own name, or publish the object first."));
        return errors;
    }

    /// <summary>True when the item names subjects and none of them exists now. An item with no subject is about nothing in particular and cannot be orphaned.</summary>
    public static bool IsOrphaned(KnowledgeItem item, ISubjectCatalog catalog) =>
        item.About.Count > 0 && item.About.All(s => Status(catalog, s) != SubjectStatus.Exists);

    /// <summary>The orphans among the items, in the order given.</summary>
    public static IReadOnlyList<KnowledgeItem> Orphans(IEnumerable<KnowledgeItem> items, ISubjectCatalog catalog) => [.. items.Where(i => IsOrphaned(i, catalog))];
}
