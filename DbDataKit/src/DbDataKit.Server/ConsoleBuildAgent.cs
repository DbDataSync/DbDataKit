using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Cli;
using DbDataKit.State;

namespace DbDataKit.Server;

/// <summary>A person as a server sees them through the console's relay: the account, the name and the role as they stand now.</summary>
public sealed record RelayPerson(string Id, string? Name, UserRole Role);

/// <summary>Where an agent's Build commands run: a Build server the console manages, one of its projects and, for anything that changes files, a workspace (a branch of the project that is the person's own).</summary>
public sealed record BuildTarget(string Server, string Project, string? Workspace);

/// <summary>
/// The console's calls to a Build server as a named person (plan 27a AG14): the same signed token a relayed call carries, so the server decides what the person may do from the roles it holds for them. The agent has no
/// credential of its own; what it can do is what its person can, and no more.
/// </summary>
public interface IBuildRelay
{
    Task<(int Status, JsonNode? Body)> CallAsync(string server, RelayPerson person, HttpMethod method, string path, object? body, CancellationToken cancellation);
}

/// <summary>One command a Build server offers, as its commands list says (<c>GET /api/v1/projects/{project}/commands</c>).</summary>
public sealed record BuildCommand(string Tool, string Name, string Purpose, string Impact, string? WriteFlag, bool Allowed, JsonObject InputSchema)
{
    /// <summary>
    /// What an agent may be offered: commands that read, and commands that write the project's own files (in a workspace). A command that touches a target database or its tracking tables is never an agent's, whatever role the person
    /// holds: applying a plan is a person's act in Build, and the write login stays on the server. A command that sends a project to another server is outward and kept out too.
    /// </summary>
    public bool OfferedToAgents => Impact is "None" or "RepoFiles" && Name is not ("project publish" or "project publish-model" or "project publish-view") && !Name.StartsWith("ui ", StringComparison.Ordinal);

    /// <summary>The traits the command's impact gives it: reading definitions, and for a command that writes files, writing definitions (the strictest reading: a command with a write flag is held to it).</summary>
    public Traits Traits => Impact == "RepoFiles" ? Traits.ReadsDefinitions | Traits.WritesDefinitions : Traits.ReadsDefinitions;

    public bool Writes => Traits.HasFlag(Traits.WritesDefinitions);

    public static IReadOnlyList<BuildCommand> Parse(JsonNode? body) =>
        [.. (body?["commands"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new BuildCommand(
            (string?)c["tool"] ?? "", (string?)c["name"] ?? "", (string?)c["purpose"] ?? "", (string?)c["impact"] ?? "", (string?)c["write_flag"], (bool?)c["allowed"] ?? false,
            c["input_schema"] as JsonObject ?? new JsonObject { ["type"] = "object" })).Where(c => c.Tool.Length > 0)];
}

/// <summary>Build's commands as tools of an agent, run on a Build server as the signed-in person (plan 27a AG14).</summary>
public static class BuildAgentTools
{
    public static string CommandsPath(string project) => $"/api/v1/projects/{Uri.EscapeDataString(project)}/commands";

    public static async Task<(IReadOnlyList<BuildCommand> Commands, string? Problem)> ListAsync(IBuildRelay relay, BuildTarget target, RelayPerson person, CancellationToken cancellation)
    {
        var (status, body) = await relay.CallAsync(target.Server, person, HttpMethod.Get, CommandsPath(target.Project), null, cancellation).ConfigureAwait(false);
        return status == 200 ? (BuildCommand.Parse(body), null) : ([], Problem(status, body, $"The Build server '{target.Server}' did not list its commands"));
    }

    private static string Problem(int status, JsonNode? body, string what) => $"{what} ({status}{((string?)body?["error"] is { } e ? ": " + e : "")}).";

