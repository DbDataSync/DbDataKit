using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Agents;

public enum AgentAudience
{
    FrontEnd,
    BackEnd,
}

/// <summary>A tool an agent names: a command (<c>project compile</c>) or an action, with the prompting setting it asks for, if any. Line and column say where it is written, so a check can point at it.</summary>
public sealed record AgentToolRef(string Kind, string Name, Prompting? Prompting, int Line, int Column)
{
    /// <summary>The tool name the runtime gives it: a command <c>project compile</c> is <c>project_compile</c>, as the MCP server names it.</summary>
    public string ToolName => Name.Replace(' ', '_').Replace('-', '_');
}

/// <summary>What an agent may draw on: which knowledge it searches (<see cref="From"/> and <see cref="Exclude"/>) and which references it is always given (<see cref="Always"/>; they count against its budget).</summary>
public sealed record AgentKnowledge(IReadOnlyList<string> From, IReadOnlyList<string> Exclude, IReadOnlyList<string> Always)
{
    public static readonly AgentKnowledge None = new([], [], []);
}

/// <summary>Whether the agent keeps memories for the person and for the work (plan 27 section 3.11).</summary>
public sealed record AgentMemory(bool Person, bool Workspace);

/// <summary>A scope: the objects of one kind the agent may use, <c>all</c> of them (within the person's own access) or those named. Line and column say where it is written.</summary>
public sealed record AgentScope(string Kind, bool All, IReadOnlyList<string> Names, int Line, int Column);

/// <summary>
/// The agent definition, <c>agent/1</c> (plan 27a AG09, plan 27 section 3.1): who it is for, its model slot, its instructions, skills, tools with the prompting each asks for, scopes, knowledge, memory, subagents and limits. It narrows what the person may do and
/// never widens it.
/// </summary>
public sealed record AgentDefinition(
    string Name,
    string? Title,
    string? Description,
    string? Owner,
    AgentAudience Audience,
    string ModelSlot,
    IReadOnlyList<string> Instructions,
    IReadOnlyList<string> Skills,
    IReadOnlyList<AgentToolRef> Tools,
    IReadOnlyList<AgentScope> Scopes,
    AgentKnowledge Knowledge,
    AgentMemory Memory,
    IReadOnlyList<string> Subagents,
    LimitSet Limits,
    IReadOnlyList<string> Output);

