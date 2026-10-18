using System.Text.Json.Nodes;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli.Mcp;

/// <summary>
/// The commands a client may run, as tools: which are offered, which options are withheld, how a call's inputs become a command line, and the JSON Schema of each. Shared by the MCP server and the web
/// interface so that both refuse the same things (a path outside the project, an option that shows values, a project other than the one served).
/// </summary>
internal sealed class ToolSurface
{
    /// <summary>Per command, the options a client cannot use: the ones that show values or read data the project's operator supplied.</summary>
    private static readonly Dictionary<string, string[]> WithheldOptions = new()
    {
        ["connection compare"] = ["--show-values"],
        ["project sample"] = ["--data"],
    };

    private readonly string projectRoot;
    private readonly bool withholdWriteFlags;
    private readonly IReadOnlySet<string> withholdFor;
    private readonly IReadOnlySet<string> alsoWithheld;
    private readonly bool personReads;

    /// <param name="withholdWriteFlags">Also withhold the flag that makes a command change something (`render --write`), so every offered call only reads.</param>
    /// <param name="withholdWriteFlagsFor">Commands whose write flags are withheld although the surface as a whole offers them (a read-only server offers `connection deploy` to plan, not to apply).</param>
    /// <param name="alsoWithheld">More options to withhold, as `command --option` (the web page withholds `plan --accept-inferred`: a person answers each question).</param>
    /// <param name="personReads">The client is a person's page, not a model: the options that show values are offered (the page asks for them explicitly; they are never on by default).</param>
    public ToolSurface(string projectRoot, IEnumerable<CommandInfo> offered, bool withholdWriteFlags = false, IEnumerable<string>? alsoWithheld = null, bool personReads = false, IEnumerable<string>? withholdWriteFlagsFor = null)
    {
        withholdFor = (withholdWriteFlagsFor ?? []).ToHashSet();
        this.personReads = personReads;
        this.alsoWithheld = (alsoWithheld ?? []).ToHashSet();
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.withholdWriteFlags = withholdWriteFlags;
        Tools = offered.ToDictionary(ToolName);
    }

    public IReadOnlyDictionary<string, CommandInfo> Tools { get; }

    /// <summary>The name a tool is offered and called by: the command's path as one token (`connection deploy` is `connection_deploy`).</summary>
    public static string ToolName(CommandInfo c) => c.Name.Replace(' ', '_').Replace('-', '_');

    private bool Withholds(CommandInfo c) => withholdWriteFlags || withholdFor.Contains(c.Name);

    private bool IsWriteFlag(CommandInfo c, OptionInfo o) => Withholds(c) && c.WriteFlag != null && c.WriteFlag.TrimStart('!').Split('|').Contains(o.Name);

    private IEnumerable<OptionInfo> Offered(CommandInfo c) =>
        c.Options.Where(o => o.Name != "--project" && !IsWriteFlag(c, o) && !alsoWithheld.Contains(c.Name + " " + o.Name) && !(!personReads && WithheldOptions.TryGetValue(c.Name, out var w) && w.Contains(o.Name)));

    internal static string PropertyName(string option) => option.TrimStart('-').Replace('-', '_');

