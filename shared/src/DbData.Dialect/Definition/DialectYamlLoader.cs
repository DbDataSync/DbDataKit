namespace DbData.Dialect;

/// <summary>
/// Strict loader for <c>dialect.yaml</c> (schema <c>dialect/1</c>): unknown keys, duplicate keys, aliases, tags,
/// wrong scalar types and non-Exact type rows without a note are all errors reported with source and line.
/// </summary>
internal static class DialectYamlLoader
{
    public const string SchemaId = "dialect/1";

    public static DialectDefinition Load(string yaml, string source)
    {
        var root = YamlTree.Parse(yaml, source);
        var f = new Fields(root, source, "dialect");
        string schema = f.Str("schema") ?? throw f.Error("missing 'schema' (expected 'dialect/1')");
        if (schema != SchemaId) throw f.Error($"unsupported schema '{schema}' (expected '{SchemaId}')");
        string id = f.Str("id") ?? throw f.Error("missing 'id'");
        if (id.Length == 0 || id != id.ToLowerInvariant() || id.Any(char.IsWhiteSpace)) throw f.Error($"id '{id}' must be lower-case without spaces");

        var d = new DialectDefinition { Id = id, Source = source, Extends = f.Str("extends"), Display = f.Str("display") };

        var quoteNode = f.Get("quote");
        if (quoteNode is YamlScalar qs)
        {
            if (qs.Value != "none") throw f.Error("'quote' must be 'none' or { open, close }", qs.Line);
            d.QuoteNone = true;
        }
        else if (quoteNode is not null)
        {
            var q = new Fields(quoteNode, source, "dialect.quote");
            d.QuoteOpen = q.Str("open") ?? throw q.Error("missing 'open'");
            d.QuoteClose = q.Str("close") ?? throw q.Error("missing 'close'");
            if (d.QuoteOpen.Length != 1 || d.QuoteClose.Length != 1) throw q.Error("'open' and 'close' must be single characters");
            q.EnsureNoUnknown();
        }

        if (f.Map("parameters") is { } pm)
        {
            if (pm.Str("style") is { } ps) d.ParameterStyle = Pick(pm, "style", ps, new Dictionary<string, ParameterStyle>
            {
                ["named_at"] = ParameterStyle.NamedAt, ["named_colon"] = ParameterStyle.NamedColon,
                ["positional"] = ParameterStyle.Positional, ["numbered"] = ParameterStyle.Numbered,
            });
            pm.EnsureNoUnknown();
        }

        if (f.Map("row_limit") is { } rl)
        {
            var map = new Dictionary<string, RowLimitStyle> { ["limit_offset"] = RowLimitStyle.LimitOffset, ["offset_fetch"] = RowLimitStyle.OffsetFetch, ["top_n"] = RowLimitStyle.TopN };
            if (rl.Str("default") is { } ds) d.DefaultRowLimit = Pick(rl, "default", ds, map);
            d.RowLimitStyles = rl.StrList("styles")?.Select(s => Pick(rl, "styles", s, map)).ToList();
            d.RowLimitTies = rl.StrList("ties")?.Select(s => Pick(rl, "ties", s, map)).ToList();
            rl.EnsureNoUnknown();
            if (d.DefaultRowLimit is { } def && d.RowLimitStyles is { } styles && !styles.Contains(def)) throw rl.Error("'default' must be one of 'styles'");
        }

        if (f.Map("identifier") is { } idf)
        {
            if (idf.Str("case") is { } cs) d.Case = Pick(idf, "case", cs, new Dictionary<string, IdentifierCase> { ["preserve"] = IdentifierCase.Preserve, ["upper"] = IdentifierCase.Upper, ["lower"] = IdentifierCase.Lower });
            d.MaxLength = idf.Int("max_length");
            d.SafeUnquoted = idf.Str("safe_unquoted");
            if (d.SafeUnquoted is not null)
                try { _ = new System.Text.RegularExpressions.Regex(d.SafeUnquoted); }
                catch (ArgumentException e) { throw idf.Error($"'safe_unquoted' is not a valid regex: {e.Message}"); }
            idf.EnsureNoUnknown();
        }

        d.ReservedWords = ListEdit.Read(f, "reserved_words");
        d.Capabilities = ListEdit.Read(f, "capabilities");
        d.Syntax = ListEdit.Read(f, "syntax");
        try
        {
            _ = ResolvedDialect.ParseFlags<EngineCaps>(d.Capabilities.Replace ?? [], "capability");
            _ = ResolvedDialect.ParseFlags<EngineCaps>(d.Capabilities.Add, "capability");
            _ = ResolvedDialect.ParseFlags<SqlSyntax>(d.Syntax.Replace ?? [], "syntax flag");
            _ = ResolvedDialect.ParseFlags<SqlSyntax>(d.Syntax.Add, "syntax flag");
        }
        catch (DialectException e) { throw new DialectException(e.Message, source, f.Line); }

        if (f.Map("read_only") is { } ro)
        {
            d.DenyWords = ListEdit.Read(ro, "deny_words");
            d.DenyPrefixes = ListEdit.Read(ro, "deny_prefixes");
            d.SelectStarters = ListEdit.Read(ro, "select_starters");
            ro.EnsureNoUnknown();
        }

        if (f.Map("literals") is { } lit)
        {
            foreach (var key in new[] { "true", "false", "binary", "date", "time", "timestamp", "timestamptz", "guid", "json", "xml", "string_prefix", "string_prefix_when", "fraction_digits" })
                if (lit.Str(key) is { } v) d.Literals[key] = v;
            lit.EnsureNoUnknown();
            if (d.Literals.TryGetValue("string_prefix_when", out var w) && w is not ("non_ascii" or "always" or "never")) throw lit.Error("'string_prefix_when' must be non_ascii, always or never");
        }

        d.Types = ReadRules(f, "types", source);
        d.TypesPrepend = ReadRules(f, "types_prepend", source) ?? [];
        f.EnsureNoUnknown();
        return d;
    }

