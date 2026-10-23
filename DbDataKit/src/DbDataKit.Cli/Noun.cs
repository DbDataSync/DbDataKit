namespace DbDataKit.Cli;

/// <summary>One verb of a noun: <c>server run</c> is the verb <c>run</c> of the noun <c>server</c>.</summary>
/// <param name="Name">The verb as typed (<c>run</c>).</param>
/// <param name="Summary">One line for the help.</param>
/// <param name="Effect">What the verb may touch; the management API offers it by this.</param>
/// <param name="Usage">The words after the verb in the usage line (<c>[--url &lt;url&gt;]</c>), empty for none.</param>
/// <param name="Run">Runs the verb with the arguments after it and returns the exit code.</param>
/// <param name="DataSchema">The JSON Schema of the document's <c>data</c> for this verb (see <see cref="DataSchemas"/>). Every verb of a router must have one: a verb without one is refused when the router is made.</param>
public sealed record Verb(string Name, string Summary, EffectClass Effect, string Usage, Func<string[], CliContext, Task<int>> Run, string? DataSchema = null);

/// <summary>
/// A group of verbs on one thing, identical in every command line that has it (plan 13, W4). The shared nouns are built here once; a product supplies what is its own
/// (how its server starts, how its configuration is read) as small interfaces and adds its own verbs with <see cref="Noun.With"/>.
/// </summary>
public sealed class Noun(string name, string summary, IReadOnlyList<Verb> verbs)
{
    public string Name { get; } = name;

    public string Summary { get; } = summary;

    public IReadOnlyList<Verb> Verbs { get; } = verbs;

    /// <summary>The same noun with more verbs, for the ones a product adds (<c>cert</c> plus its own, <c>server</c> plus <c>project add</c>).</summary>
    public Noun With(params Verb[] more) => new(Name, Summary, [.. Verbs, .. more]);

    /// <summary>The verbs as command specs (<c>server run</c>), for a product's command table and the help.</summary>
    public IEnumerable<CommandSpec> Specs() => Verbs.Select(v => new CommandSpec($"{Name} {v.Name}", v.Effect, v.Summary, true));
}

