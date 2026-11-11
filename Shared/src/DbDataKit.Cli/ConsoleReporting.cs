using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Cli;

/// <summary>
/// An outside agent's session reported to the console (plan 27a, <c>ui mcp --report-to</c>; the console's half is <c>api/agents/outside</c>): what the gate asked, what the person answered and what ran is posted as the person whose
/// personal access token the server holds, so the console's record and approvals inbox show it. When the client cannot ask the person (no elicitation) the ask goes to the inbox and the server waits for the answer there; the person's
/// answer in the inbox is the only thing that counts, as with elicitation. The console being down never lets a change run: a step that has to be asked and cannot be is refused.
/// </summary>
public sealed class ConsoleReporter : IMcpAudit
{
    /// <summary>The account the token belongs to, and the token (never an argument: a command line is seen by others).</summary>
    public const string UserVariable = "DBDATAKIT_CONSOLE_USER";
    public const string TokenVariable = "DBDATAKIT_CONSOLE_TOKEN";

    private readonly HttpClient _http;
    private readonly TextWriter _log;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _wait;
    private McpAuditEntry? _pendingAsked;
    private bool _inboxAnswered;
    private bool _warned;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the console's address (use <see cref="Create"/>).</param>
    /// <param name="log">Where a problem reaching the console is said (standard error: standard output is the protocol's).</param>
    /// <param name="poll">How often the inbox is asked for the person's answer.</param>
    /// <param name="wait">How long a step waits in the inbox before it counts as not approved.</param>
    public ConsoleReporter(HttpClient http, TextWriter log, TimeSpan? poll = null, TimeSpan? wait = null)
    {
        _http = http;
        _log = log;
        _poll = poll ?? TimeSpan.FromSeconds(1);
        _wait = wait ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>A reporter for a console at <paramref name="address"/>, signing in with a personal access token as Basic credentials (the account's name and the token, made at the identity home).</summary>
    public static ConsoleReporter Create(Uri address, string user, string token, TextWriter log, HttpMessageHandler? handler = null, TimeSpan? poll = null)
    {
        var http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));
        return new ConsoleReporter(http, log, poll);
    }

    /// <summary>The kind the console's record uses for an outcome of the gate.</summary>
    public static string KindOf(string outcome) => outcome switch
    {
        "asked" => "approval-asked",
        "approved" => "approval-approved",
        "denied" => "approval-denied",
        "ran" => "tool-called",
        "failed" => "tool-failed",
        "refused" => "tool-refused",
        _ => outcome,
    };

    public void Record(McpAuditEntry entry)
    {
        // an ask is held back: when the inbox is asked, the console writes it (and the answer) itself; when the client's own elicitation answers, it is sent with the answer
        if (entry.Outcome == "asked") { _pendingAsked = entry; return; }
        var events = new List<object>();
        if (_pendingAsked is { } asked) { _pendingAsked = null; events.Add(Event(asked)); }
        if (_inboxAnswered && entry.Outcome is "approved" or "denied")
        {
            _inboxAnswered = false;
            if (events.Count == 0) return;
        }
        else events.Add(Event(entry));
        Post("outside/events", new { events });
    }

    private static object Event(McpAuditEntry e)
    {
        var (name, version) = SplitAgent(e.Agent);
        return new { kind = KindOf(e.Outcome), agent = name, agentVersion = version, tool = e.Tool, arguments = JsonSerializer.Serialize(e.Args), detail = e.Note ?? e.Command };
    }

    /// <summary>The client's name and version from the <c>name version</c> text the server keeps.</summary>
    public static (string Name, string Version) SplitAgent(string agent)
    {
        int space = agent.LastIndexOf(' ');
        return space > 0 && agent[(space + 1)..].Any(char.IsDigit) ? (agent[..space], agent[(space + 1)..]) : (agent, "");
    }

    /// <summary>The approver for a client that cannot ask: the step waits in the console's inbox for the person's own answer. A console that cannot be reached, or an answer that does not come in time, is a no.</summary>
    public async Task<ApprovalDecision> AskInboxAsync(ApprovalRequest request, CancellationToken cancellation)
    {
        var (name, version) = SplitAgent(request.Agent);
        Guid id;
        try
        {
            using var response = await _http.PostAsync("api/agents/outside/ask", Body(new
            {
                agent = name, agentVersion = version, tool = request.Tool, preview = request.Preview,
                arguments = JsonSerializer.Serialize(request.Args), traits = string.Join(", ", request.Traits.Names()),
            }), cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new ApprovalDecision(false, $"the console did not take the question ({(int)response.StatusCode}), so a step that changes something is not run");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            id = doc.RootElement.GetProperty("id").GetGuid();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            if (cancellation.IsCancellationRequested) throw;
            return new ApprovalDecision(false, $"the console could not be reached ({ex.Message}), so a step that changes something is not run");
        }
        _pendingAsked = null;
        _inboxAnswered = true;

        var until = DateTimeOffset.UtcNow + _wait;
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(_poll, cancellation).ConfigureAwait(false);
            try
            {
                using var response = await _http.GetAsync($"api/agents/outside/ask/{id}", cancellation).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
                var status = doc.RootElement.GetProperty("status").GetString();
                string? note = doc.RootElement.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (status == "approved") return new ApprovalDecision(true, note);
                if (status is "denied" or "expired") return new ApprovalDecision(false, status == "expired" ? "nobody answered in the console's inbox in time" : note ?? "the person denied it in the console's inbox");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                if (cancellation.IsCancellationRequested) throw;
            }
        }
        return new ApprovalDecision(false, "nobody answered in the console's inbox in time");
    }

    private static StringContent Body(object value) => new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    private void Post(string path, object value)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = _http.PostAsync("api/agents/" + path, Body(value), cts.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) Warn($"the console answered {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Warn(ex.Message); }
    }

    private void Warn(string reason)
    {
        if (_warned) return;
        _warned = true;
        lock (_log) { _log.WriteLine($"{{\"warning\":\"the console did not take the record of this session: {reason.Replace("\"", "'")}\"}}"); _log.Flush(); }
    }
}

/// <summary>An audit that hands each entry to several others (the lines on standard error and the console).</summary>
public sealed class CompositeAudit(params IMcpAudit[] audits) : IMcpAudit
{
    public void Record(McpAuditEntry entry) { foreach (var a in audits) a.Record(entry); }
}
