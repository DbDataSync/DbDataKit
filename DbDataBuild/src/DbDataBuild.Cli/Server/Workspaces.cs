using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Config.Git;

namespace DbDataBuild.Cli.Server;

/// <summary>One person's draft of a project: a git worktree on a branch of its own, in a folder the server owns.</summary>
/// <param name="Id">What the API calls it: the owner and the name, <c>ada-new-orders</c>.</param>
/// <param name="Owner">The owner's id as the token carries it; only they run commands in it.</param>
/// <param name="Branch">The branch the draft is committed on, <c>ws/&lt;id&gt;</c>.</param>
/// <param name="Base">The commit the draft started at.</param>
internal sealed record Workspace(string Id, string Project, string Owner, string OwnerName, string Name, string Branch, string Base, DateTimeOffset Created);

/// <summary>
/// Workspaces on a Build server (plan 13, W02): authoring happens in a git worktree per person and name, never in the checkout the scheduler and the deploys read, so a draft cannot change what runs. A workspace starts at the
/// commit the project's checkout is at; it is listed, and abandoned (the worktree and its branch removed) by its owner. What is in <c>workspaces/&lt;project&gt;/</c> of the server's directory: the worktree folder <c>&lt;id&gt;</c> and
/// a record <c>&lt;id&gt;.json</c> beside it that says whose it is.
/// </summary>
internal sealed partial class Workspaces(string serverDirectory, TimeProvider clock)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    private string FolderOf(string project) => Path.Combine(serverDirectory, "workspaces", project);

    /// <summary>The folder commands run in for this workspace.</summary>
    public string RootOf(Workspace workspace) => Path.Combine(FolderOf(workspace.Project), workspace.Id);

    /// <summary>Makes the workspace. Throws <see cref="ArgumentException"/> for a name that is not valid or already taken and <see cref="GitWorktreeException"/> when git cannot make it (the project is not a repository, say).</summary>
    public Workspace Create(string project, string projectRoot, string ownerId, string ownerName, string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"'{name}' is not a workspace name: lower-case letters, digits, '-' and '_', at most 32.");
        if (!GitCommitService.IsRepositoryAt(projectRoot)) throw new GitWorktreeException("This project is not a git repository, so it has no workspaces.");
        var id = $"{Slug(ownerId)}-{name}";
        if (Find(project, id) is not null) throw new ArgumentException($"You already have a workspace called '{name}'.");
        var branch = $"ws/{id}";
        var folder = Path.Combine(FolderOf(project), id);
        var start = GitWorktrees.Add(projectRoot, branch, folder);
        var workspace = new Workspace(id, project, ownerId, ownerName, name, branch, start, clock.GetUtcNow());
        File.WriteAllText(RecordOf(project, id), JsonSerializer.Serialize(workspace));
        return workspace;
    }

    public IReadOnlyList<Workspace> List(string project)
    {
        var folder = FolderOf(project);
        if (!Directory.Exists(folder)) return [];
        var found = new List<Workspace>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<Workspace>(File.ReadAllText(file)) is { } w && Directory.Exists(Path.Combine(folder, w.Id))) found.Add(w);
            }
            catch (JsonException) { }
        }
        return found;
    }

    public Workspace? Find(string project, string id) => List(project).FirstOrDefault(w => w.Id == id);

    /// <summary>Removes the worktree, its branch and its record. Whatever was committed there and not merged is gone with the branch.</summary>
    public void Abandon(Workspace workspace, string projectRoot)
    {
        GitWorktrees.Remove(projectRoot, workspace.Branch, RootOf(workspace));
        var record = RecordOf(workspace.Project, workspace.Id);
        if (File.Exists(record)) File.Delete(record);
    }

    private string RecordOf(string project, string id) => Path.Combine(FolderOf(project), id + ".json");

    private static string Slug(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-') is { Length: > 0 } s ? s[..Math.Min(s.Length, 24)] : "user";
}
