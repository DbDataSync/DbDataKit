using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project agent-kit` (DESIGN.md 9.7). Effect class: repo files only. The knowledge an AI coding agent needs to work in a project as someone who knows the tool: a skill
/// (SKILL.md: the rules, the loop, how to write models, how to read plans) and the JSON Schemas of every file and of every command's output. The files are embedded in the
/// executable, so the kit always matches the version of the tool that writes it. By default nothing is written: the files are listed. `--write` installs them (default
/// `.claude/skills/dbdatabuild/` in the project), `--check` fails if an installed copy differs from this version's.
/// </summary>
internal static class AgentKitCommand
{
    public const string DefaultDir = ".claude/skills/dbdatabuild";

    public static IReadOnlyList<(string Path, string Content)> Files()
    {
        var asm = typeof(AgentKitCommand).Assembly;
        var files = new List<(string, string)>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("agentkit/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            files.Add((name["agentkit/".Length..], r.ReadToEnd().Replace("\r\n", "\n")));
        }
        return files;
    }

    public static int Run(CommandSpec spec, string projectRoot, string? dir, bool write, bool check, bool mcp, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        if (write && check) { error.WriteLine("--write and --check cannot be combined: --check writes nothing."); return CliApp.ExitUsage; }
        var target = Path.GetFullPath(Path.Combine(projectRoot, dir ?? DefaultDir));
        var rel = Path.GetRelativePath(projectRoot, target).Replace('\\', '/');
        var files = Files();
        var listing = files.Select(f => new { path = $"{rel}/{f.Path}", hash = Hashing.ScriptHash(f.Content) }).ToList();
        output.Payload("directory", rel);
        output.Payload("files", listing);

        var wrote = new List<string>();
        var stale = new List<string>();
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(target, path);
            var current = File.Exists(full) ? File.ReadAllText(full).Replace("\r\n", "\n") : null;
            if (current == content) continue;
            stale.Add($"{rel}/{path}");
            if (!write) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(content));
            File.Move(temp, full, overwrite: true);
            wrote.Add($"{rel}/{path}");
        }
        output.Payload("wrote", wrote);
        output.Payload("out_of_date", check ? stale : []);

        // --mcp: the project's .mcp.json (Claude Code reads it) gets the dbdatabuild server added, and nothing else in it is touched
        if (mcp)
        {
            var (current, desired, problem) = McpConfig.Plan(projectRoot);
            if (problem != null) { error.Diag(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new(McpConfig.FileName, 0, 0), problem)); return CliApp.ExitFindings; }
            var upToDate = current == desired;
            var wroteMcp = false;
            if (write && !upToDate)
            {
                var full = Path.Combine(projectRoot, McpConfig.FileName);
                var temp = full + ".ddb-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(temp, new UTF8Encoding(false).GetBytes(desired));
                File.Move(temp, full, overwrite: true);
                wroteMcp = true;
            }
            output.Payload("mcp", new { file = McpConfig.FileName, up_to_date = upToDate, wrote = wroteMcp });
            if (wroteMcp) output.WriteLine($"wrote {McpConfig.FileName} (the dbdatabuild server; Claude Code asks before it first uses a project server)");
            else if (check && !upToDate) { error.Diag(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new(McpConfig.FileName, 0, 0), $"`{McpConfig.FileName}` is missing the dbdatabuild server or differs from this version's.")); stale.Add(McpConfig.FileName); }
            else if (!write && !check) output.WriteLine(upToDate ? $"{McpConfig.FileName}: the dbdatabuild server is already there." : $"{McpConfig.FileName}: --write would add the dbdatabuild server (`{ProductInfo.Cli} ui mcp --project .`).");
        }

        if (write)
        {
            foreach (var w in wrote) output.WriteLine($"wrote {w}");
            output.WriteLine(wrote.Count == 0 ? $"{files.Count} file(s) already up to date in {rel}." : $"{files.Count} file(s) in {rel}; {wrote.Count} written. Commit them with the project.");
            return CliApp.ExitOk;
        }
        if (check)
        {
            foreach (var s in stale) error.Diag(new Diagnostic(DiagnosticCatalog.RenderedFileOutOfDate, new(s, 0, 0), $"`{s}` is missing or differs from this version's agent kit."));
            output.WriteLine(stale.Count == 0 ? $"OK: the agent kit in {rel} matches this version." : $"FAILED: {stale.Count} file(s) out of date. Run `{ProductInfo.Cli} {spec.Name} --write`.");
            return stale.Count == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
        }
        foreach (var f in listing) output.WriteLine(f.path);
        output.WriteLine();
        output.WriteLine($"{files.Count} file(s). `{ProductInfo.Cli} {spec.Name} --write` installs them in {rel}; point your agent at {rel}/SKILL.md.");
        return CliApp.ExitOk;
    }
}

/// <summary>The entry `agent-kit --mcp` puts in a project's `.mcp.json`, and how it is merged with what is there.</summary>
internal static class McpConfig
{
    public const string FileName = ".mcp.json";
    public const string ServerName = "dbdatabuild";

    /// <summary>
    /// The server Claude Code starts from the project folder: `dbdatabuild ui mcp --project .`, read-only (no --allow-writes, no --allow-apply). Only the READ logins are passed on, by name: a value comes from the
    /// environment Claude Code runs in, never from this file, and the write login is never here (a person who wants applying from a host sets that up by hand: docs/interfaces.md).
    /// </summary>
    public static JsonObject Entry(IEnumerable<string>? connections = null) => new()
    {
        ["type"] = "stdio",
        ["command"] = ProductInfo.Cli,
        ["args"] = new JsonArray("ui", "mcp", "--project", "."),
        ["env"] = new JsonObject((connections ?? TargetNames.All).Select(t => new KeyValuePair<string, JsonNode?>(LoginName(t), "${" + LoginName(t) + ":-}"))),
    };

    private static string LoginName(string target) => $"DBDATABUILD_{target.ToUpperInvariant()}_READ";

    /// <summary>The text of the file now (null when there is none), the text it should have, and a reason when the file cannot be merged (it is not a JSON object, or `mcpServers` is not an object).</summary>
    public static (string? Current, string Desired, string? Problem) Plan(string projectRoot)
    {
        var path = Path.Combine(projectRoot, FileName);
        JsonObject root;
        string? current = null;
        if (File.Exists(path))
        {
            current = File.ReadAllText(path).Replace("\r\n", "\n");
            try { root = JsonNode.Parse(current) as JsonObject ?? throw new JsonException("not an object"); }
            catch (JsonException) { return (current, "", $"`{FileName}` is not a JSON object, so the server is not added to it. Fix or remove the file, or add the server by hand (docs/interfaces.md)."); }
        }
        else root = new JsonObject();
        if (root["mcpServers"] is not null and not JsonObject) return (current, "", $"`mcpServers` in `{FileName}` is not an object, so the server is not added to it.");
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        servers[ServerName] = Entry(CommandTargets.NamesOf(projectRoot));
        root["mcpServers"] = servers;
        return (current, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", null);       // LF on every platform (the default is the platform's line ending)
    }
}
