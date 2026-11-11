using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Authoring;

namespace DbDataKit.Authoring.Server;

/// <summary>The program of the Sync product the Workspace runs as a child process (<see cref="ChildProgram"/>): <c>dbdatasync</c>. Its commands take their folder as <c>--repo</c> and name their JSON form <c>--json</c>, so no argument follows them.</summary>
public sealed class SyncTool(string? executable = null, TimeSpan? timeout = null) : ChildProgram("dbdatasync", executable, timeout, Missing, [])
{
    public const string Missing = "This server cannot work on a Sync project: the program dbdatasync is not beside it or on its PATH. Install Sync (the suite package has it) and restart the Workspace server.";
}

/// <summary>
/// The <c>sync</c> kind (plan 33 SC22a): a Sync replication set authored as files in a git workspace, in the layout Sync's own repository keeps (<c>config/replications/&lt;name&gt;/task.yaml</c>, <c>table-mappings/</c>, <c>config/connections/</c>, the
/// <c>config/scripts/</c> and <c>libraries/</c> a replication uses), so a Sync repository on this machine is hosted in place with <c>project add</c>. The Workspace does not reference Sync (a product is not a shared library). A draft is checked in two steps:
/// first by this class, which refuses what must never reach git (a password in a connection file, anything under <c>secrets/</c>) whatever the program says, then by <c>dbdatasync package publish --to runner --check --json</c>, which reads every file with the
/// strict readers the engine itself uses, offline and writing nothing, and refuses what would fail at the first run (a bad schedule, a mapping with no column cache). The program runs with a cleared environment in the draft's folder, a time limit and no
/// secret store of a Sync server in reach. The same program writes the package (<see cref="ProduceAsync"/>, SC22b).
/// </summary>
public sealed partial class SyncProjectKind(SyncTool tool) : IProjectKind
{
    public string Name => "sync";
    public string Title => "Sync replication set";
    public string Description => "Replications of dbdatasync: tasks, table mappings with their column cache, connection references and the scripts and libraries they use. Checked with Sync's own readers; published to a Runner as a package.";
    public bool Buildable => false;
    public string? GitIgnore => "secrets/\nstate/\nscript-cache/\nnode_modules/\n**/lib/\n";

    public IReadOnlyList<ProjectPublishRoute> Publishes { get; } =
        [new("runner", "package", "The replications as a definition package: one task per replication with its schedule and connection slots, staged on a channel of a Runner, which runs them from the connections bound there.")];

    public bool Produces => true;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    public IReadOnlyList<ProjectKindFile> Template(string id, string? title)
    {
        static ProjectKindFile File(string path, string text) => new(path, Utf8.GetBytes(text.Replace("\r\n", "\n")));
        static string Connection(string name, string engine, string host, string database) => string.Join("\n",
            "schema: connection/1", $"name: {name}", $"engine: {engine}", "address:", $"  host: {host}", $"  database: {database}", "auth:", "  mode: sql", "  user: sync", $"  password_secret: secret:dbdata:sync:connection:{name}", "");
        var task = string.Join("\n",
            "schemaVersion: 2", "name: orders", "scheduling:", "  mode: Periodic", "  cronExpression: '*/15 * * * *'",
            "changeProcessing:", "  reader:", "    kind: MsSqlChangeTracking", "    options: {}", "  cache:", "    kind: MsSqlStagingTable", "    options: {}", "  writer:", "    kind: PostgresMerge", "    options: {}",
            "bulkLoad:", "  reader:", "    kind: BatchReload", "    options: {}", "endpoints: {}", "scripts: {}", "hooks: {}", "provisioning: {}",
            "reconcile:", "  afterChange:", "    mode: none", "  deleteGuard:", "    mode: ratio", "    maxRatio: 0.5", "segmentingStrategies: []", "");
        var mapping = string.Join("\n",
            "schemaVersion: 1", "name: orders", "sources:", "- allowSubquery: true", "  connectionName: source-db", "  database: App", "  schema: dbo", "  table: Orders",
            "targets:", "- connectionName: target-db", "  database: dw", "  schema: public", "  table: orders",
            "columnMappings: []", "relationships: []", "scripts: {}", "hooks: {}", "provisioning: {}", "verification: []", "defaultSegmenting: []", "primaryKey: []",
            "sourceColumns:", "- name: id", "  nativeType: bigint", "  isPrimaryKey: true", "  keyOrdinal: 1", "targetColumns:", "- name: id", "  nativeType: bigint", "  isPrimaryKey: true", "  keyOrdinal: 1", "relationshipColumns: {}", "");
        var readme = $"# {title ?? id}\n\nA replication set of Sync. `config/replications/<name>/task.yaml` is a replication, `table-mappings/` its mappings (with the cache of the columns they read and write), `config/connections/` the connections by reference:\n"
            + "a connection holds the name of its secret (`password_secret: secret:dbdata:sync:connection:<name>`), never a password. Edit in a workspace, check, merge, publish to a Runner; the connections are bound there.\n";
        return
        [
            File("config/connections/source-db.yaml", Connection("source-db", "sqlserver", "source.example.com", "App")),
            File("config/connections/target-db.yaml", Connection("target-db", "postgres", "target.example.com", "dw")),
            File("config/replications/orders/task.yaml", task),
            File("config/replications/orders/table-mappings/orders.yaml", mapping),
            File("README.md", readme),
        ];
    }