/// <summary>
/// Dispatches <c>&lt;noun&gt; &lt;verb&gt; ...</c> and prints the help, the same way in every command line. With <c>--format json</c> the one document the command writes to standard output is
/// <c>{ schema, command, tool_version, exit_code, ok, data, messages, errors }</c> (plan 13, C05): <c>data</c> is what the verb put in <see cref="CliContext.Data"/> and has the
/// verb's <see cref="Verb.DataSchema"/>; <c>messages</c> and <c>errors</c> are the lines the text form wrote to standard output and standard error; <c>exit_code</c> is the one text mode exits with.
/// <see cref="OutputSchema"/> is the JSON Schema of that document for every verb.
/// </summary>
public sealed class NounRouter
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _program;
    private readonly string _version;
    private readonly Noun[] _nouns;

    public NounRouter(string program, IEnumerable<Noun> nouns, string version = "0")
    {
        _program = program;
        _version = version;
        _nouns = [.. nouns];
        foreach (var noun in _nouns)
            foreach (var verb in noun.Verbs)
            {
                if (verb.DataSchema is null)
                    throw new ArgumentException($"'{noun.Name} {verb.Name}' has no data schema; every verb says what its JSON document holds (DataSchemas.None when it holds nothing).");
                try { System.Text.Json.Nodes.JsonNode.Parse(verb.DataSchema); }
                catch (System.Text.Json.JsonException ex) { throw new ArgumentException($"The data schema of '{noun.Name} {verb.Name}' is not JSON: {ex.Message}"); }
            }
    }

    public IReadOnlyList<Noun> Nouns => _nouns;

    /// <summary>Every verb of every noun as command specs.</summary>
    public IEnumerable<CommandSpec> Specs() => _nouns.SelectMany(n => n.Specs());

    public async Task<int> RunAsync(string[] args, CliContext context)
    {
        if (!TryTakeFormat(args, out var rest, out var json, out var formatError))
        {
            await context.Error.WriteLineAsync(formatError);
            return CliExit.Usage;
        }
        if (!json) return await DispatchAsync(rest, context);

        using var output = new StringWriter();
        using var error = new StringWriter();
        var buffered = context with { Out = output, Error = error };
        var exit = await DispatchAsync(rest, buffered);
        var command = rest.Length >= 2 && _nouns.FirstOrDefault(n => n.Name == rest[0]) is { } noun && noun.Verbs.Any(v => v.Name == rest[1]) ? $"{rest[0]} {rest[1]}" : "";
        var document = new Dictionary<string, object?>
        {
            ["schema"] = $"{_program}.output/1",
            ["command"] = command,
            ["tool_version"] = _version,
            ["exit_code"] = exit,
            ["ok"] = exit == 0,
            ["data"] = exit == 0 || buffered.Data.Count > 0 ? buffered.Data : new Dictionary<string, object?>(),
            ["messages"] = Lines(output.ToString()),
            ["errors"] = Lines(error.ToString()),
        };
        await context.Out.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(document, Json));
        return exit;
    }

    private static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    // --format json (or --format=json) anywhere after the noun; any other value is refused
    private static bool TryTakeFormat(string[] args, out string[] rest, out bool json, out string error)
    {
        var kept = new List<string>();
        json = false;
        error = "";
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i] == "--format") { if (i + 1 >= args.Length) { rest = args; error = "--format needs a value: json."; return false; } value = args[++i]; }
            else if (args[i].StartsWith("--format=", StringComparison.Ordinal)) value = args[i]["--format=".Length..];
            else { kept.Add(args[i]); continue; }
            if (value != "json") { rest = args; error = "The only format other than text is json (--format json)."; return false; }
            json = true;
        }
        rest = [.. kept];
        return true;
    }

    private async Task<int> DispatchAsync(string[] args, CliContext context)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await context.Out.WriteAsync(Help());
            return args.Length == 0 ? CliExit.Usage : CliExit.Ok;
        }

        var noun = _nouns.FirstOrDefault(n => n.Name == args[0]);
        if (noun is null)
        {
            await context.Error.WriteLineAsync($"{_program}: '{args[0]}' is not a command. Run '{_program} help' for the list.");
            return CliExit.Usage;
        }

        if (args.Length == 1 || args[1] is "help" or "--help" or "-h")
        {
            await context.Out.WriteAsync(NounHelp(noun));
            return args.Length == 1 ? CliExit.Usage : CliExit.Ok;
        }

        var verb = noun.Verbs.FirstOrDefault(v => v.Name == args[1]);
        if (verb is null)
        {
            await context.Error.WriteLineAsync($"{_program} {noun.Name}: '{args[1]}' is not a verb. Run '{_program} {noun.Name} help' for the list.");
            return CliExit.Usage;
        }

        return await verb.Run(args[2..], context);
    }

    /// <summary>The list of nouns, one line each. Deterministic, so a snapshot test can pin it.</summary>
    public string Help()
    {
        var width = _nouns.Max(n => n.Name.Length);
        var lines = new List<string> { $"Usage: {_program} <noun> <verb> [options]", "", "Commands:" };
        lines.AddRange(_nouns.Select(n => $"  {n.Name.PadRight(width)}  {n.Summary}"));
        lines.AddRange(["", $"Run '{_program} <noun> help' for the verbs of one.", "Add --format json to any command for a JSON document instead of text."]);
        return string.Join('\n', lines) + "\n";
    }

    public string NounHelp(Noun noun)
    {
        var lines = new List<string> { $"{_program} {noun.Name}: {noun.Summary}", "", "Verbs:" };
        foreach (var verb in noun.Verbs)
        {
            lines.Add($"  {noun.Name} {verb.Name}{(verb.Usage.Length > 0 ? " " + verb.Usage : "")}");
            lines.Add($"      {verb.Summary} [{verb.Effect.Describe()}]");
        }
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The JSON Schema of the document every command writes with <c>--format json</c>: the envelope, and for each verb the schema of its <c>data</c>. Deterministic, so it can be checked in and compared.</summary>
    public string OutputSchema()
    {
        var allOf = new System.Text.Json.Nodes.JsonArray();
        var defs = new System.Text.Json.Nodes.JsonObject();
        foreach (var spec in Specs().OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var verb = _nouns.First(n => n.Name == spec.Path[0]).Verbs.First(v => v.Name == spec.Path[1]);
            var key = "data_" + spec.Id;
            var full = System.Text.Json.Nodes.JsonNode.Parse(verb.DataSchema!)!.AsObject();
            defs[key] = full;
            // a command that did not get far enough to produce its data (it failed or was used wrongly) has some of it or none: the same shape without the required properties
            var failed = key;
            if (full.ContainsKey("required"))
            {
                failed = key + "_partial";
                var partial = System.Text.Json.Nodes.JsonNode.Parse(verb.DataSchema!)!.AsObject();
                partial.Remove("required");
                defs[failed] = partial;
            }
            allOf.Add(Rule(spec.Name, ok: true, key));
            allOf.Add(Rule(spec.Name, ok: false, failed));
        }

        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"urn:{_program}:schemas:output",
            ["title"] = $"{_program} command output (--format json)",
            ["description"] = "The one JSON document a command writes to standard output with `--format json`. `data` holds what the command found or did, and its shape is checked per command below ($defs.data_<command>); `messages` and `errors` are the lines the text form writes to standard output and standard error. Exit codes are the same as in text mode: 0 done, 1 the answer is no or it failed, 2 used wrongly.",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new System.Text.Json.Nodes.JsonArray("schema", "command", "tool_version", "exit_code", "ok", "data", "messages", "errors"),
            ["properties"] = new System.Text.Json.Nodes.JsonObject
            {
                ["schema"] = new System.Text.Json.Nodes.JsonObject { ["const"] = $"{_program}.output/1" },
                ["command"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                ["tool_version"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" },
                ["exit_code"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "integer" },
                ["ok"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean" },
                ["data"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
                ["messages"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "array", ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } },
                ["errors"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "array", ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } },
            },
            ["allOf"] = allOf,
            ["$defs"] = defs,
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static System.Text.Json.Nodes.JsonObject Rule(string command, bool ok, string definition) => new()
    {
        ["if"] = new System.Text.Json.Nodes.JsonObject
        {
            ["properties"] = new System.Text.Json.Nodes.JsonObject
            {
                ["command"] = new System.Text.Json.Nodes.JsonObject { ["const"] = command },
                ["ok"] = new System.Text.Json.Nodes.JsonObject { ["const"] = ok },
            },
            ["required"] = new System.Text.Json.Nodes.JsonArray("command", "ok"),
        },
        ["then"] = new System.Text.Json.Nodes.JsonObject
        {
            ["properties"] = new System.Text.Json.Nodes.JsonObject { ["data"] = new System.Text.Json.Nodes.JsonObject { ["$ref"] = "#/$defs/" + definition } },
        },
    };
}