    private static List<TypeRule>? ReadRules(Fields f, string key, string source)
    {
        var rows = f.MapList(key);
        if (rows is null) return null;
        var list = new List<TypeRule>();
        foreach (var r in rows)
        {
            string native = r.Str("native") ?? throw r.Error("missing 'native'");
            string canonical = r.Str("canonical") ?? throw r.Error("missing 'canonical'");
            var dir = r.Str("direction") is { } ds
                ? Pick(r, "direction", ds, new Dictionary<string, TypeDirection> { ["both"] = TypeDirection.Both, ["to_canonical"] = TypeDirection.ToCanonical, ["from_canonical"] = TypeDirection.FromCanonical })
                : TypeDirection.Both;
            var fid = r.Str("fidelity") is { } fs
                ? Pick(r, "fidelity", fs, new Dictionary<string, Fidelity> { ["exact"] = Fidelity.Exact, ["widened"] = Fidelity.Widened, ["lossy"] = Fidelity.Lossy })
                : Fidelity.Exact;
            string? note = r.Str("note");
            if (fid != Fidelity.Exact && string.IsNullOrWhiteSpace(note)) throw r.Error("a Widened/Lossy row needs a 'note' naming what changes");
            CanonSpec spec;
            try { spec = CanonSpec.Parse(canonical, r.Path); }
            catch (FormatException e) { throw new DialectException(e.Message, source, r.Line); }
            var rule = new TypeRule { NativePattern = native, Canon = spec, Direction = dir, Fidelity = fid, Note = note, Line = r.Line };
            if (r.Map("defaults") is { } df)
            {
                foreach (var k in new[] { "n", "p", "s" }) if (df.Int(k) is { } v) rule.Defaults[k[0]] = v;
                df.EnsureNoUnknown();
            }
            foreach (var k in new[] { "n", "p", "s" })
            {
                if (r.Int(k + "_min") is { } lo) rule.Min[k[0]] = lo;
                if (r.Int(k + "_max") is { } hi) rule.Max[k[0]] = hi;
            }
            r.EnsureNoUnknown();
            try { rule.Compile(r.Path); }
            catch (FormatException e) { throw new DialectException(e.Message, source, r.Line); }
            list.Add(rule);
        }
        return list;
    }

    private static T Pick<T>(Fields f, string key, string value, Dictionary<string, T> map)
        => map.TryGetValue(value, out var v) ? v : throw f.Error($"'{key}' has unknown value '{value}' (expected {string.Join(", ", map.Keys)})");
}