    public JsonObject Describe(CommandInfo c)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var a in c.Arguments)
        {
            properties[a.Name] = a.Repeatable ? Array(a.Description) : new JsonObject { ["type"] = "string", ["description"] = a.Description };
            if (a.Choices.Count > 0) properties[a.Name]![a.Repeatable ? "items" : "enum"] = a.Repeatable ? new JsonObject { ["type"] = "string", ["enum"] = Strings(a.Choices) } : Strings(a.Choices);
            if (a.Required) required.Add(a.Name);
        }
        foreach (var o in Offered(c))
        {
            var schema = o.Kind switch
            {
                OptionKind.Flag => new JsonObject { ["type"] = "boolean" },
                OptionKind.Integer => new JsonObject { ["type"] = "integer" },
                OptionKind.List => Array(o.Description),
                _ => new JsonObject { ["type"] = "string" },
            };
            if (o.Kind != OptionKind.List) schema["description"] = o.Description + (o.Default != null && o.Kind != OptionKind.Flag ? $" (default: {o.Default})" : "");
            if (o.Kind == OptionKind.Path) schema["description"] = schema["description"] + " A path inside the project.";
            if (o.Choices.Count > 0) schema["enum"] = Strings(o.Choices);
            properties[PropertyName(o.Name)] = schema;
        }
        var readOnly = c.Impact is Impact.None && (c.WriteFlag == null || Withholds(c));
        return new JsonObject
        {
            ["name"] = ToolName(c),
            ["title"] = c.Name,
            ["description"] = $"[{(c.Lane.Length > 0 ? c.Lane + " · " : "")}{c.Disposition}] {c.Purpose}. Effect: {c.Effect}.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false },
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = c.Impact is Impact.Target or Impact.TargetData,
                ["idempotentHint"] = readOnly,
                ["openWorldHint"] = c.Effect.Contains("Target", StringComparison.Ordinal),
            },
        };
    }

    private static JsonObject Array(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    /// <summary>The command line for a call: the project root, positional values, then the options. A path outside the project, an option the tool does not offer, and a wrong type are refused.</summary>
    internal bool TryBuildArguments(CommandInfo command, JsonObject arguments, out List<string> argv, out string? problem)
    {
        argv = [.. command.Name.Split(' ')]; problem = null;
        var offered = Offered(command).ToDictionary(o => PropertyName(o.Name));
        var known = command.Arguments.Select(a => a.Name).Concat(offered.Keys).ToHashSet();
        foreach (var key in arguments.Select(k => k.Key))
            if (!known.Contains(key)) { problem = $"`{key}` is not an input of {command.Name}. Inputs: {string.Join(", ", known.Order())}."; return false; }

        foreach (var a in command.Arguments)
        {
            if (arguments[a.Name] is not { } v) { if (a.Required) { problem = $"`{a.Name}` is required."; return false; } continue; }
            foreach (var item in a.Repeatable ? (v as JsonArray)?.Select(x => x as JsonValue) ?? [] : [v as JsonValue])
            {
                if (item == null || !item.TryGetValue<string>(out var s)) { problem = $"`{a.Name}` must be {(a.Repeatable ? "a list of strings" : "a string")}."; return false; }
                if (!InsideProject(s, out problem, a.Name)) return false;
                argv.Add(s);
            }
        }
        foreach (var (name, o) in offered)
        {
            if (arguments[name] is not { } v) continue;
            switch (o.Kind)
            {
                case OptionKind.Flag:
                    if (v is not JsonValue f || !f.TryGetValue<bool>(out var on)) { problem = $"`{name}` must be true or false."; return false; }
                    if (on) argv.Add(o.Name);
                    break;
                case OptionKind.Integer:
                    if (v is not JsonValue n || !n.TryGetValue<int>(out var i)) { problem = $"`{name}` must be a whole number."; return false; }
                    argv.Add(o.Name); argv.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case OptionKind.List:
                    foreach (var item in (v as JsonArray)?.Select(x => x as JsonValue) ?? [])
                    {
                        if (item == null || !item.TryGetValue<string>(out var s)) { problem = $"`{name}` must be a list of strings."; return false; }
                        argv.Add(o.Name); argv.Add(s);
                    }
                    break;
                default:
                    if (v is not JsonValue t || !t.TryGetValue<string>(out var text)) { problem = $"`{name}` must be a string."; return false; }
                    if (o.Choices.Count > 0 && !o.Choices.Contains(text)) { problem = $"`{name}` must be one of {string.Join(", ", o.Choices)}."; return false; }
                    if (o.Kind == OptionKind.Path && !InsideProject(text, out problem, name)) return false;
                    argv.Add(o.Name); argv.Add(o.Kind == OptionKind.Path ? Path.GetFullPath(Path.Combine(projectRoot, text)) : text);
                    break;
            }
        }
        // a command that changes things unless a flag says not to (`project compile --check`) is held to the flag when write flags are withheld
        if (Withholds(command) && command.WriteFlag is { } held && held.StartsWith('!')) argv.Add(held[1..]);
        if (command.Options.Any(o => o.Name == "--project")) { argv.Add("--project"); argv.Add(projectRoot); }
        return true;
    }

    /// <summary>A value that names a file or a model must stay under the project root: no absolute path, no `..`.</summary>
    private bool InsideProject(string value, out string? problem, string input)
    {
        problem = null;
        if (value.Length == 0 || value.StartsWith('-')) { problem = $"`{input}`: `{value}` is not a name or a path."; return false; }
        if (Path.IsPathRooted(value) || value.Split('/', '\\').Contains("..")) { problem = $"`{input}`: `{value}` is outside the project. Paths are relative to the project root."; return false; }
        return true;
    }
}
