using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli.Web;

/// <summary>
/// What a person's interface can ask of a project, whatever carries the request: the local web server (HTTP) or the MCP server (the tools only an MCP app may call). Run a command that does not change a
/// database, read a file of the project, save answers, apply a confirmed plan and follow the job. Every refusal is decided here, once.
/// </summary>
internal sealed class WebBackend
{
    /// <summary>The commands a person's interface may run. None changes a database (compile is held to --check, deploy loses --apply-plan, --yes and --ack); `deploy` and `compare` read a target with the read login, `deploy` writes plan files into the project.</summary>
    internal static readonly string[] ReadOnlyCommands = ["project compile", "project show graph", "project show metadata", "project tests run", "project show loads", "help matrix", "help code", "project show plan", "connection deploy", "project sample", "connection compare"];

    /// <summary>The parts of a project a person may read: what the project is made of, not the plans, the state or the environment.</summary>
    private static readonly string[] ReadableDirectories = ["models", "seeds", "tests", "rendered", "hooks"];
    private static readonly string[] ReadableExtensions = [".sql", ".yml", ".yaml", ".md", ".json", ".txt", ".csv"];
    private const int MaxFileBytes = 2 << 20;

    private readonly string projectRoot;
    private readonly ICommandHost host;
    private readonly ToolSurface surface;
    private readonly SemaphoreSlim oneCommandAtATime = new(1, 1);
    private readonly WebActions actions;

    public WebBackend(string projectRoot, ICommandHost host, bool allowApply)
    {
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.host = host;
        // plan writes plan files in the project and reads the target with the read login; the person answers each question themselves, so `plan --accept-inferred` is not offered
        surface = new ToolSurface(this.projectRoot, host.Commands.Where(c => ReadOnlyCommands.Contains(c.Name)), withholdWriteFlags: true, alsoWithheld: ["connection deploy --accept-inferred"], personReads: true);
        actions = new WebActions(this.projectRoot, host, oneCommandAtATime, allowApply);
    }

    public bool AllowApply => actions.AllowApply;

    public (int, string, string) Capabilities() => (200, "application/json; charset=utf-8", new JsonObject { ["apply"] = actions.AllowApply, ["answers_file"] = WebActions.AnswersFile }.ToJsonString());
    public (int, string, string) SaveAnswers(JsonObject? body) => actions.SaveAnswers(body);
    public (int, string, string) StartApply(JsonObject? body) => actions.StartApply(body);
    public (int, string, string) JobStatus(string? id, int from) => actions.JobStatus(id, from);
    public (int, string, string) StopJob(string? id) => actions.StopJob(id);

    private static (int, string, string) Refuse(int status, string message) => (status, "text/plain; charset=utf-8", message);

    public (int, string, string) RunCommand(JsonObject? body)
    {
        var name = body?["command"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (name == null || !surface.Tools.TryGetValue(name, out var command)) return Refuse(404, $"`{name}` is not a command this page can run. It can run: {string.Join(", ", surface.Tools.Keys)}.");
        if (!surface.TryBuildArguments(command, body!["arguments"] as JsonObject ?? new JsonObject(), out var argv, out var problem)) return Refuse(400, problem!);

        CommandResult result;
        oneCommandAtATime.Wait();       // commands share process state (the DuckDB connections, the console encoding): one at a time
        try { result = host.Run(argv); }
        finally { oneCommandAtATime.Release(); }
        JsonNode? document = null;
        try { document = JsonNode.Parse(result.Out); } catch (JsonException) { }
        var reply = new JsonObject { ["exit"] = result.Exit, ["document"] = document, ["text"] = document == null ? (result.Out.Length > 0 ? result.Out : result.Err) : null };
        return (200, "application/json; charset=utf-8", reply.ToJsonString());
    }

    /// <summary>A text file of the project: under models, sources, seeds, tests, rendered or hooks, or the configuration; never through a link that leaves the project.</summary>
    public (int, string, string) ReadFile(string? relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\0')) return Refuse(400, "A path relative to the project.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or "." or "")) return Refuse(400, "A path inside the project.");
        var allowed = relative == "dbdatabuild.yml" || ReadableDirectories.Contains(parts[0]) && parts.Length > 1;
        if (!allowed || !ReadableExtensions.Contains(Path.GetExtension(relative).ToLowerInvariant())) return Refuse(403, "Not a file this page shows.");
        var full = Path.GetFullPath(Path.Combine(projectRoot, relative));
        // no part of the path below the project root may be a link: a link could lead out of it
        var walk = projectRoot;
        foreach (var part in parts)
        {
            walk = Path.Combine(walk, part);
            if (File.Exists(walk) || Directory.Exists(walk))
                if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) return Refuse(403, "Not a file this page shows.");
        }
        var info = new FileInfo(full);
        if (!info.Exists) return Refuse(404, "No such file.");
        if (info.Length > MaxFileBytes) return Refuse(413, "File too large to show.");
        return (200, "text/plain; charset=utf-8", File.ReadAllText(full, Encoding.UTF8));
    }

}
