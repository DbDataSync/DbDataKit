using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;
using DbDataKit.Knowledge.Cli;
using AgentAnswerViews = DbDataKit.Views.AgentAnswerViews;
using DbDataKit.State;
using Microsoft.Extensions.AI;

namespace DbDataKit.Server;

/// <summary>The model bound to an agent's slot for a channel: the client, its name, its price and its window.</summary>
public sealed record BoundModel(IChatClient Client, string Model, ModelPrice? Price, int WindowTokens, IReadOnlyDictionary<string, ModelPrice?>? Prices = null);

/// <summary>Which model an agent's slot is bound to (plan 27a AG05: model providers are connections, bound per channel; not built, so by default none is).</summary>
public interface IAgentModels
{
    BoundModel? For(string slot, string? channel);
}

public sealed class NoAgentModels : IAgentModels
{
    public BoundModel? For(string slot, string? channel) => null;
}

/// <summary>The commands the console's agents may use: a router and the rule for what it offers.</summary>
public interface IAgentToolSource
{
    NounRouter Router();

    McpOptions Options { get; }
}

/// <summary>
/// The console's own commands that only read, run in this process. They hold the console's authority, not the person's, so writes are not offered, and what reaches outside (<c>health check</c> takes an address), holds trust (<c>identity</c>, <c>user</c>, <c>instance</c>, <c>server</c>,
/// <c>secret</c>) or reaches state and runners is kept out.
/// </summary>
public sealed class ConsoleToolSource : IAgentToolSource
{
    private static readonly HashSet<string> NotForAgents = new(StringComparer.Ordinal) { "user", "identity", "instance", "server", "state", "runner", "secret", "health" };

    public NounRouter Router() => ConsoleCli.Router();

    public McpOptions Options { get; } = new(NotForAgents, AllowWrites: false);
}

/// <summary>An agent as the list shows it.</summary>
public sealed record AgentListing(string Name, string? Title, string? Description, string Audience, string ModelSlot, int Problems, bool ModelBound);

/// <summary>One agent in full: its file, what <c>agent check</c> says, what each tool will ask, the model's space and its skills (plan 27a AG16 in the console's folder, AG30, AG31, AG32).</summary>
public sealed record AgentDetail(string Name, string File, string Yaml, IReadOnlyList<DiagnosticView> Diagnostics, IReadOnlyList<ToolPromptingRow> Prompting, ModelSpaceView? Space, IReadOnlyList<SkillView> Skills,
    IReadOnlyList<string> Instructions, IReadOnlyList<string> Tools, bool ModelBound);

public sealed record ConversationView(ConversationRow Conversation, IReadOnlyList<TurnTextRow> Text, IReadOnlyList<TurnRecordRow> Turns, IReadOnlyList<AgentAuditRow> Audit, IReadOnlyList<ApprovalRow> Approvals, bool Running);

/// <summary>
/// The agents of the console (plan 27a AG14's host, AG15, AG17, AG29): <c>DbDataKit.Agents</c> hosted for back-end agents. The definitions are files in the console's directory (<c>agents/&lt;name&gt;/agent.yml</c> and the instruction files beside it; the skills in
/// <c>skills/&lt;name&gt;/SKILL.md</c>; the install policy in <c>agent-policy.yml</c>), read with the strict readers. A conversation runs <see cref="AgentSession"/> in the background and keeps its record in the <c>agents</c> module; a step that needs a person waits in the
/// <see cref="ApprovalDesk"/> until that person answers. <b>The tools are the console's own commands that only read</b>, run in this process: they hold the console's authority, not the person's, so writes are not offered and only an administrator may talk to an agent, until the relay
/// to a server as the person (and a workspace of the Workspace server) exists. The model comes from <see cref="IAgentModels"/>; with none bound a conversation refuses to start and says how to bind one.
/// </summary>
public sealed class ConsoleAgents
{
    private readonly string _directory;
    private readonly AgentStore _store;
    private readonly IAgentModels _models;
    private readonly IAgentToolSource _toolSource;
    private readonly ApprovalDesk _desk;
    private readonly ConcurrentDictionary<string, Live> _live = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly IBuildRelay? _relay;
    private readonly IUserAccounts? _users;
    private IReadOnlyList<BuildCommand>? _surface;
    private readonly object _surfaceLock = new();

