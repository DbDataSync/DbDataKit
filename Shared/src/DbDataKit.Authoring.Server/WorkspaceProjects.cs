using System.Text.RegularExpressions;
using DbDataKit.Apps;
using DbDataKit.Authoring;
using DbDataKit.Config.Git;
using DbDataKit.State;

namespace DbDataKit.Authoring.Server;

/// <summary>Why a project could not be made or added, in a sentence a person can read; <see cref="Status"/> is the HTTP status the route answers with.</summary>
public sealed class WorkspaceProjectException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The projects this server hosts (plan 22a GP31), kept in the <c>workspace</c> state module and served to the shared authoring calls (<see cref="IAuthoringProjects"/>, GP30). A project is a git repository: one the server made in
/// <c>projects/&lt;id&gt;</c> of its directory, or one already on this machine that an operator added by path. A project has a kind (<c>app</c> today; plan 27 adds the others), which the server records and does not interpret.
/// </summary>
public sealed partial class WorkspaceProjects(string directory, WorkspaceStore store, ProjectKinds? registry = null) : IAuthoringProjects
{
    private readonly object gate = new();

    /// <summary>The kinds this server hosts (plan 33 SC20a): what a new project starts as and how a draft is checked are the kind's, not this class's.</summary>
    public ProjectKinds Kinds { get; } = registry ?? StandardProjectKinds.Create();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public string RootOf(WorkspaceProjectRow project) => project.Path ?? Path.Combine(directory, "projects", project.Id);

    public AuthoringProject? Find(string id) => store.FindProject(id) is { } project ? new AuthoringProject(project.Id, RootOf(project), null) : null;

    /// <summary>A project in a folder of this machine has no remote login.</summary>
    public GitCredential? CredentialFor(AuthoringProject project) => null;

    /// <summary>
    /// Makes a new project: the folder <c>projects/&lt;id&gt;</c> becomes a git repository with a first commit in the person's name, and is hosted from then on. Nothing is overwritten: an id that is taken, or a folder that is already
    /// there and not empty, is refused. An app project starts as the app template (plan 22a GP32, `DbDataKit.Apps.AppTemplate`), any other kind with a README; the attributes file is in both.
    /// </summary>
    public WorkspaceProjectRow Create(string id, string kind, string? title, GitAuthor author, string createdBy, string? template = null)
    {
        CheckId(id, kind);
        if (template is not null && Kinds.Find(kind)!.Templates.All(t => t.Name != template))
            throw new WorkspaceProjectException($"The {kind} kind has no template called '{template}' ({(Kinds.Find(kind)!.Templates.Count == 0 ? "it has none" : string.Join(", ", Kinds.Find(kind)!.Templates.Select(t => t.Name)))}).", 400);
        lock (gate)
        {
            if (store.FindProject(id) is not null) throw new WorkspaceProjectException($"This server already hosts a project called '{id}'.", 409);
            var folder = Path.Combine(directory, "projects", id);
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                throw new WorkspaceProjectException($"The server already has a folder for '{id}' that is not empty, so no project was made over it.", 409);
            try
            {
                var projectKind = Kinds.Require(kind);
                // a new project starts as its kind's template (an app: the app template, plan 22a GP32), named for the project; made before the folder, so a kind that cannot make it leaves nothing behind
                IReadOnlyList<ProjectKindFile> files;
                try { files = projectKind.Template(id, title, template); }
                catch (ProjectKindUnavailableException ex) { throw new WorkspaceProjectException(ex.Message, 409); }
                Directory.CreateDirectory(folder);
                foreach (var file in files)
                {
                    var target = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, file.Content);
                }
                if (projectKind.GitIgnore is { } ignore) File.WriteAllText(Path.Combine(folder, ".gitignore"), ignore);
                File.WriteAllText(Path.Combine(folder, ".gitattributes"), "* text=auto eol=lf\n");
                LibGit2Sharp.Repository.Init(folder);
                GitWorkspaceOperations.Commit(folder, $"New {kind} project", author);
                store.AddProject(id, kind, title, null, createdBy);
                return store.FindProject(id)!;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GitOperationException or LibGit2Sharp.LibGit2SharpException)
            {
                // a half-made folder is removed so the name can be tried again
                try
                {
                    if (Directory.Exists(folder))
                    {
                        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                        Directory.Delete(folder, true);
                    }
                }
                catch (IOException) { }
                throw new WorkspaceProjectException($"The project could not be made: {ex.Message}", 500);
            }
        }
    }

    /// <summary>Hosts a repository that is already on this machine, in place (its folder is where merges land).</summary>
    public WorkspaceProjectRow Add(string id, string kind, string? title, string path, string createdBy)
    {
        CheckId(id, kind);
        var full = Path.GetFullPath(path);
        if (!GitCommitService.IsRepositoryAt(full)) throw new WorkspaceProjectException($"'{full}' is not the folder of a git repository (it has no .git folder).", 400);
        lock (gate)
        {
            if (!store.AddProject(id, kind, title, full, createdBy)) throw new WorkspaceProjectException($"This server already hosts a project called '{id}'.", 409);
            return store.FindProject(id)!;
        }
    }

    private void CheckId(string id, string kind)
    {
        if (!IsValidId(id)) throw new WorkspaceProjectException($"'{id}' is not a project name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 32.", 400);
        if (Kinds.Find(kind) is null) throw new WorkspaceProjectException($"'{kind}' is not a kind of project this server knows ({string.Join(", ", Kinds.Names)}).", 400);
    }
}