    [GeneratedRegex(@"^\s*password\s*:\s*(?<value>\S.*)$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PasswordKey();

    [GeneratedRegex(@"^\s*password_secret\s*:\s*['""]?(?<value>[^'""\s]*)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PasswordSecretKey();

    [GeneratedRegex(@"(?:password|pwd)\s*=\s*(?<value>[^;'""\s]+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PasswordInString();

    /// <summary>What must never reach git, found by looking at the files themselves: a password where a reference belongs (any of the three ways a connection can carry one) and a file under <c>secrets/</c>.</summary>
    internal static List<ProjectDiagnostic> SecretFindings(string root)
    {
        var found = new List<ProjectDiagnostic>();
        foreach (var top in new[] { "secrets", "config/secrets" })
        {
            var folder = Path.Combine(root, top.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
                found.Add(new("error", "DBD-WS-SYNC-SECRET", $"{top}/ holds files: the secrets of a Sync server are not part of a project and are never committed. Remove them; a connection names its secret by reference.", top + "/"));
        }
        var connections = Path.Combine(root, "config", "connections");
        if (!Directory.Exists(connections)) return found;
        foreach (var file in Directory.EnumerateFiles(connections, "*.y*ml", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var relative = "config/connections/" + Path.GetFileName(file);
            string[] lines;
            try { lines = File.ReadAllLines(file, Utf8); }
            catch (Exception ex) when (ex is IOException or DecoderFallbackException) { found.Add(new("error", "DBD-WS-SYNC-READ", $"{relative} cannot be read: {ex.Message}", relative)); continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith('#')) continue;
                string? why = null;
                if (PasswordKey().IsMatch(line)) why = "holds a password value under `password`";
                else if (PasswordSecretKey().Match(line) is { Success: true } s && s.Groups["value"].Value is { Length: > 0 } reference && !reference.StartsWith("secret:", StringComparison.Ordinal)) why = "has a `password_secret` that is not a secret reference (`secret:dbdata:sync:connection:<name>`)";
                else if (line.Contains("connection_string", StringComparison.OrdinalIgnoreCase) && PasswordInString().Match(line) is { Success: true } p
                    && !p.Groups["value"].Value.StartsWith("secret:", StringComparison.Ordinal) && !p.Groups["value"].Value.StartsWith("env:", StringComparison.Ordinal)) why = "has a password in its connection string";
                if (why is not null) found.Add(new("error", "DBD-WS-SYNC-SECRET", $"{relative} {why}. A connection names its secret by reference and the value is set on the server that runs it; a password in a file would reach git.", relative, i + 1));
            }
        }
        return found;
    }

    [GeneratedRegex(@"^(?<file>.+?\.(?:ya?ml|csx|sql|json|lock))(?::(?<line>\d+))?(?::\d+)?:\s*(?<message>.*)$", RegexOptions.NonBacktracking)]
    private static partial Regex Located();

    /// <summary>The words a program printed on its error output as findings: <c>/abs/config/replications/x/task.yaml: message</c> becomes the file relative to the draft and the message; a line that names no file is a finding without one.</summary>
    internal static List<ProjectDiagnostic> FindingsOf(string root, string error, string code)
    {
        var found = new List<ProjectDiagnostic>();
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var raw in error.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line is "Nothing was sent." or "Nothing more was done.") continue;
            if (Located().Match(line) is { Success: true } m)
            {
                var file = m.Groups["file"].Value;
                if (file.StartsWith(prefix, StringComparison.Ordinal)) file = file[prefix.Length..];
                file = file.Replace('\\', '/').TrimStart('/');
                found.Add(new("error", code, m.Groups["message"].Value.Trim(), file, m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null));
            }
            else found.Add(new("error", code, line));
        }
        return found;
    }

    public async Task<IReadOnlyList<ProjectDiagnostic>> CheckAsync(string root, CancellationToken cancellationToken)
    {
        var found = SecretFindings(root);
        if (tool.Find() is null) { found.Add(new("error", "DBD-WS-SYNC-MISSING", SyncTool.Missing)); return found; }
        (int Exit, string Output, string Error) result;
        try { result = await tool.RunAsync(["package", "publish", "--to", "runner", "--check", "--json", "--repo", root], root, cancellationToken); }
        catch (ProjectKindUnavailableException ex) { found.Add(new("error", "DBD-WS-SYNC-MISSING", ex.Message)); return found; }
        if (result.Exit == 0)
        {
            try { using var document = JsonDocument.Parse(result.Output); }
            catch (JsonException) { found.Add(new("error", "DBD-WS-SYNC-FAILED", $"dbdatasync did not answer with a document (exit code {result.Exit}): {result.Error.Trim()}")); }
            return found;
        }
        var said = FindingsOf(root, result.Error, "DBD-WS-SYNC");
        found.AddRange(said.Count > 0 ? said : [new("error", "DBD-WS-SYNC-FAILED", $"dbdatasync stopped with exit code {result.Exit}: {result.Error.Trim()}")]);
        return found;
    }

    public async Task<ProjectArtifact> ProduceAsync(string root, string projectId, string outputFolder, CancellationToken cancellationToken)
    {
        var package = PackageArtifacts.NameOf(projectId);
        (int Exit, string Output, string Error) result;
        try { result = await tool.RunAsync(["package", "publish", "--to", "runner", "--out", outputFolder, "--json", "--package", package, "--repo", root], root, cancellationToken); }
        catch (ProjectKindUnavailableException ex) { return new(false, ex.Message, [new("error", "DBD-WS-SYNC-MISSING", ex.Message)]); }
        if (result.Exit != 0)
        {
            var said = FindingsOf(root, result.Error, "DBD-WS-SYNC");
            return new(false, said.FirstOrDefault()?.Message ?? $"dbdatasync stopped with exit code {result.Exit}.", said.Count > 0 ? said : [new("error", "DBD-WS-SYNC-FAILED", $"dbdatasync stopped with exit code {result.Exit}: {result.Error.Trim()}")]);
        }
        return PackageArtifacts.FromOutput(package, outputFolder, result.Output, "replications");
    }
}