    private sealed class Live(AgentSession session, string agent) { public AgentSession Session { get; } = session; public string Agent { get; } = agent; public Task? Running { get; set; } public CancellationTokenSource? Cancel { get; set; } }

    public ConsoleAgents(ConsoleSettings settings, StateDatabase database, IAgentModels models, IAgentToolSource toolSource, ConsoleKnowledge knowledge, TimeProvider? time = null, IBuildRelay? relay = null, IUserAccounts? users = null)
    {
        _relay = relay;
        _users = users;
        _directory = settings.Directory;
        _store = new AgentStore(database);
        Evaluations = new EvaluationStore(database);
        _models = models;
        _toolSource = toolSource;
        Knowledge = knowledge;
        _time = time ?? TimeProvider.System;
        _desk = new ApprovalDesk(new StateApprovalLedger(_store), time: _time);
        Memories = new MemoryService(new StateMemoryStore(_store), _time);
    }

    /// <summary>The runs of agents' evaluation sets and the minimum scores before a promotion (plan 27a AG23), kept in the <c>agents</c> module.</summary>
    public EvaluationStore Evaluations { get; }

    /// <summary>The models the slots are bound to (the agent's own, and the judge's slot for evaluations).</summary>
    public IAgentModels Models => _models;

    /// <summary>What agents remember (plan 27a AG19): the person's own and the work's, kept in the <c>agents</c> module.</summary>
    public MemoryService Memories { get; }

    public AgentStore Store => _store;

    public ApprovalDesk Desk => _desk;

    public ConsoleKnowledge Knowledge { get; }

    public string AgentsFolder => Path.Combine(_directory, "agents");

    public string PolicyFile => Path.Combine(_directory, "agent-policy.yml");

    public string SkillsFolder => Path.Combine(_directory, "skills");

    // ---- definitions ----

    public AgentPolicy Policy(out IReadOnlyList<DiagnosticView> diagnostics)
    {
        diagnostics = [];
        if (!File.Exists(PolicyFile)) return AgentPolicy.None;
        var result = AgentPolicyLoader.Load(File.ReadAllText(PolicyFile), "agent-policy.yml");
        diagnostics = [.. result.Diagnostics.Select(DiagnosticView.Of)];
        return result.Policy ?? AgentPolicy.None;
    }

    public string PolicyText() => File.Exists(PolicyFile) ? File.ReadAllText(PolicyFile) : PolicyEditing.Write(AgentPolicy.None);

    public IReadOnlyList<AgentListing> List()
    {
        if (!Directory.Exists(AgentsFolder)) return [];
        var listings = new List<AgentListing>();
        foreach (var folder in Directory.EnumerateDirectories(AgentsFolder).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            var detail = Read(name, withReports: false);
            if (detail is null) continue;
            var loaded = AgentDefinitionLoader.Load(detail.Yaml, detail.File);
            listings.Add(new AgentListing(name, loaded.Definition?.Title, loaded.Definition?.Description, loaded.Definition is { Audience: AgentAudience.FrontEnd } ? "front-end" : "back-end",
                loaded.Definition?.ModelSlot ?? "", detail.Diagnostics.Count(d => d.Severity == "error"), loaded.Definition is not null && _models.For(loaded.Definition.ModelSlot, null) is not null));
        }
        return listings;
    }

    private AgentCatalog Catalog()
    {
        var catalog = AgentCatalog.Of(_toolSource.Router(), Skills().Select(s => s.Skill.Name), Directory.Exists(AgentsFolder) ? Directory.EnumerateDirectories(AgentsFolder).Select(Path.GetFileName)!.Select(n => n!) : []);
        var tools = new Dictionary<string, Traits>(catalog.Tools, StringComparer.Ordinal);
        foreach (var command in BuildSurface().Where(c => c.OfferedToAgents)) tools.TryAdd(command.Tool, command.Traits);   // Build's commands, as the last Build server asked said them
        return catalog with { Tools = tools };
    }

    // ---- Build's commands (plan 27a AG14): learned from a Build server, kept in the console's folder so a definition is checked without one ----

    private string SurfaceFile => Path.Combine(_directory, "build-surface.json");

    /// <summary>The commands the last Build server asked offered (what <c>agent check</c> and the screens know of Build's tools).</summary>
    public IReadOnlyList<BuildCommand> BuildSurface()
    {
        lock (_surfaceLock)
        {
            if (_surface is not null) return _surface;
            try { _surface = File.Exists(SurfaceFile) ? BuildCommand.Parse(JsonNode.Parse(File.ReadAllText(SurfaceFile))) : []; }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { _surface = []; }
            return _surface;
        }
    }

