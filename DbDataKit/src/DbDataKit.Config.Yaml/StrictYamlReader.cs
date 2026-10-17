using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DbDataKit.Config.Yaml;

/// <summary>
/// Reads the strict YAML subset: only the parser's events are used (never an object deserializer), scalars stay strings,
/// duplicate keys are errors, and anchors, aliases, merge keys, tags and multiple documents are rejected.
/// Problems are reported as diagnostics, never thrown.
/// </summary>
public static class StrictYamlReader
{
    /// <returns>The root node, or null for an empty file or when the file could not be read (see diagnostics).</returns>
    public static YamlNode? Read(string text, string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog = null)
    {
        var cx = new Cx(file, diagnostics, catalog ?? DefaultDiagnosticCatalog.Instance);
        Parser? parser = null;
        try
        {
            parser = new Parser(new StringReader(text));
            parser.Consume<StreamStart>();
            if (parser.TryConsume<StreamEnd>(out _)) return null; // empty file
            parser.Consume<DocumentStart>();
            var root = ReadNode(parser, cx);
            parser.Consume<DocumentEnd>();
            if (!parser.Accept<StreamEnd>(out _))
            {
                cx.Add(SharedCodes.YamlSyntax, parser.Current!.Start, "The file contains more than one YAML document.");
                return null;
            }
            return root;
        }
        catch (YamlException ex)
        {
            cx.Add(SharedCodes.YamlSyntax, ex.Start, ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or EndOfStreamException or ArgumentException or IndexOutOfRangeException or InvalidCastException)
        {
            // YamlDotNet's scanner can throw these (not YamlException) on some malformed input; a truncated file reaches the end of the event stream. Position is best effort.
            cx.Add(SharedCodes.YamlSyntax, parser?.Current?.Start ?? new Mark(0, 1, 1), "The YAML scanner could not make sense of this region (malformed flow collection or scalar).");
            return null;
        }
    }

    private sealed class Cx(string file, List<ConfigDiagnostic> diags, IDiagnosticCatalog catalog)
    {
        public void Add(string code, Mark at, string message) => diags.Add(catalog.Create(code, file, (int)at.Line, (int)at.Column, message));
        public void Add(string code, int line, int column, string message) => diags.Add(catalog.Create(code, file, line, column, message));
    }

    private static YamlNode? ReadNode(Parser p, Cx cx)
    {
        switch (p.Current)
        {
            case Scalar s:
                p.MoveNext();
                CheckProperties(s.Anchor, s.Tag, s.Start, cx);
                return new YamlScalar(s.Value, L(s.Start), C(s.Start)) { Start = (int)s.Start.Index, End = (int)s.End.Index, Quoted = s.Style is not (ScalarStyle.Plain or ScalarStyle.Any) };

            case AnchorAlias a:
                p.MoveNext();
                cx.Add(SharedCodes.UnsupportedFeature, a.Start, $"Alias `*{a.Value}` was found.");
                return null;

            case SequenceStart seq:
                p.MoveNext();
                CheckProperties(seq.Anchor, seq.Tag, seq.Start, cx);
                var items = new List<YamlNode>();
                while (!p.Accept<SequenceEnd>(out _))
                {
                    var item = ReadNode(p, cx);
                    if (item != null) items.Add(item);
                }
                var seqClose = (SequenceEnd)p.Current!;
                p.MoveNext();
                var seqFlow = seq.Style == SequenceStyle.Flow;
                return new YamlSequence(items, L(seq.Start), C(seq.Start))
                {
                    Start = (int)seq.Start.Index, Flow = seqFlow,
                    End = seqFlow ? (int)seqClose.Start.Index + 1 : items.Count > 0 ? items.Max(i => i.End) : (int)seq.Start.Index,
                };

            case MappingStart map:
                p.MoveNext();
                CheckProperties(map.Anchor, map.Tag, map.Start, cx);
                var entries = new List<YamlEntry>();
                var seen = new HashSet<string>();
                while (!p.Accept<MappingEnd>(out _))
                {
                    var keyNode = ReadNode(p, cx);
                    var value = ReadNode(p, cx);
                    if (keyNode is not YamlScalar key)
                    {
                        if (keyNode != null) cx.Add(SharedCodes.YamlSyntax, keyNode.Line, keyNode.Column, "A mapping key must be a plain string.");
                        continue;
                    }
                    if (key.Value == "<<")
                    {
                        cx.Add(SharedCodes.UnsupportedFeature, key.Line, key.Column, "Merge key `<<` was found.");
                        continue;
                    }
                    if (!seen.Add(key.Value))
                    {
                        cx.Add(SharedCodes.DuplicateKey, key.Line, key.Column, $"Key `{key.Value}` appears more than once in this mapping.");
                        continue;
                    }
                    if (value != null) entries.Add(new YamlEntry(key, value));
                }
                var mapClose = (MappingEnd)p.Current!;
                p.MoveNext();
                var mapFlow = map.Style == MappingStyle.Flow;
                return new YamlMapping(entries, L(map.Start), C(map.Start))
                {
                    Start = (int)map.Start.Index, Flow = mapFlow,
                    End = mapFlow ? (int)mapClose.Start.Index + 1 : entries.Count > 0 ? entries.Max(e => e.Value.End) : (int)map.Start.Index,
                };

            default:
                var m = p.Current!.Start;
                cx.Add(SharedCodes.YamlSyntax, m, $"Unexpected YAML construct ({p.Current.GetType().Name}).");
                p.MoveNext();
                return null;
        }
    }

    private static int L(Mark m) => (int)m.Line;
    private static int C(Mark m) => (int)m.Column;

    private static void CheckProperties(AnchorName anchor, TagName tag, Mark at, Cx cx)
    {
        if (!anchor.IsEmpty) cx.Add(SharedCodes.UnsupportedFeature, at, $"Anchor `&{anchor.Value}` was found.");
        if (!tag.IsEmpty) cx.Add(SharedCodes.UnsupportedFeature, at, $"Tag `{tag.Value}` was found.");
    }
}
