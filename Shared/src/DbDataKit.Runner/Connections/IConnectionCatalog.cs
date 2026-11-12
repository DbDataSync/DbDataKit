using DbDataKit.Connections;
using DbDataKit.State;

namespace DbDataKit.Runner.Connections;

/// <summary>The connections this runner knows: the <c>connection/1</c> documents a channel's slot binding names (plan 28c section 6). A document holds secret <i>references</i>, never values.</summary>
public interface IConnectionCatalog
{
    /// <summary>The definition named <paramref name="name"/>; null when the runner has none. A document that exists and does not load throws <see cref="InvalidDataException"/> naming the file.</summary>
    ConnectionDefinition? Find(string name);

    /// <summary>The text the definition was loaded from (secret references, never a value), for a task that makes its own client from it (an <c>http</c> slot, plan 27a IX10); null when the catalog keeps none.</summary>
    string? Text(string name) => null;
}

/// <summary>
/// The connections the console handed over (<c>connection_copy</c>, plan 27a IX23): the definition the console sent with secret references, never a value, read from the runner's own state, so the runner never reads the console's folder. A copy that
/// does not load throws <see cref="InvalidDataException"/> naming it, as a file would.
/// </summary>
public sealed class StoredConnectionCatalog(ConnectionCopies copies) : IConnectionCatalog
{
    public ConnectionDefinition? Find(string name)
    {
        if (copies.Get(name) is not { } copy) return null;
        var load = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(copy.Definition, $"the copy of the connection '{name}' (sent by {copy.SentBy})");
        if (!load.Ok)
            throw new InvalidDataException($"The copy of the connection '{name}' does not load: {string.Join(" ", load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString()))} Send it again from the console's Bindings page.");
        if (!string.Equals(load.Definition!.Name, name, StringComparison.Ordinal))
            throw new InvalidDataException($"The copy of the connection '{name}' names the connection '{load.Definition.Name}'.");
        return load.Definition;
    }

    public string? Text(string name) => copies.Get(name)?.Definition;
}

/// <summary>The first catalog that has the name: an operator's own file in the runner's connections directory wins over a copy the console sent (a file on this machine is the stronger statement), and a name in neither is unknown.</summary>
public sealed class LayeredConnectionCatalog(params IConnectionCatalog[] layers) : IConnectionCatalog
{
    public ConnectionDefinition? Find(string name)
    {
        foreach (var layer in layers)
            if (layer.Find(name) is { } found) return found;
        return null;
    }

    public string? Text(string name)
    {
        foreach (var layer in layers)
            if (layer.Find(name) is not null) return layer.Text(name);
        return null;
    }
}

/// <summary>A catalog in memory, for tests and for hosts that build their connections in code.</summary>
public sealed class InMemoryConnectionCatalog(IEnumerable<ConnectionDefinition> definitions) : IConnectionCatalog
{
    private readonly Dictionary<string, ConnectionDefinition> _byName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);

    public ConnectionDefinition? Find(string name) => _byName.GetValueOrDefault(name);
}

/// <summary>
/// A directory of <c>connection/1</c> files, <c>&lt;name&gt;.yaml</c> (or <c>.yml</c>), read at each lookup so a change to a file is seen by the next run. The name is a plain file name: one with a path separator or a dot-dot finds nothing.
/// </summary>
public sealed class DirectoryConnectionCatalog(string directory) : IConnectionCatalog
{
    public string Directory { get; } = directory;

    public ConnectionDefinition? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..", StringComparison.Ordinal)) return null;
        foreach (var extension in new[] { ".yaml", ".yml" })
        {
            var path = Path.Combine(Directory, name + extension);
            if (!File.Exists(path)) continue;
            var load = ConnectionDefinitionLoader.LoadDatabaseFile(path);
            if (!load.Ok)
                throw new InvalidDataException($"The connection file '{path}' does not load: {string.Join(" ", load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString()))}");
            if (!string.Equals(load.Definition!.Name, name, StringComparison.Ordinal))
                throw new InvalidDataException($"The connection file '{path}' names the connection '{load.Definition.Name}'; the file's name and the connection's name have to be the same.");
            return load.Definition;
        }

        return null;
    }
}