    private void LearnSurface(IReadOnlyList<BuildCommand> commands)
    {
        lock (_surfaceLock)
        {
            _surface = commands;
            try
            {
                Directory.CreateDirectory(_directory);
                File.WriteAllText(SurfaceFile, new JsonObject { ["commands"] = new JsonArray(commands.Select(c => (JsonNode)new JsonObject { ["tool"] = c.Tool, ["name"] = c.Name, ["purpose"] = c.Purpose, ["impact"] = c.Impact, ["write_flag"] = c.WriteFlag, ["allowed"] = true, ["input_schema"] = c.InputSchema.DeepClone() }).ToArray()) }.ToJsonString());
            }
            catch (IOException) { /* the memory copy still serves */ }
        }
    }

    private RelayPerson? PersonNow(string id) => _users?.Get(id) is { Enabled: true } user ? new RelayPerson(user.Id, user.DisplayName, user.Role) : null;

    /// <summary>
    /// Makes (or brings up to date) the first agent, <c>build-helper</c>, from what a Build server offers an agent: its definition with every command it may be offered, its instructions, and the skill of that server's version of
    /// Build's agent kit. The person is whoever asked; the server is asked as them.
    /// </summary>
    public async Task<(bool Done, string? Problem)> InstallBuildHelperAsync(BuildTarget target, string person, CancellationToken cancellation)
    {
        if (_relay is null || PersonNow(person) is not { } who) return (false, "This console cannot reach a Build server.");
        var (commands, problem) = await BuildAgentTools.ListAsync(_relay, target, who, cancellation);
        if (problem is not null) return (false, problem);
        var (status, skill) = await _relay.CallAsync(target.Server, who, HttpMethod.Get, "/api/v1/skill", null, cancellation);
        if (status != 200 || (string?)skill?["content"] is not { } text) return (false, $"The Build server did not give its skill ({status}); it may be an older version without one.");
        LearnSurface(commands);
        var folder = Path.Combine(AgentsFolder, BuildAgentTools.HelperName);
        foreach (var (relative, content) in BuildAgentTools.HelperFiles(commands))
        {
            var file = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }
        var skillFile = Path.Combine(SkillsFolder, BuildAgentTools.SkillName, "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillFile)!);
        File.WriteAllText(skillFile, text);
        _store.RecordAudit(null, null, "agent-installed", person, BuildAgentTools.HelperName, KnowledgeItemLoader.HashOf(text)[..12], "console", null, null, $"from {target.Server}/{target.Project}");
        return (true, null);
    }

    private List<(Skill Skill, string Original)> Skills()
    {
        var skills = new List<(Skill, string)>();
        if (!Directory.Exists(SkillsFolder)) return skills;
        foreach (var file in Directory.EnumerateFiles(SkillsFolder, "SKILL.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            if (SkillReader.Read(text, Path.GetRelativePath(_directory, file).Replace('\\', '/')).Skill is { } skill) skills.Add((skill, text));
        }
        return skills;
    }

    public IReadOnlyList<AgentTool> ToolsFor(AgentDefinition definition)
    {
        var offered = CommandTools.From(_toolSource.Router(), _toolSource.Options);
        var wanted = definition.Tools.Select(t => t.ToolName).ToHashSet(StringComparer.Ordinal);
        var consoleTools = offered.Where(t => wanted.Contains(t.Name)).ToList();
        // Build's tools are described from what a Build server said (they run only in a conversation, as the person)
        consoleTools.AddRange(BuildAgentTools.Described(BuildSurface()).Where(t => wanted.Contains(t.Name) && consoleTools.All(c => c.Name != t.Name)));
        return consoleTools;
    }

    /// <summary>The agent's file with the reports a screen shows. Null when there is no such agent.</summary>
    public AgentDetail? Read(string name, bool withReports = true)
    {
        if (!AgentDefinitionLoader.IsName(name)) return null;
        var file = Path.Combine(AgentsFolder, name, "agent.yml");
        if (!File.Exists(file)) return null;
        var yaml = File.ReadAllText(file);
        return Describe(name, yaml, withReports);
    }

    /// <summary>Checks a text as the file of agent <paramref name="name"/> without saving it.</summary>
    public AgentDetail Describe(string name, string yaml, bool withReports = true)
    {
        var relative = $"agents/{name}/agent.yml";
        var loaded = AgentDefinitionLoader.Load(yaml, relative);
        var policy = Policy(out _);
        var diagnostics = loaded.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (loaded.Definition is not { } definition) return new AgentDetail(name, relative, yaml, diagnostics, [], null, [], [], [], false);
        diagnostics.AddRange(AgentChecker.Check(definition, relative, Catalog(), policy).Select(DiagnosticView.Of));
        var bound = _models.For(definition.ModelSlot, null);
        if (!withReports) return new AgentDetail(name, relative, yaml, diagnostics, [], null, [], definition.Instructions, [.. definition.Tools.Select(t => t.ToolName)], bound is not null);

        var tools = ToolsFor(definition);
        var agentSettings = definition.Tools.Where(t => t.Prompting is not null).ToDictionary(t => t.ToolName, t => t.Prompting!.Value, StringComparer.Ordinal);
        var prompting = ToolPromptingReport.Build(tools, policy.Prompting, agentSettings);
        var allSkills = Skills();
        var chosen = allSkills.Where(s => definition.Skills.Contains(s.Skill.Name, StringComparer.Ordinal)).ToList();
        var counter = EstimateCounter.Instance;
        var instructions = Instructions(name, definition);
        var sources = Knowledge.Sources();
        var inPlay = ScopesInPlay(definition);
        var boundKnowledge = BoundKnowledgeProvider.Bind(definition, sources, inPlay, Knowledge.Catalog);
        var window = bound?.WindowTokens ?? 128_000;
        var space = ModelSpace.Build(name, ModelSpace.AlwaysGiven(instructions, tools, chosen.Select(s => s.Skill.Offer), BoundKnowledgeProvider.AlwaysGiven(definition, sources), boundKnowledge), window, counter);
        var skillViews = SkillViews.Build(chosen, window, space.TotalTokens, Math.Min(4096, window / 8), counter);
        return new AgentDetail(name, relative, yaml, diagnostics, prompting, space, skillViews, definition.Instructions, [.. definition.Tools.Select(t => t.ToolName)], bound is not null);
    }

    /// <summary>
    /// What <c>knowledge check</c> and the check of a publish need of each agent that reads (plan 27a KN10, KN17): the tokens of what it is given before any knowledge (its instructions, tool definitions and skill descriptions), the window of the model its slot is bound to
    /// (128,000 when none is) and the knowledge it draws on.
    /// </summary>
    public IReadOnlyList<AgentBudgetInput> BudgetInputs()
    {
        var inputs = new List<AgentBudgetInput>();
        if (!Directory.Exists(AgentsFolder)) return inputs;
        var sources = Knowledge.Sources();
        var allSkills = Skills();
        foreach (var directory in Directory.EnumerateDirectories(AgentsFolder).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            var file = Path.Combine(directory, "agent.yml");
            if (!AgentDefinitionLoader.IsName(name) || !File.Exists(file) || AgentDefinitionLoader.Load(File.ReadAllText(file), $"agents/{name}/agent.yml").Definition is not { } definition) continue;
            var chosen = allSkills.Where(s => definition.Skills.Contains(s.Skill.Name, StringComparer.Ordinal)).Select(s => s.Skill.Offer);
            var fixedParts = ModelSpace.AlwaysGiven(Instructions(name, definition), ToolsFor(definition), chosen, [], BoundKnowledge.Empty);
            inputs.Add(new AgentBudgetInput(definition, fixedParts.Sum(p => EstimateCounter.Instance.Count(p.Text)), _models.For(definition.ModelSlot, null)?.WindowTokens ?? 128_000, sources));
        }
        return inputs;
    }

    /// <summary>Saves an agent's file when it reads without an error; the diagnostics come back either way.</summary>
    public (bool Saved, AgentDetail Detail) Save(string name, string yaml)
    {
        var detail = Describe(name, yaml);
        if (detail.Diagnostics.Any(d => d.Severity == "error")) return (false, detail);
        var file = Path.Combine(AgentsFolder, name, "agent.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, yaml);
        return (true, detail);
    }

    private List<ContextSource> Instructions(string name, AgentDefinition definition)
    {
        var parts = new List<ContextSource>();
        foreach (var path in definition.Instructions)
        {
            var full = Path.GetFullPath(Path.Combine(AgentsFolder, name, path));
            if (!full.StartsWith(Path.GetFullPath(Path.Combine(AgentsFolder, name)) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;   // an instruction file stays in the agent's folder
            if (File.Exists(full)) parts.Add(ContextSource.Of("instructions", path, File.ReadAllText(full)));
        }
        return parts;
    }

    private static IReadOnlyList<InPlay> ScopesInPlay(AgentDefinition definition) => AgentScopes.InPlay(definition);

    /// <summary>What the agent would be given for what is in play (the keys of subjects, such as <c>semantic-model:Sales/measure:revenue</c>), in order, with each part's tokens and share of the always-given budget (plan 27a KN28; the same as <c>knowledge context</c> will print).</summary>
    public ContextReport? ContextFor(string agentName, IEnumerable<string> inPlayKeys)
    {
        if (Read(agentName, withReports: false) is not { } detail || AgentDefinitionLoader.Load(detail.Yaml, detail.File).Definition is not { } definition) return null;
        var keys = inPlayKeys.Where(k => ConsoleKnowledge.ParseSubject(k) is not null).ToList();
        var inPlay = new List<InPlay>(ScopesInPlay(definition));
        inPlay.AddRange(keys.Select(k => new InPlay(ConsoleKnowledge.ParseSubject(k)!, Whole: false)));
        var sources = Knowledge.Sources();
        var bound = BoundKnowledgeProvider.Bind(definition, sources, inPlay, Knowledge.HasCatalog ? Knowledge.Catalog : null);
        var skills = Skills().Where(s => definition.Skills.Contains(s.Skill.Name, StringComparer.Ordinal)).Select(s => s.Skill.Offer).ToList();
        var assembled = ContextAssembler.Assemble(new ContextInputs(Knowledge.PolicyItems(), Instructions(agentName, definition), bound, [], BoundKnowledgeProvider.AlwaysGiven(definition, sources), skills), EstimateCounter.Instance);
        var window = _models.For(definition.ModelSlot, null)?.WindowTokens ?? 128_000;
        var budget = (int)(window * ContextBudget.DefaultShare);
        var total = assembled.Record.Sum(p => p.Tokens);
        return new ContextReport(agentName, assembled.Record, total, window, budget, total > budget, EstimateCounter.Instance.Basis, keys,
            [.. assembled.Record.Select(p => new ContextPartShare(p.Layer, p.Id, p.Version, p.Tokens, budget == 0 ? 0 : (double)p.Tokens / budget))]);
    }

    // ---- policy ----

    /// <summary>What saving <paramref name="yaml"/> as the policy would change, loosenings first; the diagnostics when it does not read.</summary>
    public (AgentPolicy? Policy, IReadOnlyList<DiagnosticView> Diagnostics, IReadOnlyList<PolicyChange> Changes) PreviewPolicy(string yaml)
    {
        var result = AgentPolicyLoader.Load(yaml, "agent-policy.yml");
        var diagnostics = result.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (result.Policy is not { } policy || diagnostics.Any(d => d.Severity == "error")) return (null, diagnostics, []);
        return (policy, diagnostics, PolicyEditing.Describe(Policy(out _), policy));
    }

    /// <summary>Saves the policy. A change that loosens something needs <paramref name="confirmed"/>; what was loosened, by whom and when is recorded either way.</summary>
    public (bool Saved, IReadOnlyList<DiagnosticView> Diagnostics, IReadOnlyList<PolicyChange> Changes, bool NeedsConfirmation) SavePolicy(string yaml, bool confirmed, string person)
    {
        var (policy, diagnostics, changes) = PreviewPolicy(yaml);
        if (policy is null) return (false, diagnostics, changes, false);
        var loosened = changes.Where(c => c.Loosens).ToList();
        if (loosened.Count > 0 && !confirmed) return (false, diagnostics, changes, true);
        var normalised = PolicyEditing.Write(policy);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PolicyFile, normalised);
        foreach (var change in changes)
            _store.RecordAudit(null, null, change.Loosens ? "policy-loosened" : "policy-changed", person, "install-policy", "1", "console", null, null, change.Text);
        return (true, diagnostics, changes, false);
    }

    // ---- conversations ----

    public async Task<(ConversationRow? Conversation, string? Problem)> StartAsync(string agentName, string person, string personName, string? title, BuildTarget? build = null, CancellationToken cancellation = default, Action<ToolCall, ToolOutcome>? observe = null)
    {
        // an agent that works on a Build project asks the server for what it may use, as the person, before its definition is read (a tool it names is checked against that list)
        IReadOnlyList<BuildCommand> live = [];
        if (build is not null)
        {
            if (_relay is null || PersonNow(person) is not { } who) return (null, "This console cannot reach a Build server as you.");
            var (commands, problem) = await BuildAgentTools.ListAsync(_relay, build, who, cancellation);
            if (problem is not null) return (null, problem);
            live = commands;
            LearnSurface(commands);
        }
        var detail = Read(agentName, withReports: false);
        if (detail is null) return (null, $"There is no agent named '{agentName}'.");
        var loaded = AgentDefinitionLoader.Load(detail.Yaml, detail.File);
        if (loaded.Definition is not { } definition || detail.Diagnostics.Any(d => d.Severity == "error"))
            return (null, $"The agent '{agentName}' has errors in its definition ({detail.Diagnostics.First(d => d.Severity == "error").Message}). Fix them first.");
        if (definition.Audience != AgentAudience.BackEnd) return (null, $"The agent '{agentName}' is a front-end agent; the console runs back-end agents.");
        var model = _models.For(definition.ModelSlot, null);
        if (model is null) return (null, $"No model is bound to the slot '{definition.ModelSlot}'. Model providers are connections bound to a slot per channel; until that exists in this install, no conversation can start.");

        var version = KnowledgeItemLoader.HashOf(detail.Yaml)[..12];
        var id = $"c-{Guid.NewGuid():N}"[..14];
        _store.Start(id, agentName, version, person, personName, "console", null, null, title);
        var context = new AgentRunContext(person, agentName, version, id);
        var policy = Policy(out _);
        var wanted = definition.Tools.Select(t => t.ToolName).ToHashSet(StringComparer.Ordinal);
        var consoleOffered = CommandTools.From(_toolSource.Router(), _toolSource.Options).Where(t => wanted.Contains(t.Name)).ToList();
        var buildWanted = wanted.Where(w => consoleOffered.All(c => c.Name != w)).ToHashSet(StringComparer.Ordinal);
        if (buildWanted.Count > 0 && build is null) return (null, $"The agent '{agentName}' works on a Build project: say which server, project and workspace.");
        var tools = consoleOffered;
        if (build is not null && _relay is not null) tools.AddRange(BuildAgentTools.Tools(_relay, build, live, buildWanted, () => PersonNow(person)));
        var skills = new SkillCatalog(Skills().Where(s => definition.Skills.Contains(s.Skill.Name, StringComparer.Ordinal)).Select(s => s.Skill));
        var log = new StateConversationLog(_store, person, agentName, version, "console");
        if (definition.Skills.Count > 0) tools.Add(skills.Tool(context, log));
        // memories (AG19): the tools only when a kind is on, and what is remembered is read again every turn, so a forgotten one is not given again
        var switches = MemorySwitches.For(definition.Memory, policy);
        var workRef = build?.Workspace is { } ws ? $"{build.Server}/{build.Project}/{ws}" : null;
        tools.AddRange(Memories.Tools(context, switches, workRef));
        var memoryRoom = Math.Max(200, model.WindowTokens / 20);
        // a view in an answer (AG22) is checked against the view schemas before it is shown
        if (definition.Output.Count > 0)
            tools.Add(AnswerViewTool.Create(definition.Output, (view, kinds) => { var check = AgentAnswerViews.Check(view, kinds); return new ViewVerdict(check.Ok, check.Message); }));
        // subagents (AG20): each is a tool; its reach is what this agent can call, narrowed by what it names; its asks come to this conversation's desk
        if (definition.Subagents.Count > 0)
        {
            var approver = _desk.ApproverFor(id);
            var parentTools = tools.ToList();
            tools.AddRange(SubagentTools.For(definition, parentTools, context, name => SubagentSetupFor(name, parentTools), d => new ApprovalGate(approver, policy.Prompting, d.Tools.Where(t => t.Prompting is not null).ToDictionary(t => t.ToolName, t => t.Prompting!.Value, StringComparer.Ordinal), log), null, null, log));
        }
        var tracker = new InPlayTracker(ScopesInPlay(definition));
        var instructions = Instructions(agentName, definition);
        var sources = Knowledge.Sources();
        var session = new AgentSession(context, definition, model.Client, tools, new AgentSessionOptions
        {
            Policy = policy,
            Log = log,
            Approver = _desk.ApproverFor(id),
            Price = model.Price,
            PriceOf = name => model.Prices is { } prices && prices.TryGetValue(name, out var price) ? price : model.Price,
            Model = model.Model,
            Ledger = new StateUsageLedger(_store),
            AfterCall = (call, outcome) => { tracker.Observe(call, outcome); observe?.Invoke(call, outcome); },
            Inputs = _ => new ContextInputs(Knowledge.PolicyItems(), instructions, BoundKnowledgeProvider.Bind(definition, sources, tracker.Current, Knowledge.Catalog), Memories.Inputs(person, workRef, switches, memoryRoom), BoundKnowledgeProvider.AlwaysGiven(definition, sources), skills.Offers),
        });
        _live[id] = new Live(session, agentName);
        return (_store.Find(id), null);
    }

    /// <summary>A subagent as the runtime runs it: its definition, version, the model its slot is bound to, its instructions, and the tools it names that the calling agent can call. Null when it does not exist, is not a back-end agent or has no model bound.</summary>
    private SubagentSetup? SubagentSetupFor(string name, IReadOnlyList<AgentTool> parentTools)
    {
        var detail = Read(name, withReports: false);
        if (detail is null || AgentDefinitionLoader.Load(detail.Yaml, detail.File).Definition is not { Audience: AgentAudience.BackEnd } definition || detail.Diagnostics.Any(d => d.Severity == "error")) return null;
        var model = _models.For(definition.ModelSlot, null);
        if (model is null) return null;
        var wanted = definition.Tools.Select(t => t.ToolName).ToHashSet(StringComparer.Ordinal);
        return new SubagentSetup(definition, KnowledgeItemLoader.HashOf(detail.Yaml)[..12], model.Client, string.Join("\n\n", Instructions(name, definition).Select(c => c.Text)), [.. parentTools.Where(t => wanted.Contains(t.Name))]);
    }

    /// <summary>Takes the person's message and runs it in the background; false when the conversation is not here, is another person's or is still running.</summary>
    public (bool Accepted, string? Problem) Send(string conversationId, string person, string text)
    {
        if (!_live.TryGetValue(conversationId, out var live) || _store.Find(conversationId) is not { } row || row.Person != person) return (false, "There is no such conversation of yours here (a conversation does not survive a restart of the console; start a new one).");
        if (live.Running is { IsCompleted: false }) return (false, "The agent is still answering the last message.");
        live.Cancel = new CancellationTokenSource();
        live.Running = Task.Run(async () =>
        {
            try { await live.Session.SendAsync(text, live.Cancel.Token); }
            catch (Exception ex)
            {
                _store.AppendText(conversationId, "assistant", $"The run failed: {ex.Message}");
                _store.SetStatus(conversationId, "failed", ex.Message);
            }
        });
        return (true, null);
    }

    public bool Cancel(string conversationId, string person)
    {
        if (!_live.TryGetValue(conversationId, out var live) || _store.Find(conversationId)?.Person != person) return false;
        live.Cancel?.Cancel();
        return true;
    }

    public ConversationView? View(string conversationId) =>
        _store.Find(conversationId) is { } row
            ? new ConversationView(row, _store.Text(conversationId), _store.Turns(conversationId), [.. _store.Audit(conversationId: conversationId, limit: 500).Reverse()],
                [.. _store.Approvals(false, null, 500).Where(a => a.ConversationId == conversationId)], _live.TryGetValue(conversationId, out var l) && l.Running is { IsCompleted: false })
            : null;

    /// <summary>Waits for a conversation's current run (tests).</summary>
    public Task WhenIdle(string conversationId) => _live.TryGetValue(conversationId, out var l) && l.Running is { } r ? r : Task.CompletedTask;

    // ---- outside agents (plan 27a AG29): the console's half of `ui mcp --ask` ----

    /// <summary>An outside agent's step that its client cannot ask the person about: it waits in the inbox. Returns the id the agent polls.</summary>
    public Guid AskForOutside(string person, string agent, string agentVersion, string tool, string preview, string? arguments, string traits)
    {
        var id = _store.RequestApproval(null, person, agent, agentVersion, "mcp", tool, preview, arguments, traits);
        _store.RecordAudit(null, null, AgentEventKinds.Asked, person, agent, agentVersion, "mcp", tool, arguments, preview);
        return id;
    }

    /// <summary>The person's answer to a step; false when it does not count. Answers a waiting run of a console conversation, or the record of an outside agent's step.</summary>
    public bool Answer(Guid id, bool approved, string by, string? note, bool anyPerson)
    {
        if (_desk.Answer(id, approved, by, note, anyPerson)) return true;
        if (_store.Approval(id) is not { Origin: "mcp" } row) return false;
        if (!_store.Answer(id, approved, by, note, anyPerson)) return false;
        _store.RecordAudit(row.ConversationId, null, approved ? AgentEventKinds.Approved : AgentEventKinds.Denied, row.Person, row.Agent, row.AgentVersion, "mcp", row.Tool, row.Arguments, note);
        return true;
    }

    /// <summary>Marks the steps nobody answered within a day expired (called when the inbox is read).</summary>
    public int ExpireOld() => _store.ExpireApprovals(TimeSpan.FromDays(1));
}

/// <summary>Writes the approvals of the desk to the <c>agents</c> module, with the audit rows that say it was asked and answered.</summary>
internal sealed class StateApprovalLedger(AgentStore store) : IApprovalLedger
{
    public Guid Requested(string? conversationId, ApprovalPrompt prompt) =>
        store.RequestApproval(conversationId, prompt.Person, prompt.Agent, prompt.AgentVersion, "console", prompt.Tool, prompt.Preview, prompt.Arguments.ToJsonString(), string.Join(", ", prompt.Traits.Names()));

    public bool Answered(Guid id, bool approved, string by, string? note, bool anyPerson) => store.Answer(id, approved, by, note, anyPerson);

    public void Expired(Guid id) => store.Expire(id);
}

/// <summary>Writes a conversation's words, turn records and events to the <c>agents</c> module.</summary>
internal sealed class StateConversationLog(AgentStore store, string person, string agent, string version, string origin) : IConversationLog
{
    public void Record(AgentEvent entry) =>
        store.RecordAudit(entry.Context.ConversationId, entry.Turn, entry.Kind, entry.Context.Person, entry.Context.Agent, entry.Context.AgentVersion, origin, entry.Tool, entry.Kind is AgentEventKinds.ToolCalled ? entry.Detail : null,
            entry.Kind is AgentEventKinds.ToolCalled ? null : entry.Detail, entry.At);

    public void Text(string conversationId, string role, string content) => store.AppendText(conversationId, role, content);

    public void Turn(AgentRunContext context, int turn, string? model, long inputTokens, long outputTokens, decimal cost, string? contextPrint, string? contextRecord) =>
        store.RecordTurn(context.ConversationId, turn, person, agent, version, model, inputTokens, outputTokens, cost, contextPrint, contextRecord);

    public void Status(string conversationId, string status, string? reason) => store.SetStatus(conversationId, status, reason);
}

/// <summary>The usage of finished runs, read from the recorded turns.</summary>
internal sealed class StateUsageLedger(AgentStore store) : IUsageLedger
{
    private static UsageTotals Of(AgentUsageRow? row) => row is null ? UsageTotals.Zero : new UsageTotals(row.Turns, 0, row.InputTokens + row.OutputTokens, row.Cost);

    public UsageTotals Person(string person) => Of(store.Usage("person").FirstOrDefault(u => u.Key == person));

    public UsageTotals Install() => Of(store.Usage("install").FirstOrDefault());

    public void Add(string person, UsageTotals usage) { /* the turn records are the ledger: nothing to add */ }
}

/// <summary>The memories of <see cref="MemoryService"/> kept in the <c>agents</c> module.</summary>
internal sealed class StateMemoryStore(AgentStore store) : IMemoryStore
{
    private static Memory Of(MemoryRow r) => new(r.Id, r.Kind == "work" ? MemoryKind.Work : MemoryKind.Person, r.Person, r.WorkRef, r.Agent, r.Content, r.CreatedAt, r.UpdatedAt);

    public IReadOnlyList<Memory> All(string? person = null, string? workRef = null) => [.. store.Memories(person, workRef).Select(Of)];

    public Memory? Find(string id) => store.Memory(id) is { } r ? Of(r) : null;

    public void Save(Memory m) => store.SaveMemory(new MemoryRow(m.Id, m.Kind == MemoryKind.Work ? "work" : "person", m.Person, m.WorkRef, m.Agent, m.Text, m.CreatedAt, m.UpdatedAt));

    public bool Remove(string id) => store.DeleteMemory(id);
}
