using System.Text;

namespace DbDataKit.Config.Yaml;

/// <summary>Outcome of an edit: the new text, or a problem (and the text is null). The input is never modified.</summary>
public readonly record struct EditResult(string? Text, ConfigDiagnostic? Problem)
{
    public bool Ok => Problem == null;
}

/// <summary>
/// Surgical YAML editor. Every operation locates the node with the strict reader's source offsets and splices only the characters it has
/// to, so comments, blank lines, key order, quoting and line endings survive. Nothing is re-serialized. A structure it cannot splice safely
/// (block scalars, an anchor, a document that does not parse) is reported as <see cref="SharedCodes.NotEditable"/>, never rewritten wholesale.
/// The result is re-parsed before it is returned. Paths look like <c>server.listen[0].port</c>.
/// </summary>
public static class YamlEditor
{
    private readonly record struct Seg(string? Key, int Index);

    private sealed record Hit(YamlNode Node, YamlScalar? Key, YamlNode? Parent);

    /// <summary>Sets a scalar. Missing keys (and missing intermediate mappings) are added after the last sibling, in the file's indentation.</summary>
    public static EditResult Set(string text, string path, YamlValue value, string file = "", IDiagnosticCatalog? catalog = null)
    {
        var cx = new Cx(text, file, catalog);
        if (cx.Load(path, out var segs, out var root) is { } load) return load;
        var nl = cx.Nl;

        if (root == null)
        {
            if (segs.Count == 0 || segs[0].Key == null) return cx.Fail("An empty document has no sequence to index.");
            var prefix = text.Length > 0 && !text.EndsWith('\n') ? nl : "";
            return cx.Finish([new(text.Length, text.Length, prefix + BlockChain(segs, 0, 0, value, nl, out var err) + nl)], err);
        }

        var hits = Resolve(root, segs);
        if (hits.Count == segs.Count + 1 || (segs.Count == 0))
        {
            var hit = hits[^1];
            if (hit.Node is not YamlScalar s) return cx.Fail($"`{path}` is not a single value; Set replaces scalars only.");
            if (s.Value.Length == 0 && !s.Quoted && hit.Key != null)
            {
                var colon = text.IndexOf(':', hit.Key.End);
                if (colon < 0) return cx.Fail("Could not find the key's colon.");
                return cx.Finish([new(colon + 1, colon + 1, " " + YamlText.Render(value, hit.Parent?.Flow == true))]);
            }
            if (s.Start < text.Length && text[s.Start] is '|' or '>') return cx.Fail($"`{path}` is a block scalar; edit it by hand.");
            return cx.Finish([new(s.Start, s.End, YamlText.Render(value, hit.Parent?.Flow == true))]);
        }

        // missing from segment hits.Count-1 on
        var depth = hits.Count - 1;
        var last = hits[^1];
        if (segs.Skip(depth).Any(x => x.Key == null)) return cx.Fail($"`{path}` indexes past the end of a list; use Insert to add items.");
        if (last.Node is YamlMapping map)
        {
            if (map.Flow)
            {
                var chain = FlowChain(segs, depth, value);
                if (map.Entries.Count == 0) return cx.Finish([new(map.Start, map.End, "{" + chain + "}")]);
                var end = map.Entries[^1].Value.End;
                return cx.Finish([new(end, end, ", " + chain)]);
            }
            if (map.Entries.Count == 0) return cx.Fail("The parent mapping has no entries to place the key after.");
            var indent = map.Entries[0].Key.Column - 1;
            var at = LineEnd(text, EntryEnd(map.Entries[^1]) - 1);
            return cx.Finish([new(at, at, nl + BlockChain(segs, depth, indent, value, nl, out var err2))], err2);
        }
        if (last.Node is YamlScalar { Value.Length: 0, Quoted: false } empty && last.Key != null && last.Parent is YamlMapping { Flow: false })
        {
            // `key:` with nothing after it becomes a mapping
            var colon = text.IndexOf(':', last.Key.End);
            var at = colon + 1;
            var after = LineEnd(text, at);
            var indent = last.Key.Column - 1 + 2;
            // keep a trailing comment on the key line: insert at the line end
            return cx.Finish([new(after, after, nl + BlockChain(segs, depth, indent, value, nl, out var err3))], err3);
        }
        return cx.Fail($"Cannot add `{segs[depth].Key}` under `{path}`: its parent is not a mapping.");
    }

