using System.Text.RegularExpressions;
using DbDataKit.Config.Git;
using DbDataKit.Config.Yaml;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Cli.Server;

/// <summary>A project a Build server serves: a folder on this machine, or a remote repository the server keeps its own working copy of (plan 13, D19).</summary>
/// <param name="Id">What the management API calls it; lower-case letters, digits, <c>-</c> and <c>_</c>.</param>
/// <param name="Path">The project's folder, for a local project.</param>
/// <param name="Git">The remote repository's address, for a remote project.</param>
/// <param name="Branch">The branch followed; the remote's default when null.</param>
internal sealed record HostedProject(string Id, string? Path, string? Git, string? Branch)
{
    public bool IsRemote => Git is not null;
}

/// <summary>
/// The projects a server hosts (plan 13, R02): the <c>projects</c> list of <c>build/projects.yaml</c> in the server's directory, one flow mapping per project (<c>{id: sales, path: /srv/sales}</c> or
/// <c>{id: sales, git: https://host/sales.git, branch: main}</c>). Like the paired clients it is read again whenever the file changes, so a project added or removed by the command line is served or gone on the
/// next call; an entry that is not valid is skipped and named in <see cref="Problems"/>. A remote project's working copy is <c>projects/&lt;id&gt;</c> in the server's directory, which nobody edits.
/// </summary>
internal sealed partial class ProjectsFile(string serverDirectory)
{
    private readonly YamlListFile _file = new(System.IO.Path.Combine(serverDirectory, "build", "projects.yaml"), "projects");

    public string Path => _file.Path;

    public IReadOnlyList<string> Problems => [.. _file.Problems, .. Parse().Problems];

    public IReadOnlyList<HostedProject> All() => Parse().Projects;

    public HostedProject? Find(string id) => All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>The folder commands run in for this project.</summary>
    public string RootOf(HostedProject project) => project.Path ?? System.IO.Path.Combine(serverDirectory, "projects", project.Id);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    /// <summary>Adds the project, or replaces the one with the same id. Throws <see cref="ArgumentException"/> with the reason when it is not a valid project.</summary>
    public void Add(HostedProject project)
    {
        if (!IsValidId(project.Id)) throw new ArgumentException($"'{project.Id}' is not a project id: lower-case letters, digits, '-' and '_', starting with a letter or digit.");
        if ((project.Path is null) == (project.Git is null)) throw new ArgumentException("A project is a folder (--path) or a remote repository (--git), not both and not neither.");
        if (project.Path is not null && !File.Exists(System.IO.Path.Combine(project.Path, "dbdatabuild.yml")))
            throw new ArgumentException($"'{project.Path}' is not a Build project: it has no dbdatabuild.yml.");
        _file.Upsert(project.Id, Render(project));
    }

    public bool Remove(string id) => _file.Remove(id);

    /// <summary>Clones a remote project or brings its working copy up to the remote's branch; nothing to do for a local one.</summary>
    public GitRemoteResult? Pull(HostedProject project) =>
        project.IsRemote ? GitRemote.CloneOrUpdate(project.Git!, project.Branch, RootOf(project)) : null;

    private static string Render(HostedProject p) =>
        "{" + $"id: {YamlText.FlowScalar(p.Id)}, " + (p.Path is not null ? $"path: {YamlText.FlowScalar(p.Path)}" : $"git: {YamlText.FlowScalar(p.Git!)}" + (p.Branch is not null ? $", branch: {YamlText.FlowScalar(p.Branch)}" : "")) + "}";

    private (IReadOnlyList<HostedProject> Projects, IReadOnlyList<string> Problems) Parse()
    {
        var projects = new List<HostedProject>();
        var problems = new List<string>();
        foreach (var entry in _file.Entries())
        {
            string? Text(string name) => entry.Get(name) is YamlScalar { Value.Length: > 0 } s ? s.Value : null;
            var id = Text("id");
            var (path, git) = (Text("path"), Text("git"));
            if (id is null || !IsValidId(id)) problems.Add($"{Path}:{entry.Line}: project skipped: no valid id");
            else if ((path is null) == (git is null)) problems.Add($"{Path}:{entry.Line}: project '{id}' skipped: it needs a path or a git address, and only one");
            else if (projects.Any(p => p.Id == id)) problems.Add($"{Path}:{entry.Line}: project '{id}' is listed twice; the first entry stands");
            else projects.Add(new HostedProject(id, path, git, Text("branch")));
        }
        return (projects, problems);
    }
}
