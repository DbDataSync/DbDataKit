using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DbData.Dialect;

/// <summary>Raised for a malformed or invalid dialect definition; carries source name and line.</summary>
public sealed class DialectException(string message, string? source = null, int line = 0) : Exception(Format(message, source, line))
{
    public string? SourceName { get; } = source;
    public int Line { get; } = line;
    private static string Format(string m, string? s, int l) => s is null ? m : l > 0 ? $"{s}:{l}: {m}" : $"{s}: {m}";
}

internal abstract record YamlNode(int Line);
internal sealed record YamlScalar(string Value, int Line) : YamlNode(Line);
internal sealed record YamlSeq(List<YamlNode> Items, int Line) : YamlNode(Line);
internal sealed record YamlMap(List<(string Key, YamlNode Value, int KeyLine)> Entries, int Line) : YamlNode(Line);

/// <summary>
/// Reads YAML into a plain tree using the parser's events only (no object deserializer, no type guessing, no
/// anchors/aliases/tags/merge keys, single document, duplicate keys rejected).
/// </summary>
internal static class YamlTree
{
    public static YamlNode Parse(string yaml, string source)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _)) throw new DialectException("document is empty", source);
            parser.Consume<DocumentStart>();
            var root = ReadNode(parser, source);
            parser.Consume<DocumentEnd>();
            if (!parser.Accept<StreamEnd>(out _)) throw new DialectException("multiple YAML documents are not allowed", source);
            return root;
        }
        catch (YamlException ex)
        {
            throw new DialectException(ex.Message, source, (int)ex.Start.Line);
        }
    }

    private static YamlNode ReadNode(Parser p, string source)
    {
        var ev = p.Current ?? throw new DialectException("unexpected end of document", source);
        int line = (int)ev.Start.Line;
        switch (ev)
        {
            case Scalar s:
                if (!s.Tag.IsEmpty || !s.Anchor.IsEmpty) throw new DialectException("YAML tags and anchors are not allowed", source, line);
                p.MoveNext();
                return new YamlScalar(s.Value, line);
            case SequenceStart sq:
                if (!sq.Tag.IsEmpty || !sq.Anchor.IsEmpty) throw new DialectException("YAML tags and anchors are not allowed", source, line);
                p.MoveNext();
                var items = new List<YamlNode>();
                while (p.Current is not SequenceEnd) items.Add(ReadNode(p, source));
                p.MoveNext();
                return new YamlSeq(items, line);
            case MappingStart ms:
                if (!ms.Tag.IsEmpty || !ms.Anchor.IsEmpty) throw new DialectException("YAML tags and anchors are not allowed", source, line);
                p.MoveNext();
                var entries = new List<(string, YamlNode, int)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (p.Current is not MappingEnd)
                {
                    if (p.Current is not Scalar key) throw new DialectException("mapping keys must be plain scalars", source, (int)p.Current!.Start.Line);
                    p.MoveNext();
                    if (key.Value == "<<") throw new DialectException("merge keys are not allowed", source, (int)key.Start.Line);
                    if (!seen.Add(key.Value)) throw new DialectException($"duplicate key '{key.Value}'", source, (int)key.Start.Line);
                    entries.Add((key.Value, ReadNode(p, source), (int)key.Start.Line));
                }
                p.MoveNext();
                return new YamlMap(entries, line);
            case AnchorAlias:
                throw new DialectException("YAML aliases are not allowed", source, line);
            default:
                throw new DialectException($"unexpected YAML construct {ev.GetType().Name}", source, line);
        }
    }
}

/// <summary>Strict accessor over a map: every key must be taken or the caller reports the unknown ones.</summary>
internal sealed class Fields
{
    private readonly YamlMap _map;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly string _source;
    public string Path { get; }

    public Fields(YamlNode node, string source, string path)
    {
        _source = source;
        Path = path;
        _map = node as YamlMap ?? throw new DialectException($"'{path}' must be a mapping", source, node.Line);
    }

    public int Line => _map.Line;

    public DialectException Error(string message, int line = 0) => new($"{Path}: {message}", _source, line == 0 ? _map.Line : line);

    public YamlNode? Get(string key)
    {
        foreach (var (k, v, _) in _map.Entries)
            if (k == key) { _used.Add(k); return v; }
        return null;
    }

    public string? Str(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        return n is YamlScalar s ? s.Value : throw Error($"'{key}' must be a scalar", n.Line);
    }

    public int? Int(string key)
    {
        var s = Str(key);
        if (s is null) return null;
        return int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int v)
            ? v : throw Error($"'{key}' must be an integer, got '{s}'", Get(key)!.Line);
    }

    public List<string>? StrList(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        if (n is not YamlSeq seq) throw Error($"'{key}' must be a list", n.Line);
        return seq.Items.Select(i => i is YamlScalar s ? s.Value : throw Error($"'{key}' entries must be scalars", i.Line)).ToList();
    }

    public Fields? Map(string key)
    {
        var n = Get(key);
        return n is null ? null : new Fields(n, _source, $"{Path}.{key}");
    }

    public List<Fields>? MapList(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        if (n is not YamlSeq seq) throw Error($"'{key}' must be a list", n.Line);
        return seq.Items.Select((i, idx) => new Fields(i, _source, $"{Path}.{key}[{idx}]")).ToList();
    }

    public void EnsureNoUnknown()
    {
        foreach (var (k, _, line) in _map.Entries)
            if (!_used.Contains(k)) throw Error($"unknown key '{k}'", line);
    }
}
