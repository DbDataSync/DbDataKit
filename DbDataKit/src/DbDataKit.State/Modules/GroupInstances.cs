using System.Data.Common;
using System.Globalization;

namespace DbDataKit.State;

/// <summary>
/// One live instance of a group as its instance row says (plan 29b U02): who it is, and the software and module versions its binary has. The versions are null for a row written before the
/// product recorded them (the Proxy's and the runner's rows gained the columns in their second migration).
/// </summary>
/// <param name="Kind">The module that owns the row: <c>serving</c> (the Proxy's) or <c>runner</c>.</param>
/// <param name="Id">The row's key (an instance key or an instance id).</param>
/// <param name="Host">The machine, when known.</param>
/// <param name="ProcessId">The process id on that machine, when the row has one.</param>
/// <param name="Started">When it started.</param>
/// <param name="Heartbeat">When it last refreshed its row.</param>
/// <param name="SoftwareVersion">The product's version, or null when the row records none.</param>
/// <param name="ModuleVersions">Each module of the instance's binary and the version it has; null when the row records none.</param>
public sealed record GroupInstance(string Kind, string Id, string? Host, int? ProcessId, DateTimeOffset? Started, DateTimeOffset? Heartbeat, string? SoftwareVersion, IReadOnlyDictionary<string, int>? ModuleVersions);

/// <summary>What a <see cref="IGroupInstanceSource"/> reads with: the connection (and the migration's transaction, which a read must join), the layout, the time, and the version the registry holds for a module.</summary>
public sealed record GroupInstanceContext(DbConnection Connection, DbTransaction? Transaction, StateLayout Layout, DateTimeOffset Now, Func<string, int?> RegisteredVersion);

/// <summary>
/// The seam between the migrator and the products' instance tables (plan 29b U02): before a migration is applied to a group's database, the migrator asks the live instances and refuses when one cannot work with the
/// new schema. The product supplies the source when it opens its state (<see cref="StateLayout.Create"/>); the default is none, which never refuses.
/// </summary>
public interface IGroupInstanceSource
{
    /// <summary>The instances that are live now (a row whose heartbeat lapsed is not one). Reads only; runs on the migrator's connection, under the migration lock.</summary>
    IReadOnlyList<GroupInstance> Live(GroupInstanceContext context);
}

/// <summary>A migration was not applied because a live instance cannot work with the schema it would make. The message names the instance and the fix.</summary>
public sealed class MigrationRefusedException(string message, GroupInstance instance, string module, int from, int to) : InvalidOperationException(message)
{
    public GroupInstance Instance { get; } = instance;
    public string Module { get; } = module;
    public int From { get; } = from;
    public int To { get; } = to;
}

/// <summary>The text an instance row keeps its module versions as: <c>name=version;name=version</c>, sorted by name.</summary>
public static class ModuleVersionText
{
    public static string Format(IEnumerable<StateModule> modules) =>
        string.Join(';', modules.Select(m => (m.Name, m.Version)).Distinct().OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => $"{m.Name}={m.Version.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>The module versions of the layout's binary.</summary>
    public static string Format(StateLayout layout) => Format(layout.Namespaces.Select(n => n.Module));

    /// <summary>Reads the text back; null for no text. A part that is not <c>name=number</c> is skipped, so a newer format never stops an older reader.</summary>
    public static IReadOnlyDictionary<string, int>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && int.TryParse(part[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)) result[part[..at].Trim()] = version;
        }

        return result;
    }
}

/// <summary>
/// Reads the two instance tables the suite has, the Proxy's <c>serving_instance</c> (module <c>serving</c>) and the runner's <c>instance</c> (module <c>runner</c>), for whichever of the modules the layout has. The
/// versions are read when the module's registry version is at least the one of the migration that added the columns (3 for serving, 2 for runner); before that the rows are returned without versions. Used by the products as their
/// <see cref="IGroupInstanceSource"/>, by the status API and by <c>state show</c>.
/// </summary>
public sealed class StandardGroupInstanceSource(TimeSpan? runnerLease = null) : IGroupInstanceSource
{
    /// <summary>A serving instance's row is live for this long after its last heartbeat (the Proxy's lease).</summary>
    public static readonly TimeSpan ServingLease = TimeSpan.FromSeconds(30);

    /// <summary>The module versions whose migration added <c>software_version</c> and <c>module_versions</c> (the Proxy's third, the runner's second).</summary>
    private const int ServingVersionColumnsFrom = 3;
    private const int RunnerVersionColumnsFrom = 2;

    private readonly TimeSpan _runnerLease = runnerLease ?? TimeSpan.FromSeconds(90);

    public IReadOnlyList<GroupInstance> Live(GroupInstanceContext context)
    {
        var list = new List<GroupInstance>();
        if (context.Layout.Has("serving") && context.RegisteredVersion("serving") is >= 1) list.AddRange(ReadServing(context));
        if (context.Layout.Has("runner") && context.RegisteredVersion("runner") is >= 1) list.AddRange(ReadRunner(context));
        return list;
    }

    private static List<GroupInstance> ReadServing(GroupInstanceContext c)
    {
        var withVersions = c.RegisteredVersion("serving") >= ServingVersionColumnsFrom;
        var rows = new List<GroupInstance>();
        using var reader = Query(c, "serving", $"SELECT instance_key, host, process_id, started_at, heartbeat_at{(withVersions ? ", software_version, module_versions" : "")} FROM {{table:serving_instance}};");
        while (reader.Read())
        {
            var host = reader.GetString(1);
            var pid = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            var heartbeat = reader.Time(4);
            if (c.Now - heartbeat >= ServingLease || (host == Environment.MachineName && !Running(pid))) continue;
            rows.Add(new GroupInstance("serving", reader.GetString(0), host, pid, reader.Time(3), heartbeat,
                withVersions && !reader.IsDBNull(5) ? reader.GetString(5) : null, withVersions && !reader.IsDBNull(6) ? ModuleVersionText.Parse(reader.GetString(6)) : null));
        }

        return rows;
    }

    private List<GroupInstance> ReadRunner(GroupInstanceContext c)
    {
        var withVersions = c.RegisteredVersion("runner") >= RunnerVersionColumnsFrom;
        var rows = new List<GroupInstance>();
        using var reader = Query(c, "runner", $"SELECT id, host, version, started_at, heartbeat_at, state{(withVersions ? ", software_version, module_versions" : "")} FROM {{table:instance}};");
        while (reader.Read())
        {
            var heartbeat = reader.Time(4);
            if (reader.GetString(5) != RunnerInstance.Running || c.Now - heartbeat >= _runnerLease) continue;
            var software = withVersions && !reader.IsDBNull(6) ? reader.GetString(6) : reader.GetString(2);
            rows.Add(new GroupInstance("runner", reader.Uuid(0).ToString(), reader.GetString(1), null, reader.Time(3), heartbeat,
                software, withVersions && !reader.IsDBNull(7) ? ModuleVersionText.Parse(reader.GetString(7)) : null));
        }

        return rows;
    }

    private static DbDataReader Query(GroupInstanceContext c, string module, string sql)
    {
        var command = c.Connection.CreateCommand();
        command.Transaction = c.Transaction;
        command.CommandText = StateTemplates.Render(c.Layout.Render(sql, module), StateDialect.For(c.Layout.Engine));
        return command.ExecuteReader();
    }

    private static bool Running(int processId)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
