using DbDataBuild.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DbDataBuild.Models.Yaml;

/// <summary>
/// Reads the strict YAML subset (DESIGN.md 6.3): scalars stay strings, duplicate keys are errors,
/// and anchors, aliases, merge keys and tags are rejected. Problems are reported as diagnostics, never thrown.
/// </summary>
public static class StrictYamlReader
{
    public static YamlNode? Read(string text, string file, List<Diagnostic> diagnostics)
    {
        var parser = new Parser(new StringReader(text));
        try
        {
            parser.Consume<StreamStart>();
            if (parser.TryConsume<StreamEnd>(out _)) return null; // empty file
            parser.Consume<DocumentStart>();
            var root = ReadNode(parser, file, diagnostics);
            parser.Consume<DocumentEnd>();
            if (!parser.Accept<StreamEnd>(out _))
            {
                var m = parser.Current!.Start;
                diagnostics.Add(new Diagnostic(DiagnosticCatalog.YamlSyntax, At(file, m),
                    "The file contains more than one YAML document."));
                return null;
            }
            return root;
        }
        catch (YamlException ex)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCatalog.YamlSyntax, At(file, ex.Start), ex.Message));
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or EndOfStreamException)
        {
            // YamlDotNet's scanner can throw these (not YamlException) on some malformed input; a truncated file reaches the end of the event stream. Position is best effort.
            var m = parser.Current?.Start ?? new Mark(0, 1, 1);
            diagnostics.Add(new Diagnostic(DiagnosticCatalog.YamlSyntax, At(file, m),
                "The YAML scanner could not make sense of this region (malformed flow collection or scalar)."));
            return null;
        }
    }

    private static YamlNode? ReadNode(Parser p, string file, List<Diagnostic> diags)
    {
        switch (p.Current)
        {
            case Scalar s:
                p.MoveNext();
                CheckProperties(s.Anchor, s.Tag, s.Start, file, diags);
                return new YamlScalar(s.Value, L(s.Start), C(s.Start)) { Start = (int)s.Start.Index, End = (int)s.End.Index, Quoted = s.Style is not (YamlDotNet.Core.ScalarStyle.Plain or YamlDotNet.Core.ScalarStyle.Any) };

            case AnchorAlias a:
                p.MoveNext();
                Unsupported(file, a.Start, $"Alias `*{a.Value}` was found.", diags);
                return null;

            case SequenceStart seq:
                p.MoveNext();
                CheckProperties(seq.Anchor, seq.Tag, seq.Start, file, diags);
                var items = new List<YamlNode>();
                while (!p.Accept<SequenceEnd>(out var seqEnd))
                {
                    var item = ReadNode(p, file, diags);
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
                CheckProperties(map.Anchor, map.Tag, map.Start, file, diags);
                var entries = new List<YamlEntry>();
                var seen = new HashSet<string>();
                while (!p.Accept<MappingEnd>(out _))
                {
                    var keyNode = ReadNode(p, file, diags);
                    var value = ReadNode(p, file, diags);
                    if (keyNode is not YamlScalar key)
                    {
                        if (keyNode != null)
                            diags.Add(new Diagnostic(DiagnosticCatalog.YamlSyntax, new(file, keyNode.Line, keyNode.Column),
                                "A mapping key must be a plain string."));
                        continue;
                    }
                    if (key.Value == "<<")
                    {
                        Unsupported(file, new Mark(0, key.Line, key.Column), "Merge key `<<` was found.", diags);
                        continue;
                    }
                    if (!seen.Add(key.Value))
                    {
                        diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, new(file, key.Line, key.Column),
                            $"Key `{key.Value}` appears more than once in this mapping."));
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
                diags.Add(new Diagnostic(DiagnosticCatalog.YamlSyntax, At(file, m),
                    $"Unexpected YAML construct ({p.Current.GetType().Name})."));
                p.MoveNext();
                return null;
        }
    }

    private static int L(Mark m) => (int)m.Line;
    private static int C(Mark m) => (int)m.Column;
    private static SourceLocation At(string file, Mark m) => new(file, L(m), C(m));

    private static void CheckProperties(AnchorName anchor, TagName tag, Mark at, string file, List<Diagnostic> diags)
    {
        if (!anchor.IsEmpty) Unsupported(file, at, $"Anchor `&{anchor.Value}` was found.", diags);
        if (!tag.IsEmpty) Unsupported(file, at, $"Tag `{tag.Value}` was found.", diags);
    }

    private static void Unsupported(string file, Mark at, string found, List<Diagnostic> diags) =>
        diags.Add(new Diagnostic(DiagnosticCatalog.UnsupportedYamlFeature, At(file, at), found));
}
