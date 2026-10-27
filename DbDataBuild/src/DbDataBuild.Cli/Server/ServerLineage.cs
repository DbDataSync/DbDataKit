using DbDataKit.Lineage;
using DbDataKit.Lineage.Cli;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// <c>dbdatabuild lineage graph|impact|provenance [--project &lt;id&gt;] [--repo &lt;server directory&gt;]</c> (plan 18, G05) on the server's command line: the design lineage of every project the server hosts, as one graph. Lineage identity names a table
/// from its connection's address, so the same table in two projects is one node and a flow from one project's models into another's shows. <c>--project</c> narrows it to one hosted project. A server with no project has an empty graph.
/// A connection's address is read from the logins the server stores (as its commands get them), else from the process's environment; a connection without one is named by itself (see <see cref="BuildLineage.IdentityOf"/>).
/// Run lineage is not in this graph: the graph carries design only, and the last runs (<see cref="BuildLineage.Runs"/>) need the tracking database.
/// </summary>
internal static class ServerLineage
{
    public static Noun Create(string[] invocation)
    {
        var directory = ServerCli.DirectoryOf(invocation);
        var problems = new List<string>();
        string? only = null;

        async Task<LineageGraph> Source(CancellationToken token)
        {
            var projects = new ProjectsFile(directory);
            var chosen = projects.All().Where(p => only is null || p.Id == only).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            var environment = await EnvironmentAsync(directory, token);
            var graphs = new List<LineageGraph>();
            foreach (var project in chosen)
            {
                var root = projects.RootOf(project);
                if (!File.Exists(Path.Combine(root, "dbdatabuild.yml"))) { problems.Add($"Project '{project.Id}' is left out: it has no files at {root} (a remote project that was never cloned?)."); continue; }
                try { var context = ProjectContext.Load(root); graphs.Add(BuildLineage.Design(context, BuildLineage.IdentityOf(context.Config, environment))); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // one broken project does not take the others' lineage with it; the type says what went wrong and not the message, which can quote a value
                    problems.Add($"Project '{project.Id}' is left out: its lineage could not be built ({ex.GetType().Name}). `dbdatabuild project compile --project {root}` shows what is wrong with it.");
                }
            }
            return graphs.Count == 0 ? LineageGraph.Empty : LineageGraph.Merge(graphs);
        }

        var noun = LineageNoun.Create(Source);
        return new Noun(noun.Name, noun.Summary, noun.Verbs.Select(v => v with
        {
            Usage = v.Usage + " [--project <id>] [--repo <server directory>]",
            Run = async (args, context) =>
            {
                var (own, project) = Without(args);
                if (project is not null && new ProjectsFile(directory).Find(project) is null)
                {
                    var hosted = new ProjectsFile(directory).All().Select(p => p.Id).ToList();
                    await context.Error.WriteLineAsync($"No project '{project}' is hosted. " + (hosted.Count == 0 ? "No project is hosted; add one with 'server project add'." : $"The hosted projects are: {string.Join(", ", hosted)}."));
                    return CliExit.Failure;
                }
                (only, problems) = (project, []);
                int exit;
                try { exit = await v.Run(own, context); }
                catch (LineageGraphException ex)
                {
                    await context.Error.WriteLineAsync($"The projects' lineage cannot be joined: {ex.Message}");
                    return CliExit.Failure;
                }
                foreach (var problem in problems) await context.Error.WriteLineAsync(problem);
                return exit;
            },
        }).ToList());
    }

    /// <summary>The arguments the lineage verbs know: <c>--repo</c> and <c>--project</c> with their values taken out, and the project named.</summary>
    private static (string[] Args, string? Project) Without(string[] args)
    {
        var kept = new List<string>();
        string? project = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--repo" && i + 1 < args.Length) i++;
            else if (args[i] == "--project" && i + 1 < args.Length) project = args[++i];
            else kept.Add(args[i]);
        }
        return ([.. kept], project);
    }

    /// <summary>The logins the server stores (when it has a secret store; one is never made for this) over the process's environment.</summary>
    private static async Task<Func<string, string?>> EnvironmentAsync(string directory, CancellationToken token)
    {
        IReadOnlyDictionary<string, string> logins = new Dictionary<string, string>();
        if (Directory.Exists(DbDataKit.Secrets.Store.SecretKeys.StoreDirectory(directory)))
        {
            try { logins = await ServerSecrets.LoginsAsync(ServerSecrets.Open(directory), token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* the store cannot be read: addresses fall back to the environment, then to the placeholder */ }
        }
        return name => logins.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);
    }
}
