namespace DbDataBuild.Models.Yaml;

/// <summary>Node of the strict YAML subset. All scalars are strings; every node carries its source position (1-based).</summary>
public abstract record YamlNode(int Line, int Column)
{
    /// <summary>Character offset where the node starts in the source text.</summary>
    public int Start { get; init; }

    /// <summary>Character offset just past the node's last character. For block collections this is the end of the last child.</summary>
    public int End { get; init; }

    /// <summary>True for flow style (<c>[a, b]</c>, <c>{a: 1}</c>), false for block style and scalars.</summary>
    public bool Flow { get; init; }
}

public sealed record YamlScalar(string Value, int Line, int Column) : YamlNode(Line, Column)
{
    /// <summary>Written in quotes (or as a block scalar). Values are always read as strings; this only lets a file tell a plain `null` from the text "null".</summary>
    public bool Quoted { get; init; }
}

public sealed record YamlSequence(IReadOnlyList<YamlNode> Items, int Line, int Column) : YamlNode(Line, Column);

public sealed record YamlEntry(YamlScalar Key, YamlNode Value);

public sealed record YamlMapping(IReadOnlyList<YamlEntry> Entries, int Line, int Column) : YamlNode(Line, Column)
{
    public YamlNode? Get(string key) => Entries.FirstOrDefault(e => e.Key.Value == key)?.Value;
}
