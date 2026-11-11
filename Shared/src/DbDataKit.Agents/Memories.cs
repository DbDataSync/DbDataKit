using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Cli;

namespace DbDataKit.Agents;

public enum MemoryKind
{
    /// <summary>What the agent remembers of a person: only that person sees it, lists it, edits it and deletes it.</summary>
    Person,

    /// <summary>What the agent remembers of the work (a workspace or a work item): whoever opens the work sees it.</summary>
    Work,
}

/// <summary>One memory. <see cref="Person"/> is who it is about (a person memory) or who wrote it (a work memory); <see cref="WorkRef"/> names the work (a workspace or work item), null for a person memory.</summary>
public sealed record Memory(string Id, MemoryKind Kind, string Person, string? WorkRef, string? Agent, string Text, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Where memories are kept (the <c>agents</c> module in a host; <see cref="InMemoryMemoryStore"/> in a test). It does no checking: <see cref="MemoryService"/> decides who may see and change what.</summary>
public interface IMemoryStore
{
    IReadOnlyList<Memory> All(string? person = null, string? workRef = null);

    Memory? Find(string id);

    void Save(Memory memory);

    bool Remove(string id);
}

public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly Dictionary<string, Memory> _items = [];

    public IReadOnlyList<Memory> All(string? person = null, string? workRef = null)
    {
        lock (_items) return [.. _items.Values.Where(m => (person is null || m.Person == person) && (workRef is null || m.WorkRef == workRef)).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id, StringComparer.Ordinal)];
    }

    public Memory? Find(string id) { lock (_items) return _items.GetValueOrDefault(id); }

    public void Save(Memory memory) { lock (_items) _items[memory.Id] = memory; }

    public bool Remove(string id) { lock (_items) return _items.Remove(id); }
}

/// <summary>Which kinds of memory are on, for one agent: the agent's own setting narrowed by the install's (an administrator turns each kind off per agent or for the install; off means it is neither written nor read).</summary>
public sealed record MemorySwitches(bool Person, bool Work)
{
    public static readonly MemorySwitches Off = new(false, false);

    public bool Allows(MemoryKind kind) => kind == MemoryKind.Person ? Person : Work;

    public static MemorySwitches For(AgentMemory agent, AgentPolicy policy) => new(agent.Person && policy.PersonMemory, agent.Workspace && policy.WorkMemory);
}

public sealed record MemoryResult(bool Ok, string Message, Memory? Memory = null);

/// <summary>
/// Memories (plan 27a AG19, plan 27 section 3.11): the person's own and the work's, kept by the host, written and forgotten by the agent through two tools with their own prompting, read through the context assembly after the instructions and before references (inside the
/// room it is given, newest first, each part recorded in the turn), <b>never a secret</b> (a value that looks like one is refused in words), and never a way to share learning: that goes through suggestions to the knowledge (KN19), which a person approves.
/// </summary>
public sealed class MemoryService(IMemoryStore store, TimeProvider? time = null)
{
    public const int MaximumLength = 500;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // what a secret looks like: a key with a known prefix, a bearer or basic credential, a private key, a "password = ..." pair, a connection string with a password, a long run of base64 or hex
    private static readonly Regex[] Secrets =
    [
        new(@"\b(sk|pk|rk|ghp|gho|ghs|xox[abp]|AKIA|ASIA|AIza)[-_A-Za-z0-9]{12,}", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\b(password|passwd|pwd|secret|api[-_ ]?key|token|client[-_ ]secret)\s*[:=]\s*\S{4,}", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\b(password|passwd|pwd|passphrase)\s+(is|was|=)\s+\S{4,}", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\b(password|pwd)=[^;\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"\b[A-Za-z0-9+/_-]{40,}={0,2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.", RegexOptions.Compiled | RegexOptions.CultureInvariant),
    ];

    /// <summary>Whether a text may be remembered: a sentence in words the model can act on when it may not.</summary>
    public static string? Refusal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "There is nothing to remember: give the text.";
        if (text.Length > MaximumLength) return $"A memory is at most {MaximumLength} characters ({text.Length} given). Keep the one thing worth remembering.";
        if (Secrets.Any(r => r.IsMatch(text)))
            return "That looks like a secret (a key, a password, a token or a connection string) and a memory never holds one. Remember what it is for (for example which connection to use), never its value.";
        return null;
    }

    public MemoryResult Remember(string person, MemoryKind kind, string? workRef, string? agent, string text, MemorySwitches switches)
    {
        if (!switches.Allows(kind)) return new(false, $"{(kind == MemoryKind.Person ? "Person" : "Work")} memories are turned off for this agent; nothing was written.");
        if (kind == MemoryKind.Work && string.IsNullOrEmpty(workRef)) return new(false, "There is no workspace or work item in this conversation to remember something about.");
        if (Refusal(text) is { } refusal) return new(false, refusal);
        var now = _time.GetUtcNow();
        var memory = new Memory(Guid.NewGuid().ToString("N")[..12], kind, person, kind == MemoryKind.Work ? workRef : null, agent, text.Trim(), now, now);
        store.Save(memory);
        return new(true, $"Remembered ({kind.ToString().ToLowerInvariant()} memory {memory.Id}).", memory);
    }

