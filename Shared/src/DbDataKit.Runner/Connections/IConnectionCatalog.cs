using DbDataKit.Connections;

namespace DbDataKit.Runner.Connections;

/// <summary>The connections this runner knows: the <c>connection/1</c> documents a channel's slot binding names (plan 28c section 6). A document holds secret <i>references</i>, never values.</summary>
public interface IConnectionCatalog
{
    /// <summary>The definition named <paramref name="name"/>; null when the runner has none. A document that exists and does not load throws <see cref="InvalidDataException"/> naming the file.</summary>
    ConnectionDefinition? Find(string name);
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
            var load = ConnectionDefinitionLoader.LoadFile(path);
            if (!load.Ok)
                throw new InvalidDataException($"The connection file '{path}' does not load: {string.Join(" ", load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString()))}");
            if (!string.Equals(load.Definition!.Name, name, StringComparison.Ordinal))
                throw new InvalidDataException($"The connection file '{path}' names the connection '{load.Definition.Name}'; the file's name and the connection's name have to be the same.");
            return load.Definition;
        }

        return null;
    }
}
