using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Cli;

/// <summary>What a product's <c>ui mcp</c> offers (plan 13, C08).</summary>
/// <param name="Excluded">Commands never offered, as <c>noun</c> or <c>noun verb</c>: the long-running and interactive ones, and anything that takes a secret or returns rows.</param>
/// <param name="AllowWrites">Also offer the commands that change files or the system. Without it only the ones that read are offered.</param>
/// <param name="Gate">Also offer the commands that change something, each call waiting for the person's approval (plan 22a GP36). Null: no approvals.</param>
/// <param name="RefuseData">Leave out the commands whose traits say they read data (an agent whose policy keeps data from its model, plan 27a AG02).</param>
public sealed record McpOptions(IReadOnlySet<string> Excluded, bool AllowWrites = false, McpGate? Gate = null, bool RefuseData = false)
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

    // the session: who is on the other end, what it can do, and what the person has been asked (plan 22a GP36)
    private TextReader? _input;
    private TextWriter? _output;
    private readonly Queue<string> _queued = new();
    private string _agent = "an unnamed agent";
    private bool _canElicit;
    private bool _ended;
    private int _asks;
    private readonly HashSet<string> _approvedOnce = new(StringComparer.Ordinal);

    /// <summary>The tools offered, in the order of the router, by name (<c>health_check</c>).</summary>
    public IReadOnlyList<(string Name, Noun Noun, Verb Verb)> Tools { get; } = Offered(router, options);

    /// <summary>The commands a server with these options offers, in the order of the router: the same rule the MCP server and the agent runtime both use.</summary>
    public static List<(string Name, Noun Noun, Verb Verb)> Offered(NounRouter router, McpOptions options)
    {
        var tools = new List<(string Name, Noun Noun, Verb Verb)>();
        foreach (var noun in router.Nouns)
        {
            if (options.Excluded.Contains(noun.Name) || McpOptions.Always.Contains(noun.Name)) continue;
            foreach (var verb in noun.Verbs)
            {
                string command = $"{noun.Name} {verb.Name}";
                if (options.Excluded.Contains(command) || McpOptions.Always.Contains(command)) continue;
                var traits = verb.EffectiveTraits;
                if (!options.AllowWrites && options.Gate is null && traits.Changes()) continue;
                if (options.RefuseData && traits.HasFlag(Traits.ReadsData)) continue;
                tools.Add((ToolName(noun, verb), noun, verb));
            }
        }
        return tools;
    }

    public static string ToolName(Noun noun, Verb verb) => (noun.Name + "_" + verb.Name).Replace('-', '_');

    /// <summary>Reads requests until the input ends and answers each. A line that is not JSON gets a parse error; the loop never throws.</summary>
    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken cancellation = default)
    {
        _input = input;
        _output = output;
        while ((_queued.Count > 0 ? _queued.Dequeue() : await input.ReadLineAsync(cancellation).ConfigureAwait(false)) is { } line)
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
        if (parameters?["clientInfo"]?["name"] is JsonValue client && client.TryGetValue<string>(out var clientName) && clientName.Length > 0)
            _agent = parameters["clientInfo"]!["version"] is JsonValue v && v.TryGetValue<string>(out var clientVersion) ? $"{clientName} {clientVersion}" : clientName;
        _canElicit = parameters?["capabilities"]?["elicitation"] is JsonObject;
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
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = !verb.EffectiveTraits.Changes(),
            ["traits"] = new JsonArray([.. verb.EffectiveTraits.Names().Select(n => (JsonNode)JsonValue.Create(n)!)]),
        },
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
        var givenArgs = args.Skip(2).ToList();

        var gate = options.Gate;
        string command = $"{tool.Noun.Name} {tool.Verb.Name}";
        void Audit(string outcome, string? note = null) =>
            gate?.Audit?.Record(new McpAuditEntry(DateTimeOffset.UtcNow, gate.Person, _agent, name, command, givenArgs, outcome, note));
        if (gate is not null)
        {
            var traits = tool.Verb.EffectiveTraits;
            if (_ended)
            {
                Audit("refused", "the run ended when a step was denied");
                return Refused("The run has ended: a step was not approved. Nothing more will run in this session; ask the person to start again.");
            }
            var prompting = gate.For(traits);
            if (prompting == Prompting.Ask || (prompting == Prompting.AskOnce && !_approvedOnce.Contains(name)))
            {
                var request = new ApprovalRequest(name, command, givenArgs, traits, _agent);
                Audit("asked", request.Preview);
                ApprovalDecision decision;
                try { decision = await (gate.Approver ?? (!_canElicit && gate.FallbackApprover is { } inbox ? inbox : ElicitAsync))(request, cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { decision = new ApprovalDecision(false, $"asking the person failed: {ex.Message}"); }
                if (!decision.Approved)
                {
                    _ended = true;
                    Audit("denied", decision.Note);
                    return Refused($"Not approved{(string.IsNullOrWhiteSpace(decision.Note) ? "" : ": " + decision.Note)}. The step did not run and the run has ended; nothing was changed.");
                }
                if (prompting == Prompting.AskOnce) _approvedOnce.Add(name);
                Audit("approved", decision.Note);
            }
        }

        var (text, isError) = await RunToolAsync(router, tool.Noun, tool.Verb, givenArgs, cancellation).ConfigureAwait(false);
        Audit(isError ? "failed" : "ran", isError ? "the command did not succeed" : null);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }

    /// <summary>
    /// Runs one command in process with <c>--format json</c> and returns its document (or, when it wrote none, what it wrote to standard error), cut at <see cref="MaxDocumentCharacters"/>. The MCP server and the agent runtime
    /// (<c>DbDataKit.Agents</c>) both call it, so a tool result is the same document wherever it is asked for.
    /// </summary>
    public static async Task<(string Text, bool IsError)> RunToolAsync(NounRouter router, Noun noun, Verb verb, IReadOnlyList<string> arguments, CancellationToken cancellation = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await router.RunAsync([noun.Name, verb.Name, .. arguments, "--format", "json"], new CliContext(output, error, TextReader.Null, cancellation)).ConfigureAwait(false);
        string text = output.ToString().Trim();
        if (text.Length == 0) text = error.ToString().Trim();
        if (text.Length > MaxDocumentCharacters) text = text[..MaxDocumentCharacters] + "\n(cut: the document is longer than a tool result may be)";
        return (text, exit != 0);
    }

    private static JsonObject Refused(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = true,
    };

    /// <summary>
    /// Asks the person through the client (MCP elicitation, protocol 2025-06-18): a request goes out on the same channel and the next reply with its id is the answer; whatever else arrives meanwhile waits its turn. A client that cannot be asked
    /// means no. Only the client's own reply counts: nothing a tool returned can stand for an approval.
    /// </summary>
    private async Task<ApprovalDecision> ElicitAsync(ApprovalRequest request, CancellationToken cancellation)
    {
        if (!_canElicit || _input is null || _output is null)
            return new ApprovalDecision(false, "this client cannot ask the person (it does not offer elicitation), so a step that changes something is not run");
        string id = $"approval-{++_asks}";
        var ask = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "elicitation/create",
            ["params"] = new JsonObject
            {
                ["message"] = $"{request.Agent} asks to run: {request.Preview}",
                ["requestedSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["approve"] = new JsonObject { ["type"] = "boolean", ["title"] = "Approve this step", ["description"] = request.Preview } },
                    ["required"] = new JsonArray("approve"),
                },
            },
        };
        await _output.WriteLineAsync(ask.ToJsonString(Compact)).ConfigureAwait(false);
        await _output.FlushAsync(cancellation).ConfigureAwait(false);
        while (await _input.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? reply;
            try { reply = JsonNode.Parse(line); }
            catch (JsonException) { reply = null; }
            if (reply is JsonObject o && o["method"] is null && o["id"] is JsonValue v && v.TryGetValue<string>(out var replyId) && replyId == id)
            {
                var action = o["result"]?["action"] is JsonValue a && a.TryGetValue<string>(out var s) ? s : null;
                bool approve = action == "accept" && (o["result"]?["content"]?["approve"] is not JsonValue b || !b.TryGetValue<bool>(out var yes) || yes);
                return approve ? new ApprovalDecision(true) : new ApprovalDecision(false, action is "decline" or "cancel" ? $"the person chose {action}" : "the person did not approve");
            }
            _queued.Enqueue(line);   // another message that arrived while the person decided
        }
        return new ApprovalDecision(false, "the client closed the connection while the step waited for approval");
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
        new Verb("mcp", "The Model Context Protocol on standard input and output: the commands that read, as tools (with --ask, the ones that change something too, each after the person approves it; with --report-to, the session is recorded in a console and a client that cannot ask the person asks in the console's inbox)", EffectClass.OfflineOnly, "[--allow-writes] [--ask] [--refuse-data] [--report-to <console address>]",
            async (args, context) =>
            {
                var parsed = CliArgs.Parse(args, new HashSet<string> { "allow-writes", "ask", "refuse-data" }, new HashSet<string> { "report-to" });
                if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
                {
                    await context.Error.WriteLineAsync("Usage: ui mcp [--allow-writes] [--ask] [--refuse-data] [--report-to <console address>]");
                    return CliExit.Usage;
                }
                McpGate? gate = parsed.Has("ask") ? McpGate.ForCurrentUser(new TextWriterAudit(context.Error)) : null;
                if (parsed.Get("report-to") is { } address)
                {
                    if (!Uri.TryCreate(address, UriKind.Absolute, out var console) || console.Scheme is not ("https" or "http"))
                    {
                        await context.Error.WriteLineAsync($"--report-to takes the console's address (https://...), not '{address}'.");
                        return CliExit.Usage;
                    }
                    string? user = Environment.GetEnvironmentVariable(ConsoleReporter.UserVariable), token = Environment.GetEnvironmentVariable(ConsoleReporter.TokenVariable);
                    if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(token))
                    {
                        await context.Error.WriteLineAsync($"--report-to signs in as a person with a personal access token: set {ConsoleReporter.UserVariable} (the account's name) and {ConsoleReporter.TokenVariable} (a token made at the identity home).");
                        return CliExit.Usage;
                    }
                    var reporter = ConsoleReporter.Create(console, user, token, context.Error);
                    gate = (gate ?? McpGate.ForCurrentUser()) with { Audit = new CompositeAudit(new TextWriterAudit(context.Error), reporter), FallbackApprover = reporter.AskInboxAsync };
                }
                var server = new McpServer(router(), new McpOptions(new HashSet<string>(excluded, StringComparer.Ordinal), parsed.Has("allow-writes"), gate, parsed.Has("refuse-data")), serverName, version);
                await server.ServeAsync(context.In, context.Out, context.Cancellation);
                return CliExit.Ok;
            },
            DataSchemas.None),
    ]);
}