public sealed class AgentDefinitionResult(AgentDefinition? definition, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public AgentDefinition? Definition { get; } = definition;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Definition is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of <c>agent.yml</c>, <c>agent/1</c> (plan 27a AG09): every problem is a diagnostic with the file, line and column and a suggestion, and nothing throws on a damaged file. Codes: <c>DBD-AGT-001</c> a value or the shape,
/// <c>-002</c> the format number. What the definition names is checked against what exists by <see cref="AgentChecker"/>.
/// </summary>
public static class AgentDefinitionLoader
{
    public const string BadAgent = "DBD-AGT-001";
    public const string FormatNumber = "DBD-AGT-002";

    /// <summary>The format this program reads (plan 24a: a newer one is refused naming the file and both numbers).</summary>
    public const int Version = 1;

    public const string Kind = "agent";

    public static readonly string[] Keys = ["agent", "name", "title", "description", "owner", "audience", "model", "instructions", "skills", "tools", "scopes", "knowledge", "memory", "subagents", "limits", "output"];

    public static readonly string[] Audiences = ["front-end", "back-end"];

    public static readonly string[] ToolKinds = ["command", "action"];

    public static readonly string[] ToolKeys = ["command", "action", "prompting"];

    public static readonly string[] PromptingNames = ["ask", "ask-once", "do-not-ask"];

    /// <summary>The kinds of object a scope may name (plan 27 section 3.6): the semantic models, DbDataStore models and operations, Build projects, connections, apps and integrations.</summary>
    public static readonly string[] ScopeKinds = ["semantic-models", "store-models", "build-projects", "connections", "apps", "integrations"];

    public static readonly string[] KnowledgeKeys = ["from", "exclude", "always"];

    public static readonly string[] MemoryKeys = ["person", "workspace"];

    public static readonly string[] LimitKeys = ["turns", "tool-calls", "tokens", "cost"];

    /// <summary>The view kinds an agent may return (plan 32: tables, matrix grids and Vega-Lite charts); an answer is checked against them.</summary>
    public static readonly string[] OutputKinds = ["table", "matrix", "vega-lite"];

    /// <summary>The operations of a Store model scope entry (<c>Customers.Orders:read</c>).</summary>
    public static readonly string[] StoreOperations = ["read", "write", "change-request"];

    private static readonly Regex Name = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Schema()
    {
        using var stream = typeof(AgentDefinitionLoader).Assembly.GetManifestResourceStream("DbDataKit.Agents.schemas.agent.schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Refusal(string file, int format) => format > Version
        ? $"the {Kind} definition '{file}' is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
        : $"the {Kind} definition '{file}' is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";

    public static AgentDefinitionResult Load(string yaml, string file)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml.TrimStart('﻿'), file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadAgent, file, 1, 1, "The agent file is empty.", "Write `agent: 1`, a `name`, an `audience` and the rest of the definition."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var definition = new Reader(file, diags).Read(root);
        return new(diags.HasErrors() ? null : definition, diags);
    }

    private sealed class Reader(string fileName, List<ConfigDiagnostic> diags) : AgentFileReader(fileName, diags, BadAgent)
    {

        public AgentDefinition? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(root, "An agent is a mapping with `agent: 1`, `name` and `audience`."); return null; }
            if (!CheckFormat(map)) return null;
            CheckKeys(map, Keys, "an agent definition");

            var name = Scalar(map, "name", required: true, map);
            if (name is not null && !Name.IsMatch(name.Value)) { Bad(name, $"`{name.Value}` is not an agent name: lower-case letters, digits and '-', starting with a letter, at most 64.", "Example: `build-helper`."); name = null; }
            var audience = Audience(map);
            var slot = ModelSlot(map);
            var instructions = Paths(map, "instructions");
            var skills = StringList(map, "skills", required: false, unique: true)?.Select(s => s.Value).ToList() ?? [];
            var tools = Tools(map);
            var scopes = Scopes(map);
            var subagents = StringList(map, "subagents", required: false, unique: true)?.Select(s => s.Value).ToList() ?? [];
            var output = Output(map);
            if (name is null || audience is null || slot is null) return null;
            return new AgentDefinition(name.Value, Text(map, "title"), Text(map, "description"), Text(map, "owner"), audience.Value, slot, instructions, skills, tools, scopes, Knowledge(map), Memory(map), subagents, Limits(map), output);
        }

        private bool CheckFormat(YamlMapping map)
        {
            var node = map.Get("agent");
            if (node is null) { Add(FormatNumber, map, $"`agent` is missing: the file says which format of {Kind} it is in.", "Add `agent: 1`."); return false; }
            if (node is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var format) || format < 1)
            { Add(FormatNumber, node, "`agent` must be a positive whole number."); return false; }
            if (format == Version) return true;
            Add(FormatNumber, node, Refusal(File, format), format > Version ? $"Upgrade the tool, or write the file in format {Version}." : null);
            return false;
        }

        private string? Text(YamlMapping map, string key)
        {
            if (map.Get(key) is not { } node) return null;
            if (node is YamlScalar { Value.Length: > 0 and <= 2000 } s && !s.Value.Any(c => char.IsControl(c) && c != '\n')) return s.Value;
            Bad(node, $"`{key}` is text of at most 2000 characters.");
            return null;
        }

        private AgentAudience? Audience(YamlMapping map)
        {
            var node = Scalar(map, "audience", required: true, map);
            if (node is null) return null;
            var i = Array.IndexOf(Audiences, node.Value);
            if (i >= 0) return (AgentAudience)i;
            Bad(node, $"`{node.Value}` is not an audience.", $"Use one of: {string.Join(", ", Audiences)}.");
            return null;
        }

        private string? ModelSlot(YamlMapping map)
        {
            if (map.Get("model") is not { } node) { Add(SharedCodes.MissingKey, map, "Required key `model` is missing.", "Add `model: { slot: default }`: the slot is bound to a provider connection and model per channel."); return null; }
            if (node is YamlMapping m)
            {
                CheckKeys(m, ["slot"], "`model`");
                return Scalar(m, "slot", required: true, m)?.Value;
            }
            Bad(node, "`model` is a mapping naming a slot: `model: { slot: default }`.");
            return null;
        }

        private List<string> Paths(YamlMapping map, string key)
        {
            var list = StringList(map, key, required: false) ?? [];
            foreach (var p in list.Where(p => p.Value.StartsWith('/') || p.Value.Contains("..") || p.Value.Contains('\\')))
                Bad(p, $"`{p.Value}` is not a path inside the agent's project: write it relative, with forward slashes and no `..`.");
            return list.Select(p => p.Value).ToList();
        }

        private List<AgentToolRef> Tools(YamlMapping map)
        {
            var tools = new List<AgentToolRef>();
            if (map.Get("tools") is not { } node) return tools;
            if (node is not YamlSequence seq) { Bad(node, "`tools` is a list: `- command: project compile`, optionally with `prompting:`."); return tools; }
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping entry) { Bad(item, "A tool is a mapping such as `command: project compile`.", $"Keys: {string.Join(", ", ToolKeys)}."); continue; }
                CheckKeys(entry, ToolKeys, "a tool");
                var kinds = ToolKinds.Where(k => entry.Get(k) is not null).ToList();
                if (kinds.Count != 1) { Bad(entry, kinds.Count == 0 ? "A tool names a `command` or an `action`." : "A tool names either a `command` or an `action`, not both."); continue; }
                var named = entry.Get(kinds[0]);
                if (named is not YamlScalar { Value.Length: > 0 } nameNode || nameNode.Value.Any(char.IsControl)) { Bad(named!, $"`{kinds[0]}` names one tool, on one line."); continue; }
                Prompting? prompting = null;
                if (entry.Get("prompting") is { } p)
                {
                    if (p is YamlScalar ps && Array.IndexOf(PromptingNames, ps.Value) is var i and >= 0) prompting = (Prompting)i;
                    else Bad(p, $"`{(p as YamlScalar)?.Value}` is not a prompting setting.", $"Use one of: {string.Join(", ", PromptingNames)}.");
                }
                if (tools.Any(t => t.Kind == kinds[0] && t.Name == nameNode.Value)) { Bad(nameNode, $"`{nameNode.Value}` is listed more than once."); continue; }
                tools.Add(new AgentToolRef(kinds[0], nameNode.Value, prompting, nameNode.Line, nameNode.Column));
            }
            return tools;
        }

        private List<AgentScope> Scopes(YamlMapping map)
        {
            var scopes = new List<AgentScope>();
            if (map.Get("scopes") is not { } node) return scopes;
            if (node is not YamlMapping m) { Bad(node, "`scopes` is a mapping: `scopes: { build-projects: all }`.", $"Kinds: {string.Join(", ", ScopeKinds)}."); return scopes; }
            CheckKeys(m, ScopeKinds, "`scopes`");
            foreach (var entry in m.Entries.Where(e => ScopeKinds.Contains(e.Key.Value)))
            {
                if (entry.Value is YamlScalar { Value: "all" }) { scopes.Add(new AgentScope(entry.Key.Value, true, [], entry.Key.Line, entry.Key.Column)); continue; }
                var names = new List<string>();
                if (entry.Value is YamlSequence list && list.Items.Count > 0)
                {
                    foreach (var item in list.Items)
                    {
                        if (item is not YamlScalar { Value.Length: > 0 } s || s.Value.Any(char.IsControl)) { Bad(item, $"`{entry.Key.Value}` lists names on one line."); continue; }
                        if (entry.Key.Value == "store-models" && s.Value.Contains(':') && !StoreOperations.Contains(s.Value[(s.Value.LastIndexOf(':') + 1)..]))
                        { Bad(s, $"`{s.Value}` ends in an operation that is not one: use {string.Join(", ", StoreOperations)}."); continue; }
                        names.Add(s.Value);
                    }
                    scopes.Add(new AgentScope(entry.Key.Value, false, names, entry.Key.Line, entry.Key.Column));
                }
                else Bad(entry.Value, $"`{entry.Key.Value}` is `all` (within the person's own access) or a list of names.");
            }
            return scopes;
        }

        private AgentKnowledge Knowledge(YamlMapping map)
        {
            if (map.Get("knowledge") is not { } node) return AgentKnowledge.None;
            if (node is not YamlMapping m) { Bad(node, "`knowledge` is a mapping: `{ from: [...], exclude: [...], always: [...] }`."); return AgentKnowledge.None; }
            CheckKeys(m, KnowledgeKeys, "`knowledge`");
            List<string> Of(string key) => StringList(m, key, required: false, unique: true)?.Select(s => s.Value).ToList() ?? [];
            return new AgentKnowledge(Of("from"), Of("exclude"), Of("always"));
        }

        private AgentMemory Memory(YamlMapping map)
        {
            if (map.Get("memory") is not { } node) return new AgentMemory(false, false);
            if (node is not YamlMapping m) { Bad(node, "`memory` is a mapping: `{ person: true, workspace: true }`."); return new AgentMemory(false, false); }
            CheckKeys(m, MemoryKeys, "`memory`");
            return new AgentMemory(Flag(m, "person"), Flag(m, "workspace"));
        }

        private bool Flag(YamlMapping map, string key)
        {
            if (map.Get(key) is not { } n) return false;
            if (n is YamlScalar { Value: "true" or "false" } s) return s.Value == "true";
            Bad(n, $"`{key}` is `true` or `false`.");
            return false;
        }

        private LimitSet Limits(YamlMapping map)
        {
            if (map.Get("limits") is not { } node) return LimitSet.None;
            if (node is not YamlMapping m) { Bad(node, "`limits` is a mapping: `{ turns: 60, cost: 5.00 }`.", $"Keys: {string.Join(", ", LimitKeys)}."); return LimitSet.None; }
            CheckKeys(m, LimitKeys, "`limits`");
            return ReadLimits(m);
        }

        private List<string> Output(YamlMapping map)
        {
            var list = StringList(map, "output", required: false, unique: true) ?? [];
            foreach (var o in list.Where(o => !OutputKinds.Contains(o.Value))) Bad(o, $"`{o.Value}` is not a view kind an agent may return.", $"Use: {string.Join(", ", OutputKinds)}.");
            return list.Select(o => o.Value).ToList();
        }
    }
}