    /// <summary>
    /// The tools for an agent that named <paramref name="wanted"/> (tool names) from the live list. A command the person may not run, a write with no workspace and anything an agent is never offered are left out. The
    /// <paramref name="person"/> is asked for on every call, so an account disabled or changed during a conversation is what the next call carries (null refuses it).
    /// </summary>
    public static IReadOnlyList<AgentTool> Tools(IBuildRelay relay, BuildTarget target, IReadOnlyList<BuildCommand> commands, ISet<string> wanted, Func<RelayPerson?> person)
    {
        var tools = new List<AgentTool>();
        foreach (var command in commands.Where(c => wanted.Contains(c.Tool) && c.OfferedToAgents && c.Allowed && (!c.Writes || target.Workspace is not null)))
            tools.Add(Tool(relay, target, command, person));
        return tools;
    }

    /// <summary>Tools that only describe, for the screens that say what an agent would be offered before any conversation exists (they cannot run).</summary>
    public static IReadOnlyList<AgentTool> Described(IEnumerable<BuildCommand> commands) =>
        [.. commands.Where(c => c.OfferedToAgents).Select(c => new AgentTool(c.Tool, Describe(c), c.Traits, c.InputSchema, $"build command {c.Name}", (_, _) => Task.FromResult(new ToolOutcome("This tool runs in a conversation.", true))))];

    private static string Describe(BuildCommand c) => $"{c.Purpose}. Effect: {c.Impact switch { "RepoFiles" => "changes the project's files in your workspace", _ => "reads" }}.";

    private static AgentTool Tool(IBuildRelay relay, BuildTarget target, BuildCommand command, Func<RelayPerson?> person) =>
        new(command.Tool, Describe(command), command.Traits, command.InputSchema, $"build command {command.Name} on {target.Server}/{target.Project}{(target.Workspace is null ? "" : "/" + target.Workspace)}", async (arguments, cancellation) =>
        {
            if (person() is not { } who) return new ToolOutcome("The person this conversation is for can no longer sign in, so nothing was run.", true);
            var path = $"{CommandsPath(target.Project)}/{Uri.EscapeDataString(command.Tool)}" + (target.Workspace is { } w ? $"?workspace={Uri.EscapeDataString(w)}" : "");
            var (status, body) = await relay.CallAsync(target.Server, who, HttpMethod.Post, path, arguments, cancellation).ConfigureAwait(false);
            if (status is not (>= 200 and < 300))
                return new ToolOutcome(Problem(status, body, $"`{command.Name}` was refused or failed on the Build server"), true);
            var text = body?["document"] is { } document ? document.ToJsonString() : (string?)body?["text"] ?? "";
            return new ToolOutcome(text.Length > 120_000 ? text[..120_000] + "\n... cut: ask for less." : text, (int?)body?["exit"] >= 2);
        });

    // ---- the build-helper agent's files ----

    public const string HelperName = "build-helper";

    public const string SkillName = "dbdatabuild";

    /// <summary>The files of the first agent, <c>build-helper</c>: its definition (every command the server offers an agent, with the prompting its traits give) and its instructions. The skill is Build's own, from the server.</summary>
    public static IReadOnlyDictionary<string, string> HelperFiles(IEnumerable<BuildCommand> commands, string modelSlot = "default")
    {
        var yaml = new StringBuilder();
        yaml.Append("agent: 1\nname: build-helper\ntitle: Build helper\ndescription: Helps write and check dbdatabuild models in a workspace; the person decides what is kept and what reaches a database.\naudience: back-end\n");
        yaml.Append($"model: {{ slot: {modelSlot} }}\ninstructions: [instructions/role.md]\nskills: [{SkillName}]\ntools:\n");
        foreach (var c in commands.Where(c => c.OfferedToAgents).OrderBy(c => c.Tool, StringComparer.Ordinal)) yaml.Append($"  - command: {c.Name}\n");
        return new Dictionary<string, string>
        {
            ["agent.yml"] = yaml.ToString(),
            ["instructions/role.md"] = """
                You are the build helper. You work in one dbdatabuild project, in a workspace that is the person's own branch of it, as that person: you can do what they can and no more.

                Load the skill `dbdatabuild` first and follow it. Read before you write: look at the project, compile it, read what the commands report, then change model files with the commands that change files. Say what you changed and why.

                You never apply a plan, refresh data, publish or touch a database: those are the person's own acts in Build and are not offered to you. When the work needs one, say which command and why, and stop.
                A step that changes a file is shown to the person, who approves or denies it; a denied step is not tried again unless they ask.
                """,
        };
    }
}
