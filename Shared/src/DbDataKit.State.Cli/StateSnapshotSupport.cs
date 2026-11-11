using System.IO.Compression;
using System.Text;
using DbDataKit.Cli;
using DbDataKit.Secrets;
using DbDataKit.State.Engines.Snapshots;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Cli;

/// <summary>
/// What the snapshot verbs (<c>state export</c>, <c>import</c>, <c>verify</c>, <c>move</c>; plan 29b X04) share: the product's state as an endpoint for <see cref="StateSnapshotService"/>, the progress lines, the
/// other side of a comparison or a move as options that name a database (a connection with secret references only), and the exit code and the one sentence for each refusal of the services.
/// The verbs work offline: the service of the product has to be stopped, which the services check (a live instance row or a SQLite file held by another process refuses with its name).
/// </summary>
internal static class StateSnapshotSupport
{
    /// <summary>The product's state, ready for the services.</summary>
    internal sealed record Opened(StateSession Session, StateEndpoint Endpoint, IReadOnlyList<StateModule> Modules, StateSnapshotService Service, ISecretResolver? Secrets);

    /// <summary>Reads the product's settings and module list. On a problem it prints the sentence and returns the exit code instead.</summary>
    public static async Task<(Opened? Opened, int Exit)> OpenAsync(StateVerbContext context, CliArgs parsed, CliContext output)
    {
        StateSession session;
        try { session = StateSession.Create(context, parsed); }
        catch (StateSettingsProblem ex)
        {
            await output.Error.WriteLineAsync(ex.Message);
            return (null, StateExit.Settings);
        }

        if (context.Modules is null)
        {
            await output.Error.WriteLineAsync($"{Capital(context.Product)} has no module list yet, so there is nothing to export, import or compare.");
            return (null, StateExit.Settings);
        }

        ISecretResolver? secrets;
        try { secrets = session.Target.Secrets?.Invoke(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await output.Error.WriteLineAsync($"The secret store could not be opened: {session.Scrub(ex.Message)}");
            return (null, StateExit.Settings);
        }

        var endpoint = new StateEndpoint(session.Settings, session.IsSqlite ? session.File : null);
        return (new Opened(session, endpoint, context.Modules, new StateSnapshotService(secrets, appliedBy: context.Program), secrets), CliExit.Ok);
    }

    public static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Progress lines per table, as the writer and the importer report them.</summary>
    public static IProgress<SnapshotProgress> Lines(TextWriter write) => new InlineProgress<SnapshotProgress>(p =>
    {
        switch (p.Phase)
        {
            case "table-done": write.WriteLine($"  {p.Module}.{p.Table}: done ({p.Rows} rows so far)"); break;
            case "done": write.WriteLine($"  all tables done ({p.Rows} rows, {p.PayloadBytes} payload bytes)"); break;
        }
    });

    /// <summary>Progress lines of a comparison: each table once.</summary>
    public static IProgress<DbDataKit.State.Verification.VerificationProgress> VerifyLines(TextWriter write)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return new InlineProgress<DbDataKit.State.Verification.VerificationProgress>(p =>
        {
            if (seen.Add($"{p.Module}.{p.Table}")) write.WriteLine($"  comparing {p.Module}.{p.Table} ({p.TablesDone}/{p.TablesTotal} tables)");
        });
    }

    /// <summary>The exit code and the one sentence for a refusal or a failure of one of the services. Messages from the services never hold a password.</summary>
    public static (int Exit, string Sentence) Describe(Exception exception) => exception switch
    {
        StateServiceException ex => (ServiceExit(ex.Reason), ex.Message),
        SnapshotRefusedException ex => (StateExit.SnapshotRefused, ex.Message),
        SnapshotImportException ex => (ImportExit(ex.Reason), ex.Message),
        StateMoveException ex => (MoveExit(ex), ex.Message),
        IOException or UnauthorizedAccessException => (StateExit.SnapshotInvalid, $"A file could not be read or written: {FirstLine(exception.Message)} Check the path and the rights of this account."),
        _ => throw new InvalidOperationException("Not a refusal of the snapshot services.", exception),
    };