    /// <summary>What a person may see: their own memories, and the work memories of the work they have open (the host passes only work the person can open).</summary>
    public IReadOnlyList<Memory> Visible(string person, string? workRef, MemorySwitches switches) =>
    [
        .. (switches.Person ? store.All(person).Where(m => m.Kind == MemoryKind.Person) : []),
        .. (switches.Work && workRef is not null ? store.All(workRef: workRef).Where(m => m.Kind == MemoryKind.Work) : []),
    ];

    /// <summary>A memory of the person's own, or of work the person has open. Another person's is not found, so nothing says it exists.</summary>
    private Memory? Own(string id, string person, string? workRef)
    {
        var m = store.Find(id);
        if (m is null) return null;
        if (m.Kind == MemoryKind.Person) return m.Person == person ? m : null;
        return workRef is not null && m.WorkRef == workRef ? m : null;
    }

    public MemoryResult Edit(string id, string person, string? workRef, string text)
    {
        if (Own(id, person, workRef) is not { } m) return new(false, $"There is no memory '{id}' of yours.");
        if (Refusal(text) is { } refusal) return new(false, refusal);
        var edited = m with { Text = text.Trim(), UpdatedAt = _time.GetUtcNow() };
        store.Save(edited);
        return new(true, "The memory was changed.", edited);
    }

    public MemoryResult Forget(string id, string person, string? workRef)
    {
        if (Own(id, person, workRef) is null) return new(false, $"There is no memory '{id}' of yours to forget.");
        store.Remove(id);
        return new(true, $"Forgotten: memory {id} is deleted and will not be given again.");
    }

    /// <summary>
    /// The memories as context parts, newest first, as many as fit in <paramref name="budgetTokens"/>; the context assembler gives them after the instructions and before references and records each (id and version) in the turn.
    /// </summary>
    public IReadOnlyList<ContextSource> Inputs(string person, string? workRef, MemorySwitches switches, int budgetTokens, ITokenCounter? counter = null)
    {
        counter ??= EstimateCounter.Instance;
        var parts = new List<ContextSource>();
        var used = 0;
        foreach (var m in Visible(person, workRef, switches).OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            var text = $"- [{m.Id}] ({(m.Kind == MemoryKind.Person ? "about the person" : "about this work")}) {m.Text}";
            var tokens = counter.Count(text);
            if (used + tokens > budgetTokens) break;
            used += tokens;
            parts.Add(new ContextSource("memory", m.Id, KnowledgeHash(m.Text), text));
        }
        return parts;
    }

    private static string KnowledgeHash(string text) => DbDataKit.Knowledge.KnowledgeItemLoader.HashOf(text)[..12];

    /// <summary>The tools <c>memory_remember</c> and <c>memory_forget</c> for one conversation (none when both kinds are off). Their prompting is their own: remembering asks once per conversation, forgetting asks every time; an install or the agent may only make them stricter.</summary>
    public IReadOnlyList<AgentTool> Tools(AgentRunContext context, MemorySwitches switches, string? workRef)
    {
        if (!switches.Person && !switches.Work) return [];
        var kinds = new JsonArray();
        if (switches.Person) kinds.Add("person");
        if (switches.Work && workRef is not null) kinds.Add("work");
        if (kinds.Count == 0) return [];
        var remember = new AgentTool("memory_remember", "Remember one short fact for later conversations: about the person (their preferences, how they like answers) or about the work in hand. Never a secret, never something that is shared knowledge: suggest that to the knowledge owner instead.",
            Traits.WritesData,
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("text"),
                ["properties"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = $"One fact, at most {MaximumLength} characters." },
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = kinds, ["description"] = "person (default) or work." },
                },
            }, "memory", (arguments, _) =>
            {
                var kind = (string?)arguments["kind"] == "work" ? MemoryKind.Work : MemoryKind.Person;
                var result = Remember(context.Person, kind, workRef, context.Agent, (string)arguments["text"]!, switches);
                return Task.FromResult(new ToolOutcome(result.Message, !result.Ok));
            })
        { DeclaredPrompting = Prompting.AskOnce };
        var forget = new AgentTool("memory_forget", "Forget a memory by its id, when the person asks you to or it is wrong.", Traits.WritesData,
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("id"),
                ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string", ["description"] = "The id of the memory, as it is listed in what you remember." } },
            }, "memory", (arguments, _) =>
            {
                var result = Forget((string)arguments["id"]!, context.Person, workRef);
                return Task.FromResult(new ToolOutcome(result.Message, !result.Ok));
            })
        { DeclaredPrompting = Prompting.Ask };
        return [remember, forget];
    }
}
