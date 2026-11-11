using DbDataKit.Authoring;
using DbDataKit.Authoring.Server;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Server;

/// <summary>
/// The Workspace hosted in the console (plan 33 section 3, SC05): the engine of <c>DbDataKit.Authoring.Server</c> over the console's own state (the module <c>workspace</c>, in the console's state database), the console's secret store (the
/// publisher's key is a secret of a name of its own, <c>workspace:publisher-key:&lt;target&gt;</c>), and the Workspace's own folder (<c>projects/</c>, <c>workspaces/</c>, <c>builds/</c>: <see cref="ConsoleWorkspaceSettings.DirectoryFor"/>).
/// <list type="bullet">
/// <item><b>No app code runs here.</b> A build is the same child process as the standalone server's (<c>AppSourceBuilder</c>, Bun with an allow-listed environment, in a network namespace); the console process only reads its result.</item>
/// <item><b>Audit.</b> What the Workspace audits goes to the console's audit with the module named: the verb is <c>workspace.&lt;noun&gt;.&lt;verb&gt;</c> (<see cref="WorkspaceAuditSink"/>).</item>
/// <item><b>Who may do what</b> is the signed-in person's roles (<see cref="ConsoleWorkspaceApi.RolesOf"/>), checked by the engine on every call exactly as it checks a paired console's token.</item>
/// </list>
/// </summary>
public sealed class ConsoleWorkspace : IDisposable
{
    public ConsoleWorkspace(ConsoleWorkspaceSettings settings, ConsoleSettings console, StateDatabase state, ILogger log)
    {
        Settings = settings;
        Directory = settings.DirectoryFor(console.Directory);
        System.IO.Directory.CreateDirectory(Directory);
        Log = line => log.LogWarning("{Line}", line);
        Store = new WorkspaceStore(state);
        Projects = new WorkspaceProjects(Directory, Store);
        Workspaces = new Workspaces(Directory, TimeProvider.System);
        Version = ConsoleHost.Version;
        var secrets = ConsoleSecrets.Open(console.Directory);
        Publishing = new WorkspacePublishing(Directory, Store, () => secrets, () => new SecretResolverChain([secrets, new EnvSecretProvider()]));
        Builds = new WorkspaceBuilds(Directory, Store, Workspaces, () => settings.Apps ?? WorkspaceAppSettings.LoadFrom(console.SettingsPath), Log);
        Audit = new WorkspaceAuditSink(state, ConsoleState.ServiceId);
    }

    public ConsoleWorkspaceSettings Settings { get; }

    public string Directory { get; }

    public string Version { get; }

    public Action<string> Log { get; }

    public WorkspaceStore Store { get; }

    public WorkspaceProjects Projects { get; }

    public Workspaces Workspaces { get; }

    public WorkspaceBuilds Builds { get; }

    public WorkspacePublishing Publishing { get; }

    public IAuditLog Audit { get; }

    /// <summary>The id the engine reports as its instance: the console's own, as the Workspace is part of it.</summary>
    public string InstanceId => ConsoleWorkspaceSettings.ServerId;

    public void Dispose() => Builds.Dispose();
}

/// <summary>The console's audit for the Workspace (plan 33 SC05: "the same events in the console's audit, with the module named"): an entry of the engine is an audit event whose verb starts <c>workspace.</c> and whose target is of kind <c>workspace</c>.</summary>
public sealed class WorkspaceAuditSink(StateDatabase state, string serviceId) : IAuditLog
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly AuditLog _audit = new(state, serviceId);

    public void Append(AuditEntry entry) => state.Retry(() => _audit.Record(new AuditEvent(
        "person", entry.UserId, ConsoleWorkspaceSettings.AuditPrefix + Verb(entry.Command), Word(entry.Outcome.Split(':')[0]),
        ViaKind: "console", ViaId: entry.ConsoleId, TargetKind: entry.Target is null ? null : "workspace", TargetId: entry.Target,
        Detail: System.Text.Json.JsonSerializer.Serialize(entry, Json))));

    public IReadOnlyList<AuditEntry> Read(int limit = 100)
    {
        var newest = new List<AuditEntry>();
        foreach (var record in _audit.Read(new AuditQuery(ServiceId: serviceId, VerbPrefix: ConsoleWorkspaceSettings.AuditPrefix, Limit: limit)))
            if (record.Entry.Detail is { } detail && System.Text.Json.JsonSerializer.Deserialize<AuditEntry>(detail, Json) is { } entry) newest.Add(entry);
        newest.Reverse();
        return newest;
    }

    private static string Verb(string command) => string.Join('.', command.Split('.').Select(Word));

    private static string Word(string text)
    {
        var word = new string([.. text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]).Trim('_');
        return word.Length == 0 || !char.IsAsciiLetter(word[0]) ? "x" + word : word;
    }
}
