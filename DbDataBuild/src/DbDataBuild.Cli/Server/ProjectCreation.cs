using DbDataKit.Config.Git;

namespace DbDataBuild.Cli.Server;

/// <summary>Why a project could not be made, in a sentence a person can read; <see cref="Status"/> is the HTTP status the route answers with.</summary>
internal sealed class ProjectCreationException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Making a new project from one of the built-in templates on the server (plan 13, W07): the folder <c>projects/&lt;id&gt;</c> of the server's directory gets the template's files (the same code <c>project create</c> writes them with),
/// becomes a git repository with the template as its first commit in the author's name, and is hosted from then on as a project that is a folder on the server (workspaces are made from it and merged back into it inside the server, D15).
/// Nothing is overwritten: an id that is taken, or a folder that is already there, is refused.
/// </summary>
internal sealed class ProjectCreator(string serverDirectory, ProjectsFile projects)
{
    private readonly object gate = new();

    public HostedProject Create(string id, string templateName, GitAuthor author)
    {
        if (!ProjectsFile.IsValidId(id)) throw new ProjectCreationException($"'{id}' is not a project name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64.", 400);
        if (TemplateStore.Find(templateName) is not { } template) throw new ProjectCreationException($"There is no template '{templateName}'. Templates: {string.Join(", ", TemplateStore.All.Select(t => t.Name))}.", 404);
        lock (gate)
        {
            if (projects.Find(id) is not null) throw new ProjectCreationException($"This server already hosts a project called '{id}'.", 409);
            var folder = Path.Combine(serverDirectory, "projects", id);
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new ProjectCreationException($"The server already has a folder for '{id}' that is not empty, so nothing was written. Choose another name.", 409);
            try
            {
                NewCommand.Write(template, folder);
                // rendered files are hashed and compared as committed, so a merge on a machine that converts line endings must not touch them
                if (!File.Exists(Path.Combine(folder, ".gitattributes")))
                    File.WriteAllText(Path.Combine(folder, ".gitattributes"), "* text=auto eol=lf\nrendered/** text eol=lf\n");
                LibGit2Sharp.Repository.Init(folder);
                GitWorkspaceOperations.Commit(folder, $"New project from the {template.Name} template", author);
                var project = new HostedProject(id, folder, null, null);
                projects.Add(project);
                return project;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GitOperationException or LibGit2Sharp.LibGit2SharpException or ArgumentException)
            {
                // a half-made folder is removed so the name can be tried again
                try { if (Directory.Exists(folder)) { foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(folder, true); } }
                catch (IOException) { }
                throw new ProjectCreationException($"The project could not be made: {ex.Message}", 500);
            }
        }
    }
}
