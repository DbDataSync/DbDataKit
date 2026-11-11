using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Cli;

/// <summary>What a product's <c>ui mcp</c> offers (plan 13, C08).</summary>
/// <param name="Excluded">Commands never offered, as <c>noun</c> or <c>noun verb</c>: the long-running and interactive ones, and anything that takes a secret or returns rows.</param>
/// <param name="AllowWrites">Also offer the commands that change files or the system. Without it only the ones that read are offered.</param>
public sealed record McpOptions(IReadOnlySet<string> Excluded, bool AllowWrites = false)
{
    /// <summary>The commands every product keeps out: the MCP server itself, and the ones that run for ever or ask questions on a terminal.</summary>
    public static readonly IReadOnlySet<string> Always = new HashSet<string>(StringComparer.Ordinal) { "ui", "server run", "server setup" };
}

/// <summary>
/// The Model Context Protocol over standard input and output (newline-delimited JSON-RPC 2.0) for any <see cref="NounRouter"/>: every offered command is a tool, a call runs it in process with
/// <c>--format json</c>, and the result is the command's document. The tools come from the router's verbs, so a command added to the router is offered (or kept out by its effect class)
/// without anything written here. A tool takes the words and options after the verb as a list (<c>args</c>); the verb's usage line says what they are.
/// </summary>
public sealed class McpServer(NounRouter router, McpOptions options, string serverName, string version)
{
    public const string LatestProtocol = "2025-06-18";
    private static readonly string[] SupportedProtocols = [LatestProtocol, "2025-03-26", "2024-11-05"];

    /// <summary>The longest document returned in a tool result; a larger one is cut.</summary>
    public const int MaxDocumentCharacters = 120_000;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The tools offered, in the order of the router, by name (<c>health_check</c>).</summary>
    public IReadOnlyList<(string Name, Noun Noun, Verb Verb)> Tools { get; } = Offered(router, options);

    private static List<(string, Noun, Verb)> Offered(NounRouter router, McpOptions options)
    {
        var tools = new List<(string, Noun, Verb)>();
        foreach (var noun in router.Nouns)
        {
            if (options.Excluded.Contains(noun.Name) || McpOptions.Always.Contains(noun.Name)) continue;
            foreach (var verb in noun.Verbs)
            {
                string command = $"{noun.Name} {verb.Name}";
                if (options.Excluded.Contains(command) || McpOptions.Always.Contains(command)) continue;
                if (!options.AllowWrites && verb.Effect is not (EffectClass.OfflineOnly or EffectClass.TargetReadOnly)) continue;
                tools.Add((ToolName(noun, verb), noun, verb));
            }
        }
        return tools;
    }

    public static string ToolName(Noun noun, Verb verb) => (noun.Name + "_" + verb.Name).Replace('-', '_');

