using System.Text;
using DbDataKit.State;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The data sources a client publishes to a group (plan 20 O04b): kept as the published definition <c>data-source/&lt;name&gt;</c> in the state the group shares (the whole <c>connection/1</c> file as its content), so a
/// data source a Build server makes through one instance is served by every one. Each instance writes the files it serves to <c>group/connections/</c> of its data directory, which the configuration reads after its own
/// <c>connections/</c> (a hand-made data source of the same name wins); a version that is not a valid data source leaves the file it replaces. A data source made by hand stays the instance's own configuration.
/// </summary>
public sealed class PublishedDataSources(DefinitionStore store, StateDatabase state, string dataDirectory, Action reload, Action<string>? problem = null) : IDefinitionSubscriber
{
    public const string DefinitionKind = "data-source";

    private readonly object _gate = new();

    public string Kind => DefinitionKind;

    /// <summary>Format 1 and format 2 of <c>connection</c> (the sections <c>cache</c> and <c>checks</c>, plan 24a UC03).</summary>
    public DefinitionFormats Formats => DefinitionFormats.Of(DbDataKit.Connections.ConnectionDefinition.CurrentVersion);

    /// <summary>Whether this instance is one of a group (a SQL Server or PostgreSQL state); one alone keeps every data source in its configuration.</summary>
    public bool Enabled => GatewayState.IsGroup(state);

    public static string FolderIn(string dataDirectory) => Path.Combine(dataDirectory, "group", "connections");

    private string FileOf(string name) => Path.Combine(FolderIn(dataDirectory), name + ".yaml");

    /// <summary>The file the group serves under the name; null when it serves none.</summary>
    public string? Text(string name) => Enabled ? store.Current(DefinitionKind, name)?.Content : null;

    /// <summary>Every data source the group serves, by name.</summary>
    public IReadOnlyList<(string Name, string Text)> All() => Enabled ? [.. store.CurrentOfKind(DefinitionKind).Select(v => (v.Name, v.Content))] : [];

    /// <summary>Makes <paramref name="text"/> the file the group serves under the name, and serves it here at once; the other instances serve it when they next look.</summary>
    public void Publish(string name, string text, string by)
    {
        // the format is the file's own (`schema: connection/<n>`): 2 when it has a cache or checks section (24a UC03), and a publish is refused while a live instance of the group cannot read it
        store.Publish(DefinitionKind, name, text, by, format: DbDataKit.Connections.ConnectionDefinitionLoader.FormatOfText(text));
        ServeNow(name);
    }

    /// <summary>Stops the group serving the data source (its versions are kept), here at once.</summary>
    public void Retire(string name, string by)
    {
        store.Retire(DefinitionKind, name, by);
        ServeNow(name);
    }

    /// <summary>The follower's call: writes the file the group now serves under the name (or removes it), and reads the configuration again. A file that is not a data source leaves the one served.</summary>
    public void Apply(string name, DefinitionVersion? current)
    {
        lock (_gate)
        {
            var file = FileOf(name);
            if (current is null)
            {
                if (!File.Exists(file)) return;
                File.Delete(file);
                reload();
                return;
            }
            var loaded = DbDataKit.Connections.ConnectionDefinitionLoader.Load(current.Content, file);
            if (!loaded.Ok)
            {
                var message = $"Published data source '{name}' version {current.Version} is not a valid data source, so {(File.Exists(file) ? "the version served before stays" : "it is not served")} here: {string.Join("; ", loaded.Diagnostics.Select(d => d.Message))}";
                problem?.Invoke(message);
                throw new InvalidDataException(message);
            }
            if (File.Exists(file) && File.ReadAllText(file) == current.Content) return;
            Directory.CreateDirectory(FolderIn(dataDirectory));
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(temp, current.Content, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
            reload();
        }
    }

    /// <summary>Serves what the group serves under the name on this instance now (after a change made here). A version that is not a data source is reported and the file served stays.</summary>
    public void ServeNow(string name)
    {
        try { Apply(name, store.Current(DefinitionKind, name)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* reported by Apply */ }
    }

    /// <summary>Removes the files of data sources the group no longer serves (retired while this instance was stopped). Called before the follower starts.</summary>
    public void Reconcile()
    {
        if (!Enabled || !Directory.Exists(FolderIn(dataDirectory))) return;
        var served = store.CurrentOfKind(DefinitionKind).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var removed = false;
        foreach (var file in Directory.EnumerateFiles(FolderIn(dataDirectory), "*.yaml").Where(f => !served.Contains(Path.GetFileNameWithoutExtension(f))))
        {
            File.Delete(file);
            removed = true;
        }
        if (removed) reload();
    }
}
