namespace DbDataKit.Authoring;

/// <summary>A file a new project of a kind starts with: where (relative, <c>/</c> between the parts) and what.</summary>
public sealed record ProjectKindFile(string Path, byte[] Content);

/// <summary>One finding of a kind's check of a draft, in the shape every kind answers: <see cref="Severity"/> is <c>error</c>, <c>warning</c> or <c>info</c>; <see cref="File"/> and <see cref="Line"/> say where, when the check knows.</summary>
public sealed record ProjectDiagnostic(string Severity, string Code, string Message, string? File = null, int? Line = null)
{
    public bool IsError => Severity == "error";
}

/// <summary>What a kind made of a draft to publish (plan 33 SC20d, SC21b, SC22b): whether it worked, a sentence, the findings that stopped it, and what it wrote down about it (the tasks of a package, the models), kept with the build.</summary>
public sealed record ProjectArtifact(bool Succeeded, string Message, IReadOnlyList<ProjectDiagnostic> Diagnostics, IReadOnlyDictionary<string, string>? Info = null);

/// <summary>
/// What a project of a kind can be published as, and to which product (plan 33 SC20b): <see cref="Product"/> is the kind of server that takes it (<c>gateway</c>, <c>runner</c>), <see cref="What"/> the thing that arrives there
/// (<c>app</c>, <c>package</c>, <c>model</c>, <c>connection</c>), <see cref="Description"/> a sentence for the screen.
/// </summary>
public sealed record ProjectPublishRoute(string Product, string What, string Description);

/// <summary>
/// A kind of project the Workspace hosts (plan 33 SC20a): an app today, a Build project, a Store model and a Sync replication set beside it. A kind is a row of a registry, not a code path: it says what a new project starts as
/// (<see cref="Template(string, string?)"/>), how a draft of it is checked (<see cref="CheckAsync"/>, with the same validators the standalone product uses) and what it publishes as and where (<see cref="Publishes"/>). The server's calls (list the
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

    /// <summary>The starting points a new project of this kind can be made from besides its plain one (plan 27a IX21: templates generate ordinary code the author then owns). None by default.</summary>
    IReadOnlyList<ProjectTemplate> Templates => [];

    /// <summary>The files of a new project made from the starting point <paramref name="template"/> (one of <see cref="Templates"/>); null is the plain one, <see cref="Template(string, string?)"/>.</summary>
    IReadOnlyList<ProjectKindFile> Template(string id, string? title, string? template) =>
        template is null ? Template(id, title) : throw new ArgumentException($"The {Name} kind has no template called '{template}'.", nameof(template));

    /// <summary>True when the kind makes something to publish from a draft (<see cref="ProduceAsync"/>): a package of tasks for a Runner, the models of a Store. An app is bundled instead (<see cref="Buildable"/>).</summary>
    bool Produces => false;

    /// <summary>
    /// Makes what <see cref="Publishes"/> says from the draft in <paramref name="root"/> into <paramref name="outputFolder"/> (a build's folder, empty): for a <c>package</c> route <c>package.yaml</c> and <c>files/</c>, for a <c>model</c> route <c>models/&lt;name&gt;.yaml</c> in the current format.
    /// The draft is checked first by the caller; a kind that cannot produce says why in <see cref="ProjectArtifact.Diagnostics"/>. <paramref name="projectId"/> names the package.
    /// </summary>
    Task<ProjectArtifact> ProduceAsync(string root, string projectId, string outputFolder, CancellationToken cancellationToken) => throw new NotSupportedException($"The {Name} kind makes nothing to publish.");

    /// <summary>Checks the draft in <paramref name="root"/> and returns what is wrong with it; empty means it is sound. Never throws for a damaged file: that is a diagnostic.</summary>
    Task<IReadOnlyList<ProjectDiagnostic>> CheckAsync(string root, CancellationToken cancellationToken);
}

/// <summary>A starting point a screen offers for a new project of a kind: its name (what the API says), a title and what it generates.</summary>
public sealed record ProjectTemplate(string Name, string Title, string Description);

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

/// <summary>A kind cannot do what was asked on this server (a program it runs is not installed, say): a sentence a person can act on, never a stack trace. The server answers 409 with it.</summary>
public sealed class ProjectKindUnavailableException(string message) : Exception(message);
