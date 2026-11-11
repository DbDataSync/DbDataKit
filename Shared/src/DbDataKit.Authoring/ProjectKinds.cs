namespace DbDataKit.Authoring;

/// <summary>A file a new project of a kind starts with: where (relative, <c>/</c> between the parts) and what.</summary>
public sealed record ProjectKindFile(string Path, byte[] Content);

/// <summary>One finding of a kind's check of a draft, in the shape every kind answers: <see cref="Severity"/> is <c>error</c>, <c>warning</c> or <c>info</c>; <see cref="File"/> and <see cref="Line"/> say where, when the check knows.</summary>
public sealed record ProjectDiagnostic(string Severity, string Code, string Message, string? File = null, int? Line = null)
{
    public bool IsError => Severity == "error";
}

/// <summary>
/// What a project of a kind can be published as, and to which product (plan 33 SC20b): <see cref="Product"/> is the kind of server that takes it (<c>gateway</c>, <c>runner</c>), <see cref="What"/> the thing that arrives there
/// (<c>app</c>, <c>package</c>, <c>model</c>, <c>connection</c>), <see cref="Description"/> a sentence for the screen.
/// </summary>
public sealed record ProjectPublishRoute(string Product, string What, string Description);

/// <summary>
/// A kind of project the Workspace hosts (plan 33 SC20a): an app today, a Build project, a Store model and a Sync replication set beside it. A kind is a row of a registry, not a code path: it says what a new project starts as
/// (<see cref="Template"/>), how a draft of it is checked (<see cref="CheckAsync"/>, with the same validators the standalone product uses) and what it publishes as and where (<see cref="Publishes"/>). The server's calls (list the
/// kinds, make a project, check a draft, publish) read these and know no kind by name.
/// </summary>
public interface IProjectKind
{
    /// <summary>The name stored with a project (<c>app</c>); it is also what <c>--kind</c> and the API's <c>kind</c> say.</summary>
    string Name { get; }

    /// <summary>The words a screen puts on it.</summary>
    string Title { get; }

    string Description { get; }

    /// <summary>True when a draft of this kind can be built into something previewable (an app is bundled; the others are checked, not built).</summary>
    bool Buildable { get; }

    /// <summary>What <c>.gitignore</c> a new project gets, or null for none.</summary>
    string? GitIgnore { get; }

    IReadOnlyList<ProjectPublishRoute> Publishes { get; }

    /// <summary>The files of a new project <paramref name="id"/> (titled <paramref name="title"/> when given).</summary>
    IReadOnlyList<ProjectKindFile> Template(string id, string? title);

    /// <summary>Checks the draft in <paramref name="root"/> and returns what is wrong with it; empty means it is sound. Never throws for a damaged file: that is a diagnostic.</summary>
    Task<IReadOnlyList<ProjectDiagnostic>> CheckAsync(string root, CancellationToken cancellationToken);
}

/// <summary>The kinds a server hosts, by name, in the order they were added.</summary>
public sealed class ProjectKinds
{
    private readonly List<IProjectKind> kinds = [];

    public ProjectKinds(IEnumerable<IProjectKind> all)
    {
        foreach (var kind in all)
        {
            if (kinds.Any(k => k.Name == kind.Name)) throw new ArgumentException($"There are two project kinds called '{kind.Name}'.", nameof(all));
            kinds.Add(kind);
        }
    }

    public IReadOnlyList<IProjectKind> All => kinds;

    public IReadOnlyList<string> Names => [.. kinds.Select(k => k.Name)];

    public IProjectKind? Find(string name) => kinds.FirstOrDefault(k => k.Name == name);

    /// <summary>The kind, or a sentence naming the kinds that exist.</summary>
    public IProjectKind Require(string name) => Find(name) ?? throw new ArgumentException($"'{name}' is not a kind of project this server knows ({string.Join(", ", Names)}).", nameof(name));
}