    /// <summary>Reads requests until the input ends and answers each. A line that is not JSON gets a parse error; the loop never throws.</summary>
    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken cancellation = default)
    {
        while (await input.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException) { message = null; }
            if (message is null) { await output.WriteLineAsync(Error(null, -32700, "Parse error").ToJsonString(Compact)); await output.FlushAsync(cancellation); continue; }
            foreach (var reply in await HandleAsync(message, cancellation).ConfigureAwait(false))
                await output.WriteLineAsync(reply.ToJsonString(Compact));
            await output.FlushAsync(cancellation);
        }
    }

    /// <summary>The reply to one message (none for a notification). Exposed for tests.</summary>
    public async Task<IReadOnlyList<JsonNode>> HandleAsync(JsonNode message, CancellationToken cancellation = default)
    {
        if (message is JsonArray) return [Error(null, -32600, "Batches are not supported")];
        if (message is not JsonObject request || request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
            return [Error((message as JsonObject)?["id"]?.DeepClone(), -32600, "Invalid request")];
        var id = request["id"]?.DeepClone();
        if (id is null) return [];                                    // a notification (initialized, cancelled): nothing to answer
        try
        {
            JsonObject result = method switch
            {
                "initialize" => Initialize(request["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = new JsonArray([.. Tools.Select(t => Describe(t.Name, t.Noun, t.Verb))]) },
                "tools/call" => await CallAsync(request["params"] as JsonObject, cancellation).ConfigureAwait(false),
                _ => throw new McpException(-32601, $"Method not found: {method}"),
            };
            return [new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }];
        }
        catch (McpException ex) { return [Error(id, ex.Code, ex.Message)]; }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        string asked = parameters?["protocolVersion"]?.GetValue<string>() ?? LatestProtocol;
        return new JsonObject
        {
            ["protocolVersion"] = SupportedProtocols.Contains(asked) ? asked : LatestProtocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = serverName, ["version"] = version },
            ["instructions"] = $"Each tool runs one {serverName} command and returns its JSON document. Pass the words and options that follow the command, as in its usage line, in 'args'.",
        };
    }

    /// <summary>One tool: its name, what it does, its effect, the usage line, and the schema of its input.</summary>
    public static JsonObject Describe(string name, Noun noun, Verb verb) => new()
    {
        ["name"] = name,
        ["description"] = $"{verb.Summary}. [{verb.Effect.Describe()}] Usage: {noun.Name} {verb.Name}{(verb.Usage.Length > 0 ? " " + verb.Usage : "")}",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["args"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "The words and options after the command, one per element, as in the usage line.",
                },
            },
        },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = verb.Effect is EffectClass.OfflineOnly or EffectClass.TargetReadOnly },
    };

    private async Task<JsonObject> CallAsync(JsonObject? parameters, CancellationToken cancellation)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? throw new McpException(-32602, "A tool name is required.");
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool.Name is null) throw new McpException(-32602, $"Unknown tool: {name}");

        var args = new List<string> { tool.Noun.Name, tool.Verb.Name };
        if (parameters?["arguments"]?["args"] is JsonArray given)
            foreach (var item in given)
                args.Add(item is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new McpException(-32602, "'args' holds strings."));
        // the format is the server's to choose
        if (args.Skip(2).Any(a => a == "--format" || a.StartsWith("--format=", StringComparison.Ordinal)))
            throw new McpException(-32602, "--format is not an option here; the result is always the JSON document.");
        args.AddRange(["--format", "json"]);

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await router.RunAsync([.. args], new CliContext(output, error, TextReader.Null, cancellation)).ConfigureAwait(false);
        string text = output.ToString().Trim();
        if (text.Length == 0) text = error.ToString().Trim();
        if (text.Length > MaxDocumentCharacters) text = text[..MaxDocumentCharacters] + "\n(cut: the document is longer than a tool result may be)";
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = exit != 0,
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

/// <summary><c>ui mcp</c>: the noun every product with a router adds so an agent can use the same commands.</summary>
public static class UiNoun
{
    /// <param name="router">The product's router, which contains this noun; a function because the noun is made before the router.</param>
    /// <param name="serverName">The program name, reported to the client.</param>
    /// <param name="version">The program version, reported to the client.</param>
    /// <param name="excluded">Commands this product never offers (see <see cref="McpOptions.Excluded"/>).</param>
    public static Noun Create(Func<NounRouter> router, string serverName, string version, IEnumerable<string> excluded) => new("ui", "The local interfaces",
    [
        new Verb("mcp", "The Model Context Protocol on standard input and output: the commands that read, as tools", EffectClass.OfflineOnly, "[--allow-writes]",
            async (args, context) =>
            {
                var parsed = CliArgs.Parse(args, new HashSet<string> { "allow-writes" }, new HashSet<string>());
                if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
                {
                    await context.Error.WriteLineAsync("Usage: ui mcp [--allow-writes]");
                    return CliExit.Usage;
                }
                var server = new McpServer(router(), new McpOptions(new HashSet<string>(excluded, StringComparer.Ordinal), parsed.Has("allow-writes")), serverName, version);
                await server.ServeAsync(context.In, context.Out, context.Cancellation);
                return CliExit.Ok;
            },
            DataSchemas.None),
    ]);
}
