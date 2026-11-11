namespace DbDataKit.Cli;

/// <summary>How a step is approved (plan 27 section 3.3, plan 22a GP36): ask every time, ask once per session, or do not ask.</summary>
public enum Prompting
{
    Ask,
    AskOnce,
    DoNotAsk,
}

/// <summary>What the person is shown before a step that changes something runs: the command, what it was asked to do and what it may touch.</summary>
public sealed record ApprovalRequest(string Tool, string Command, IReadOnlyList<string> Args, Traits Traits, string Agent)
{
    /// <summary>One line the person reads: <c>workspace commit "message"  (writes definitions)</c>.</summary>
    public string Preview => $"{Command}{(Args.Count > 0 ? " " + string.Join(' ', Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)) : "")}  [{string.Join(", ", Traits.Names())}]";
}

/// <summary>The person's answer to one <see cref="ApprovalRequest"/>.</summary>
public sealed record ApprovalDecision(bool Approved, string? Note = null);

/// <summary>One line of the record of a session: who, through which agent, which step, and what came of it (<c>asked</c>, <c>approved</c>, <c>denied</c>, <c>ran</c>, <c>failed</c>, <c>refused</c>).</summary>
public sealed record McpAuditEntry(DateTimeOffset At, string Person, string Agent, string Tool, string Command, IReadOnlyList<string> Args, string Outcome, string? Note = null);

public interface IMcpAudit
{
    void Record(McpAuditEntry entry);
}

/// <summary>An audit that keeps its entries in memory (tests, and the default when none is given).</summary>
public sealed class McpAuditLog : IMcpAudit
{
    private readonly List<McpAuditEntry> _entries = [];

    public IReadOnlyList<McpAuditEntry> Entries { get { lock (_entries) return [.. _entries]; } }

    public void Record(McpAuditEntry entry) { lock (_entries) _entries.Add(entry); }
}

/// <summary>An audit that writes each entry as one JSON line to a writer (standard error for <c>ui mcp --ask</c>: standard output is the protocol's channel).</summary>
public sealed class TextWriterAudit(TextWriter writer) : IMcpAudit
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Record(McpAuditEntry entry)
    {
        lock (writer) { writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(entry, Json)); writer.Flush(); }
    }
}

/// <summary>
/// The approval gate of an MCP server (plan 22a GP36): with a gate, the commands that change something are offered like any other, and each call waits for the person first. A denied step ends the run: every later call is refused.
/// Without a way to ask (the client does not support elicitation and no <see cref="Approver"/> is given) a changing step is refused, never run. The gate never lets a text in a tool's result stand for an approval: the answer comes from the client's own channel.
/// </summary>
/// <param name="Person">Who is answering: the account the server runs as, by default.</param>
/// <param name="Approver">Asks the person. Null means the MCP client is asked (elicitation); tests give their own.</param>
/// <param name="Audit">Where each ask, answer, run and refusal is recorded.</param>
/// <param name="PromptingFor">The setting of a step by its traits; null means <see cref="Default"/>.</param>
public sealed record McpGate(string Person, Func<ApprovalRequest, CancellationToken, Task<ApprovalDecision>>? Approver = null, IMcpAudit? Audit = null, Func<Traits, Prompting>? PromptingFor = null)
{
    /// <summary>The defaults of plan 27 section 3.3, from traits, the strictest of those a command has: reads definitions and reads data do not ask; writes definitions or files ask once per conversation; writes data, changes structure, deletes, outward and elevated ask every time.</summary>
    public static Prompting Default(Traits traits) =>
        (traits & (Traits.WritesData | Traits.ChangesStructure | Traits.Deletes | Traits.Outward | Traits.Elevated)) != Traits.None ? Prompting.Ask
        : traits.HasFlag(Traits.WritesDefinitions) ? Prompting.AskOnce
        : Prompting.DoNotAsk;

    public Prompting For(Traits traits) => (PromptingFor ?? Default)(traits);

    public static McpGate ForCurrentUser(IMcpAudit? audit = null) => new(Environment.UserName, null, audit);
}