    public static bool IsRefusal(Exception exception) => exception is StateServiceException or SnapshotRefusedException or SnapshotImportException or StateMoveException or IOException or UnauthorizedAccessException;

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).TrimEnd('.') + ".";
    }

    private static int ServiceExit(StateServiceRefusal reason) => reason switch
    {
        StateServiceRefusal.SqlitePathMissing or StateServiceRefusal.SecretMissing or StateServiceRefusal.SettingsInvalid => StateExit.Settings,
        StateServiceRefusal.SourceMissing or StateServiceRefusal.ConnectionFailed => StateExit.Unreachable,
        StateServiceRefusal.NotAStateDatabase or StateServiceRefusal.NeedsMigration => StateExit.NotReady,
        StateServiceRefusal.DatabaseNewer => StateExit.Refused,
        StateServiceRefusal.TargetNotEmpty => StateExit.TargetNotEmpty,
        StateServiceRefusal.SameDatabase => CliExit.Usage,
        StateServiceRefusal.SnapshotNotIntact => StateExit.VerifyDiffers,
        _ => CliExit.Failure,
    };

    private static int ImportExit(SnapshotImportRefusal reason) => reason switch
    {
        SnapshotImportRefusal.TargetNotEmpty => StateExit.TargetNotEmpty,
        SnapshotImportRefusal.TableDigestMismatch => StateExit.VerifyDiffers,
        SnapshotImportRefusal.LoadFailed or SnapshotImportRefusal.MigrationFailed or SnapshotImportRefusal.Unsupported => CliExit.Failure,
        _ => StateExit.SnapshotInvalid,
    };

    private static int MoveExit(StateMoveException ex) => ex.InnerException switch
    {
        SnapshotRefusedException => StateExit.SnapshotRefused,
        StateServiceException inner when inner.Reason is StateServiceRefusal.TargetNotEmpty => StateExit.TargetNotEmpty,
        StateServiceException inner when inner.Reason is StateServiceRefusal.SnapshotNotIntact => StateExit.VerifyDiffers,
        StateServiceException inner when inner.Reason is StateServiceRefusal.SameDatabase => CliExit.Usage,
        StateServiceException inner when inner.Reason is StateServiceRefusal.SecretMissing or StateServiceRefusal.SqlitePathMissing or StateServiceRefusal.SettingsInvalid => StateExit.Settings,
        StateServiceException inner when inner.Reason is StateServiceRefusal.NotAStateDatabase or StateServiceRefusal.NeedsMigration => StateExit.NotReady,
        _ => StateExit.MoveFailed,
    };

    /// <summary>Prints the refusal's sentence on standard error; returns its exit code.</summary>
    public static async Task<int> RefuseAsync(CliContext output, Exception exception)
    {
        var (exit, sentence) = Describe(exception);
        await output.Error.WriteLineAsync(sentence);
        output.Data["error"] = sentence;
        return exit;
    }

    // ---- the other database of a comparison or a move

    /// <summary>
    /// The database named by <c>--&lt;prefix&gt;engine</c>, <c>--&lt;prefix&gt;connection</c>, <c>--&lt;prefix&gt;schema</c>, <c>--&lt;prefix&gt;prefix</c> and (for SQLite) <c>--&lt;prefix&gt;sqlite</c>: the settings a product's <c>state</c>
    /// section holds, as options. The connection is a connection string whose credentials are secret references, never a literal password (it would be in the shell's history). The namespaces and the retention start from
    /// <paramref name="basis"/> and the schema and prefix options replace the default for every module.
    /// </summary>
    public static StateEndpoint? Other(CliArgs parsed, string prefix, StateSettings basis, string? defaultSqlite, out string? problem)
    {
        problem = null;
        var namespaces = basis.Namespaces;
        var engine = parsed.Get(prefix + "engine");
        var connection = parsed.Get(prefix + "connection");
        if (string.IsNullOrWhiteSpace(engine))
        {
            problem = $"--{prefix}engine is needed (sqlite, mssql or postgres).";
            return null;
        }

        var settings = new StateSettings(engine, connection) { Namespaces = namespaces, Retention = basis.Retention };
        try { _ = settings.EngineId; }
        catch (InvalidOperationException ex) { problem = ex.Message; return null; }

        if (!settings.IsSqlite)
        {
            if (string.IsNullOrWhiteSpace(connection)) { problem = $"--{prefix}connection is needed for the engine {engine}: a connection string whose password is a secret reference (Password=secret:state-db or Password=env:NAME)."; return null; }
            if (ConnectionStringReferences.CredentialValues(connection).Count > 0)
            {
                problem = $"--{prefix}connection holds a password written out: put it in a secret reference (Password=secret:state-db, or Password=env:NAME) so it is not in the command line or its history.";
                return null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(connection)) { problem = $"--{prefix}connection is for SQL Server and PostgreSQL; a SQLite state is a file (--{prefix}sqlite)."; return null; }

        if (parsed.Get(prefix + "schema") is { Length: > 0 } || parsed.Get(prefix + "prefix") is { Length: > 0 })
        {
            var current = namespaces.Default;
            settings = settings with
            {
                Namespaces = new StateNamespaceSettings
                {
                    Default = new NamespaceSetting(parsed.Get(prefix + "schema") ?? current.Schema, parsed.Get(prefix + "prefix") ?? current.Prefix, current.File),
                    Modules = namespaces.Modules,
                    Registry = namespaces.Registry,
                },
            };
        }

        var sqlite = parsed.Get(prefix + "sqlite") ?? defaultSqlite;
        if (settings.IsSqlite && sqlite is not { Length: > 0 }) { problem = $"--{prefix}sqlite is needed for the engine sqlite: the path of the SQLite state file."; return null; }
        return new StateEndpoint(settings, settings.IsSqlite ? Path.GetFullPath(sqlite!) : null);
    }

    // ---- secrets in a snapshot

    /// <summary>The files of the snapshot directory (table files are decompressed) that contain one of the secret values; their paths relative to the directory.</summary>
    public static List<string> FindSecrets(string directory, IReadOnlyCollection<string> secrets)
    {
        var needles = secrets.Where(s => s.Length >= 3).Select(s => Encoding.UTF8.GetBytes(s)).ToList();
        var found = new List<string>();
        if (needles.Count == 0) return found;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            using var raw = File.OpenRead(file);
            var gzip = raw.Length > 2 && raw.ReadByte() == 0x1f && raw.ReadByte() == 0x8b;
            raw.Position = 0;
            using var stream = gzip ? new GZipStream(raw, CompressionMode.Decompress) : (Stream)raw;
            if (Contains(stream, needles)) found.Add(Path.GetRelativePath(directory, file));
        }

        return found;
    }

    private static bool Contains(Stream stream, List<byte[]> needles)
    {
        var overlap = needles.Max(n => n.Length) - 1;
        var buffer = new byte[Math.Max(81920, overlap * 2 + 1)];
        var filled = 0;
        while (true)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0) return false;
            filled += read;
            foreach (var needle in needles)
                if (buffer.AsSpan(0, filled).IndexOf(needle) >= 0) return true;
            var keep = Math.Min(overlap, filled);
            buffer.AsSpan(filled - keep, keep).CopyTo(buffer);
            filled = keep;
        }
    }
}