    /// <summary>Removes a key (with its value and children) or a list item. A removed block entry takes its own lines; comments above it stay.</summary>
    public static EditResult Remove(string text, string path, string file = "", IDiagnosticCatalog? catalog = null)
    {
        var cx = new Cx(text, file, catalog);
        if (cx.Load(path, out var segs, out var root) is { } load) return load;
        if (root == null || segs.Count == 0) return cx.Fail("Nothing to remove at that path.");
        var hits = Resolve(root, segs);
        if (hits.Count != segs.Count + 1) return cx.Fail($"`{path}` was not found.");
        var hit = hits[^1];
        var parent = hit.Parent!;

        if (parent is YamlMapping pm)
        {
            var i = pm.Entries.ToList().FindIndex(e => ReferenceEquals(e.Key, hit.Key));
            var entry = pm.Entries[i];
            if (pm.Flow)
            {
                if (pm.Entries.Count == 1) return cx.Finish([new(entry.Key.Start, entry.Value.End, "")]);
                return i < pm.Entries.Count - 1
                    ? cx.Finish([new(entry.Key.Start, pm.Entries[i + 1].Key.Start, "")])
                    : cx.Finish([new(pm.Entries[i - 1].Value.End, entry.Value.End, "")]);
            }
            var ls = LineStart(text, entry.Key.Start);
            if (text[ls..entry.Key.Start].Trim().Length > 0)
            {
                // first key of a list item (`- name: x`): the dash line belongs to the item
                if (i == pm.Entries.Count - 1) return cx.Fail("The only key of a list item cannot be removed on its own; remove the item.");
                return cx.Finish([new(entry.Key.Start, pm.Entries[i + 1].Key.Start, "")]);
            }
            return cx.Finish([new(ls, LineEndInclusive(text, EntryEnd(entry) - 1), "")]);
        }

        var seq = (YamlSequence)parent;
        var idx = seq.Items.ToList().FindIndex(x => ReferenceEquals(x, hit.Node));
        if (seq.Flow)
        {
            if (seq.Items.Count == 1) return cx.Finish([new(hit.Node.Start, hit.Node.End, "")]);
            return idx < seq.Items.Count - 1
                ? cx.Finish([new(hit.Node.Start, seq.Items[idx + 1].Start, "")])
                : cx.Finish([new(seq.Items[idx - 1].End, hit.Node.End, "")]);
        }
        return cx.Finish([new(LineStart(text, hit.Node.Start), LineEndInclusive(text, hit.Node.End - 1), "")]);
    }

    /// <summary>Inserts a scalar into the list at <paramref name="path"/>: appended, or before <paramref name="index"/>.</summary>
    public static EditResult Insert(string text, string path, YamlValue value, int? index = null, string file = "", IDiagnosticCatalog? catalog = null)
    {
        var cx = new Cx(text, file, catalog);
        if (cx.Load(path, out var segs, out var root) is { } load) return load;
        if (root == null) return cx.Fail("An empty document has no list.");
        var nl = cx.Nl;
        var hits = Resolve(root, segs);
        if (hits.Count != segs.Count + 1) return cx.Fail($"`{path}` was not found.");
        var hit = hits[^1];

        if (hit.Node is YamlScalar { Value.Length: 0, Quoted: false } && hit.Key != null && hit.Parent is YamlMapping { Flow: false })
        {
            var colon = text.IndexOf(':', hit.Key.End);
            var at = LineEnd(text, colon + 1);
            return cx.Finish([new(at, at, nl + new string(' ', hit.Key.Column - 1 + 2) + "- " + YamlText.Render(value, false))]);
        }
        if (hit.Node is not YamlSequence seq) return cx.Fail($"`{path}` is not a list.");
        if (index is < 0 || index > seq.Items.Count) return cx.Fail($"Index {index} is outside the list (it has {seq.Items.Count} items).");
        var i = index ?? seq.Items.Count;

        if (seq.Flow)
        {
            var v = YamlText.Render(value, true);
            if (seq.Items.Count == 0) return cx.Finish([new(seq.Start, seq.End, "[" + v + "]")]);
            return i < seq.Items.Count
                ? cx.Finish([new(seq.Items[i].Start, seq.Items[i].Start, v + ", ")])
                : cx.Finish([new(seq.Items[^1].End, seq.Items[^1].End, ", " + v)]);
        }
        var first = seq.Items[0];
        var ls = LineStart(text, first.Start);
        var dashIndent = text[ls..first.Start];
        var dash = dashIndent.LastIndexOf('-');
        dashIndent = dash < 0 ? new string(' ', Math.Max(0, first.Column - 3)) : dashIndent[..dash];
        var line = dashIndent + "- " + YamlText.Render(value, false);
        if (i < seq.Items.Count)
        {
            var at = LineStart(text, seq.Items[i].Start);
            return cx.Finish([new(at, at, line + nl)]);
        }
        var end = LineEnd(text, seq.Items[^1].End - 1);
        return cx.Finish([new(end, end, nl + line)]);
    }

    // ---- helpers

    private readonly record struct Splice(int Start, int End, string Text);

    private sealed class Cx(string text, string file, IDiagnosticCatalog? catalog)
    {
        private readonly IDiagnosticCatalog _catalog = catalog ?? DefaultDiagnosticCatalog.Instance;
        public string Nl { get; } = text.Contains("\r\n") ? "\r\n" : "\n";

