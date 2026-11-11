using DbDataKit.Lineage;

namespace DbDataKit.Knowledge;

/// <summary>One thing the lineage graph says exists, named the way a knowledge item names its subject.</summary>
public sealed record LineageSubject(string Type, string Name, string NarrowingKind = "", string NarrowingName = "")
{
    public KnowledgeSubject ToSubject() => new(Type, [Name], NarrowingKind, NarrowingName);

    public string Key => ToSubject().Key;
}

/// <summary>
/// The catalog of subjects read from the lineage graph (plan 27a KN02's adapter): what exists to be written about is what the graph has a node for, by the identity the graph already gives it, so a subject is the same thing for knowledge, lineage and the
/// catalog. The graph is the joined graph of the servers a console manages (plan 18). What its nodes say:
/// <list type="bullet">
/// <item><c>semantic-model</c> with <c>measure</c> or <c>dimension</c>: a semantic member node (Build's <c>catalog</c>, <c>member</c> and <c>member_kind</c> properties: a measure by its name, an attribute as <c>dimension.attribute</c>, so the dimension exists with it); a published semantic model is a package node.</item>
/// <item><c>app</c>, <c>integration</c>, <c>store-model</c>: a deployment node of type <c>package</c> whose kind is <c>app</c>, <c>integration</c> or <c>entity-model</c>, by the package's name.</item>
/// <item><c>connection</c>: a dataset node's <c>connection</c> property (the connection of Build's project it is read through).</item>
/// </list>
/// What the graph does not have a node for (<c>build-project</c>, <c>tag</c>, <c>property</c>, a Store model's fields) is not answered here: a host puts another catalog beside this one with <see cref="CompositeSubjectCatalog"/>. A subject the graph does not show is
/// <see cref="SubjectStatus.Unknown"/>, or <see cref="SubjectStatus.Gone"/> when the keys given as <c>previous</c> (the keys a graph showed before) had it: the graph keeps no history, so the host that refreshes keeps the last answer and tells the difference between "dropped" and "never existed".
/// </summary>
public sealed class LineageSubjectCatalog : ISubjectCatalog
{
    private static readonly Dictionary<string, string> PackageKinds = new(StringComparer.Ordinal) { ["app"] = "app", ["integration"] = "integration", ["entity-model"] = "store-model", ["semantic-model"] = "semantic-model" };

    private readonly HashSet<string> _present = new(StringComparer.Ordinal);
    private readonly HashSet<string> _previous;

    public LineageSubjectCatalog(LineageGraph graph, IEnumerable<string>? previous = null)
    {
        _previous = [.. previous ?? []];
        Subjects = [.. Read(graph).Distinct().OrderBy(s => s.Type, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.NarrowingKind, StringComparer.Ordinal).ThenBy(s => s.NarrowingName, StringComparer.Ordinal)];
        foreach (var subject in Subjects) _present.Add(subject.Key);
    }

    /// <summary>Everything the graph says exists, in a fixed order.</summary>
    public IReadOnlyList<LineageSubject> Subjects { get; }

    public SubjectStatus Status(KnowledgeSubject subject)
    {
        var self = StatusOf(new KnowledgeSubject(subject.Type, subject.Names).Key);
        if (subject.NarrowingKind.Length == 0 || self != SubjectStatus.Exists) return self;
        return StatusOf(subject.Unpositioned().Key);
    }

    private SubjectStatus StatusOf(string key) => _present.Contains(key) ? SubjectStatus.Exists : _previous.Contains(key) ? SubjectStatus.Gone : SubjectStatus.Unknown;

    private static IEnumerable<LineageSubject> Read(LineageGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            var p = node.PropertyMap;
            switch (node.Kind)
            {
                case NodeKind.SemanticMember when p.TryGetValue("catalog", out var catalog) && catalog.Length > 0:
                    yield return new LineageSubject("semantic-model", catalog);
                    if (p.GetValueOrDefault("member") is { Length: > 0 } member)
                    {
                        var kind = p.GetValueOrDefault("member_kind");
                        if (kind == "measure") yield return new LineageSubject("semantic-model", catalog, "measure", member);
                        else if (kind == "attribute" && member.Split('.', 2) is { Length: 2 } parts) yield return new LineageSubject("semantic-model", catalog, "dimension", parts[0]);
                    }
                    break;
                case NodeKind.Deployment when p.GetValueOrDefault(DeploymentProperties.Type) == DeploymentTypes.Package
                    && p.GetValueOrDefault(DeploymentProperties.Kind) is { } packageKind && PackageKinds.TryGetValue(packageKind, out var type)
                    && p.GetValueOrDefault(DeploymentProperties.Name) is { Length: > 0 } name:
                    yield return new LineageSubject(type, name);
                    break;
                case NodeKind.Dataset when p.GetValueOrDefault("connection") is { Length: > 0 } connection:
                    yield return new LineageSubject("connection", connection);
                    break;
            }
        }
    }
}

/// <summary>
/// Several catalogs asked as one: a subject exists when any of them says so; it is gone when none says it exists and one says it is gone; otherwise it is unknown. A host that has the lineage graph (<see cref="LineageSubjectCatalog"/>) and also a list of
/// the Build projects or the tags it keeps itself puts them together here.
/// </summary>
public sealed class CompositeSubjectCatalog(params ISubjectCatalog[] catalogs) : ISubjectCatalog
{
    public SubjectStatus Status(KnowledgeSubject subject)
    {
        var all = catalogs.Select(c => c.Status(subject)).ToList();
        return all.Contains(SubjectStatus.Exists) ? SubjectStatus.Exists : all.Contains(SubjectStatus.Gone) ? SubjectStatus.Gone : SubjectStatus.Unknown;
    }
}