        public EditResult Fail(string why) => new(null, _catalog.Create(SharedCodes.NotEditable, file, 0, 0, why));

        public EditResult? Load(string path, out List<Seg> segs, out YamlNode? root)
        {
            root = null;
            segs = [];
            var parseDiags = new List<ConfigDiagnostic>();
            root = StrictYamlReader.Read(text, file, parseDiags, _catalog);
            if (parseDiags.HasErrors()) return new EditResult(null, parseDiags.First(d => d.Severity == ConfigSeverity.Error));
            if (root != null && root is not YamlMapping and not YamlSequence) return Fail("The document root is a single value.");
            var parsed = ParsePath(path);
            if (parsed == null) return Fail($"`{path}` is not a valid path (use a.b[0].c).");
            segs = parsed;
            return null;
        }

        public EditResult Finish(IReadOnlyList<Splice> splices, string? problem = null)
        {
            if (problem != null) return Fail(problem);
            var sb = new StringBuilder(text);
            foreach (var s in splices.OrderByDescending(s => s.Start)) sb.Remove(s.Start, s.End - s.Start).Insert(s.Start, s.Text);
            var result = sb.ToString();
            var verify = new List<ConfigDiagnostic>();
            StrictYamlReader.Read(result, file, verify, _catalog);
            if (verify.HasErrors()) return Fail("The edit would produce a document that does not parse (" + verify.First().Message + "); nothing was changed.");
            return new EditResult(result, null);
        }
    }

    private static List<Seg>? ParsePath(string path)
    {
        var segs = new List<Seg>();
        if (path.Length == 0) return segs;
        var i = 0;
        var key = new StringBuilder();
        void FlushKey() { if (key.Length > 0) { segs.Add(new(key.ToString(), -1)); key.Clear(); } }
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.') { FlushKey(); i++; }
            else if (c == '[')
            {
                FlushKey();
                var close = path.IndexOf(']', i);
                if (close < 0 || !int.TryParse(path.AsSpan(i + 1, close - i - 1), out var n) || n < 0) return null;
                segs.Add(new(null, n));
                i = close + 1;
            }
            else { key.Append(c); i++; }
        }
        FlushKey();
        return segs;
    }

    /// <summary>Walks as far as the path exists. The first hit is the root; hit k is the node reached after k segments.</summary>
    private static List<Hit> Resolve(YamlNode root, List<Seg> segs)
    {
        var hits = new List<Hit> { new(root, null, null) };
        var node = root;
        foreach (var seg in segs)
        {
            if (seg.Key != null && node is YamlMapping m && m.GetEntry(seg.Key) is { } e) { hits.Add(new(e.Value, e.Key, m)); node = e.Value; }
            else if (seg.Key == null && node is YamlSequence q && seg.Index < q.Items.Count) { hits.Add(new(q.Items[seg.Index], null, q)); node = q.Items[seg.Index]; }
            else break;
        }
        return hits;
    }

    private static int EntryEnd(YamlEntry e) => e.Value is YamlScalar { Value.Length: 0, Quoted: false } ? e.Key.End : e.Value.End;

    private static string BlockChain(List<Seg> segs, int from, int indent, YamlValue value, string nl, out string? problem)
    {
        problem = null;
        var sb = new StringBuilder();
        for (var i = from; i < segs.Count; i++)
        {
            if (segs[i].Key == null) { problem = "A list index cannot be created by Set."; return ""; }
            if (i > from) sb.Append(nl);
            sb.Append(new string(' ', indent + 2 * (i - from))).Append(YamlText.Scalar(segs[i].Key!)).Append(':');
            if (i == segs.Count - 1) sb.Append(' ').Append(YamlText.Render(value, false));
        }
        return sb.ToString();
    }

    private static string FlowChain(List<Seg> segs, int from, YamlValue value)
    {
        var inner = YamlText.Render(value, true);
        for (var i = segs.Count - 1; i >= from; i--)
        {
            inner = YamlText.FlowScalar(segs[i].Key!) + ": " + inner;
            if (i > from) inner = "{" + inner + "}";
        }
        return inner;
    }

    private static int LineStart(string text, int index)
    {
        var i = text.LastIndexOf('\n', Math.Max(0, index - 1));
        return i < 0 || index == 0 ? 0 : i + 1;
    }

    /// <summary>Offset of the end of the line containing <paramref name="index"/>, before its line break (where a trailing comment ends).</summary>
    private static int LineEnd(string text, int index)
    {
        var i = text.IndexOf('\n', Math.Max(0, index));
        if (i < 0) return text.Length;
        return i > 0 && text[i - 1] == '\r' ? i - 1 : i;
    }

    private static int LineEndInclusive(string text, int index)
    {
        var i = text.IndexOf('\n', Math.Max(0, index));
        return i < 0 ? text.Length : i + 1;
    }
}
