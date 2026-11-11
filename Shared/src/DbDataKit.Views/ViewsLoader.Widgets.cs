using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

public static partial class ViewsLoader
{
    // `<` followed by a letter, `/`, `!` or `?` opens a tag, a comment or a declaration: none of it is text of a widget (the rule of the portal home, DBD-APP-013)
    private static readonly Regex RawHtml = new(@"<[A-Za-z/!?]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLink = new(@"\[(?<text>[^\]]*)\]\((?<href>[^)]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ParamUse = new(@"\{\{\s*params\.(?<name>[A-Za-z0-9_-]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HashRule = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether a link in a widget's text may be followed: a path on the host that starts with a single <c>/</c>. A widget's text carries no link that leaves the host.</summary>
    public static bool IsSafeTextLink(string href) =>
        href.Length is > 0 and <= 500 && !href.Any(char.IsControl) && !href.Contains(' ') && href.StartsWith('/') && !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains('\\');

    private sealed partial class Reader
    {
        public List<ViewWidget>? ReadWidgetsRoot(YamlNode root, IEnumerable<string> parameters)
        {
            foreach (var p in parameters) declared.Add(p);
            return ReadWidgets(root);
        }

        private List<ViewWidget>? ReadWidgets(YamlNode node)
        {
            if (Map(node, "widgets", "`widgets`", node) is not { } map) return null;
            if (map.Entries.Count == 0) { Bad(ViewsCodes.InvalidValue, map, "widgets", "`widgets` defines at least one widget."); return null; }
            if (map.Entries.Count > MaximumWidgets) { Bad(ViewsCodes.InvalidValue, map, "widgets", $"A document has at most {MaximumWidgets} widgets."); return null; }
            var list = new List<ViewWidget>();
            foreach (var e in map.Entries)
            {
                if (!NameRule.IsMatch(e.Key.Value)) { Bad(ViewsCodes.InvalidValue, e.Key, J("widgets", e.Key.Value), $"`{e.Key.Value}` is not a widget id: lower-case letters, digits, '-' and '_', starting with a letter."); continue; }
                if (ReadWidget(e.Key.Value, e.Value) is { } w) list.Add(w);
            }
            return list;
        }

        private ViewWidget? ReadWidget(string id, YamlNode node)
        {
            var path = J("widgets", id);
            if (Map(node, path, $"The widget `{id}`", node) is not { } map) return null;
            var typeNode = map.Get("type");
            if (typeNode is null) { Add(ViewsCodes.MissingKey, map, $"Required key `type` is missing.", $"Add `type:` with one of: {string.Join(", ", Choices["widgetType"])}.", J(path, "type")); return null; }
            if (typeNode is YamlScalar { Value: "embed" })
            { Bad(ViewsCodes.ReservedWidget, typeNode, J(path, "type"), "The widget type `embed` is reserved: this format does not read it yet.", $"Use one of: {string.Join(", ", Choices["widgetType"])}."); return null; }
            if (typeNode is not YamlScalar ts || !Choices["widgetType"].Contains(ts.Value))
            { Bad(SharedCodes.UnknownUnionKind, typeNode, J(path, "type"), $"`{(typeNode as YamlScalar)?.Value}` is not a widget type.", $"Use one of: {string.Join(", ", Choices["widgetType"])}."); return null; }
            var type = ts.Value;
            var title = map.Get("title") is { } t ? Text(t, J(path, "title"), "`title`") : null;

            CheckKeysAt(map, Keys["widget." + type], $"a {type} widget", path, e =>
            {
                if (type == "chart" && ComputingKeys.Contains(e.Key.Value)) { Computing(e.Key, J(path, e.Key.Value)); return true; }
                if (type == "image" && e.Key.Value is "url" or "src" or "href" or "uri" or "data" or "base64")
                { Bad(ViewsCodes.UnsafeImage, e.Key, J(path, e.Key.Value), $"An image is held in the definition's artifacts by hash (`hash: sha256:...`); `{e.Key.Value}` would fetch it from outside."); return true; }
                return false;
            });

            var fields = new List<EncodedField>();
            WidgetQuery? query = null;
            HashSet<string>? outputs = null;
            switch (type)
            {
                case "metric":
                    if (map.Get("label") is { } lb) Text(lb, J(path, "label"), "`label`");
                    if (map.Get("format") is { } fm) FormatValue(fm, J(path, "format"));
                    MetricEncodings(map, path, fields);
                    break;
                case "chart": ChartEncodings(map, path, fields); break;
                case "matrix": MatrixEncodings(map, path, fields); break;
                case "table": TableEncodings(map, path, fields); break;
                case "text": TextBody(map, path); break;
                case "image": ImageBody(map, path); break;
                case "control": ControlBody(map, path); break;
            }
            if (type is "metric" or "chart" or "matrix" or "table")
            {
                var q = Required(map, "query", path);
                if (q is not null) query = ReadQuery(q, J(path, "query"), out outputs);
                if (map.Get("on_select") is { } os) OnSelect(os, J(path, "on_select"));
                if (map.Get("drill") is { } dr) Drill(dr, J(path, "drill"));
            }
            if (query is { Explicit: true } && outputs is not null)
                foreach (var f in fields.Where(f => !outputs.Contains(f.Field)))
                    Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.FieldNotInQuery, File, f.Line, f.Column,
                        $"`{f.Field}` is encoded as {f.Channel} but is not an output of the query (it gives {string.Join(", ", outputs.Order())}).",
                        "Name an output of the query, or add the field to it.", f.Path));
            return new ViewWidget(id, type, title, fields, query, ToObject(map), map.Line, map.Column);
        }

        private void Computing(YamlNode at, string path) =>
            Bad(ViewsCodes.ChartNotInSubset, at, path, $"`{at switch { YamlScalar s => s.Value, _ => "this" }}` isn't allowed in a chart: it fetches data or computes a value, and a chart only says what is drawn. Computation belongs in the widget's query.",
                "Put the aggregate, bin, time unit or calculation in the query (a grain of an attribute, a measure, a transform) and encode its output.");

        // ---- encodings ----

        private YamlMapping? Encodings(YamlMapping widget, string path, string type)
        {
            var node = Required(widget, "encodings", path);
            if (node is null || Map(node, J(path, "encodings"), "`encodings`", widget) is not { } enc) return null;
            CheckKeysAt(enc, Keys["encodings." + type], $"the encodings of a {type}", J(path, "encodings"), e =>
            {
                if (type != "chart" || !ComputingKeys.Contains(e.Key.Value)) return false;
                Computing(e.Key, J(J(path, "encodings"), e.Key.Value));
                return true;
            });
            return enc;
        }

        private void MetricEncodings(YamlMapping widget, string path, List<EncodedField> fields)
        {
            if (Encodings(widget, path, "metric") is not { } enc) return;
            var ep = J(path, "encodings");
            if (Required(enc, "value", ep) is { } v && Field(v, J(ep, "value"), "`value`") is { } vf) fields.Add(new("value", vf, J(ep, "value"), v.Line, v.Column));
            if (enc.Get("compare") is { } c && Field(c, J(ep, "compare"), "`compare`") is { } cf) fields.Add(new("compare", cf, J(ep, "compare"), c.Line, c.Column));
        }

        private void ChartEncodings(YamlMapping widget, string path, List<EncodedField> fields)
        {
            var markNode = Required(widget, "mark", path);
            if (markNode is YamlMapping mm)
            {
                CheckKeysAt(mm, Keys["mark"], "a mark", J(path, "mark"), e => { if (!ComputingKeys.Contains(e.Key.Value)) return false; Computing(e.Key, J(J(path, "mark"), e.Key.Value)); return true; });
                if (Required(mm, "type", J(path, "mark")) is { } mt) Choice(mt, J(J(path, "mark"), "type"), "A mark's `type`", Choices["mark"], ViewsCodes.ChartNotInSubset);
                if (mm.Get("tooltip") is { } tt) Flag(tt, J(J(path, "mark"), "tooltip"), "`tooltip`");
                if (mm.Get("point") is { } pt) Flag(pt, J(J(path, "mark"), "point"), "`point`");
            }
            else if (markNode is not null) Choice(markNode, J(path, "mark"), "`mark`", Choices["mark"], ViewsCodes.ChartNotInSubset);

            if (Encodings(widget, path, "chart") is not { } enc) return;
            var ep = J(path, "encodings");
            foreach (var e in enc.Entries.Where(e => Keys["encodings.chart"].Contains(e.Key.Value)))
            {
                var cp = J(ep, e.Key.Value);
                if (e.Key.Value == "tooltip" && e.Value is YamlSequence list)
                {
                    for (var i = 0; i < list.Items.Count; i++) Channel(e.Key.Value, list.Items[i], I(cp, i), fields);
                }
                else Channel(e.Key.Value, e.Value, cp, fields);
            }
        }

        private void Channel(string channel, YamlNode node, string path, List<EncodedField> fields)
        {
            if (node is YamlScalar { Value.Length: > 0 } shorthand) { fields.Add(new(channel, shorthand.Value, path, node.Line, node.Column)); return; }
            if (node is not YamlMapping m) { Bad(ViewsCodes.InvalidValue, node, path, "A channel is a field name, or a mapping with `field`, `type`, `sort`, `stack` and `title`."); return; }
            CheckKeysAt(m, Keys["channel"], "a channel", path, e => { if (!ComputingKeys.Contains(e.Key.Value)) return false; Computing(e.Key, J(path, e.Key.Value)); return true; });
            if (m.Get("field") is { } f && Field(f, J(path, "field"), "A channel's `field`") is { } fv) fields.Add(new(channel, fv, J(path, "field"), f.Line, f.Column));
            if (m.Get("type") is { } t) Choice(t, J(path, "type"), "A channel's `type`", Choices["fieldType"], ViewsCodes.ChartNotInSubset);
            if (m.Get("sort") is { } s)
            {
                if (s is YamlMapping) Computing(s, J(path, "sort"));
                else if (s is YamlScalar { Value: var sv, Quoted: var q } && (q ? sv is "ascending" or "descending" or "sort_key" : sv is "ascending" or "descending" or "sort_key" or "null" or "~" or "")) { }
                else if (s is not YamlSequence) Bad(ViewsCodes.InvalidValue, s, J(path, "sort"), "A channel's `sort` is ascending, descending, sort_key or a list of members.");
            }
            if (m.Get("stack") is { } st && !(st is YamlScalar { Value: var v } && (v is "null" or "~" or "" or "true" or "false" or "zero" or "center" or "normalize")))
                Bad(ViewsCodes.InvalidValue, st, J(path, "stack"), "A channel's `stack` is null, true, false, zero, center or normalize.");
            if (m.Get("title") is { } ti && ti is not YamlScalar) Bad(ViewsCodes.InvalidValue, ti, J(path, "title"), "A channel's `title` is text.");
            if (m.Get("format") is { } fm) FormatValue(fm, J(path, "format"));
        }

        private void MatrixEncodings(YamlMapping widget, string path, List<EncodedField> fields)
        {
            if (Encodings(widget, path, "matrix") is not { } enc) return;
            var ep = J(path, "encodings");
            foreach (var axis in new[] { "rows", "columns" })
            {
                if (enc.Get(axis) is not { } an || Seq(an, J(ep, axis), $"`{axis}`", enc) is not { } list) continue;
                var sorted = 0;
                for (var i = 0; i < list.Items.Count; i++)
                {
                    var ip = I(J(ep, axis), i);
                    if (list.Items[i] is YamlMapping entry)
                    {
                        // an axis attribute with a format and a sort (DBR41): `{field, format, sort}`
                        CheckKeysAt(entry, Keys["matrix_axis_entry"], $"an entry of the {axis}", ip);
                        if (Required(entry, "field", ip) is { } fn && Field(fn, J(ip, "field"), "`field`") is { } ff) fields.Add(new(axis, ff, J(ip, "field"), fn.Line, fn.Column));
                        if (entry.Get("format") is { } fmt) FormatValue(fmt, J(ip, "format"));
                        if (entry.Get("sort") is { } so)
                        {
                            SortKey(so, J(ip, "sort"));
                            if (++sorted > 1) Bad(ViewsCodes.InvalidValue, so, J(ip, "sort"), $"Only one level of the {axis} carries a sort: it is the sort of the axis.");
                        }
                    }
                    else if (Field(list.Items[i], ip, "A field") is { } f) fields.Add(new(axis, f, ip, list.Items[i].Line, list.Items[i].Column));
                }
            }
            if (Required(enc, "values", ep) is { } vn && Seq(vn, J(ep, "values"), "`values`", enc, 1) is { } vals)
                for (var i = 0; i < vals.Items.Count; i++)
                {
                    var vp = I(J(ep, "values"), i);
                    if (Map(vals.Items[i], vp, "A value", vals) is not { } vm) continue;
                    CheckKeysAt(vm, ["field", "empty", "format", "label"], "a matrix value", vp);
                    if (Required(vm, "field", vp) is { } fn && Field(fn, J(vp, "field"), "`field`") is { } f) fields.Add(new("values", f, J(vp, "field"), fn.Line, fn.Column));
                    if (vm.Get("empty") is { } em) Choice(em, J(vp, "empty"), "`empty`", ["blank", "zero"]);
                    if (vm.Get("format") is { } fmt) FormatValue(fmt, J(vp, "format"));
                    if (vm.Get("label") is { } lb && lb is not YamlScalar) Bad(ViewsCodes.InvalidValue, lb, J(vp, "label"), "`label` is text.");
                }
            if (enc.Get("values_on") is { } vo) Choice(vo, J(ep, "values_on"), "`values_on`", ["columns", "rows"]);
            if (enc.Get("totals") is { } tn && Map(tn, J(ep, "totals"), "`totals`", enc) is { } tm)
            {
                CheckKeysAt(tm, ["rows", "columns"], "`totals`", J(ep, "totals"));
                foreach (var e in tm.Entries.Where(e => e.Key.Value is "rows" or "columns")) Flag(e.Value, J(J(ep, "totals"), e.Key.Value), $"`totals.{e.Key.Value}`");
            }
            if (enc.Get("sort") is { } sn && Map(sn, J(ep, "sort"), "`sort`", enc) is { } sm)
            {
                CheckKeysAt(sm, ["rows", "columns"], "`sort`", J(ep, "sort"));
                foreach (var e in sm.Entries.Where(e => e.Key.Value is "rows" or "columns")) SortKey(e.Value, J(J(ep, "sort"), e.Key.Value));
            }
            if (enc.Get("column_limit") is { } cl && Map(cl, J(ep, "column_limit"), "`column_limit`", enc) is { } cm)
            {
                CheckKeysAt(cm, ["max", "overflow"], "`column_limit`", J(ep, "column_limit"));
                if (Required(cm, "max", J(ep, "column_limit")) is { } mx) Whole(mx, J(J(ep, "column_limit"), "max"), "`max`", 1, 10_000);
                if (Required(cm, "overflow", J(ep, "column_limit")) is { } ov) Choice(ov, J(J(ep, "column_limit"), "overflow"), "`overflow`", ["error", "truncate", "top_n_other"]);
            }
        }

        private void SortKey(YamlNode node, string path)
        {
            if (Map(node, path, "A sort", node) is not { } m) return;
            CheckKeysAt(m, ["by", "dir"], "a sort", path);
            if (Required(m, "by", path) is { } by) Field(by, J(path, "by"), "`by`");
            if (m.Get("dir") is { } d) Choice(d, J(path, "dir"), "`dir`", ["asc", "desc"]);
        }

        private void TableEncodings(YamlMapping widget, string path, List<EncodedField> fields)
        {
            if (Encodings(widget, path, "table") is not { } enc) return;
            var ep = J(path, "encodings");
            if (Required(enc, "columns", ep) is { } cn && Seq(cn, J(ep, "columns"), "`columns`", enc, 1, 200) is { } cols)
                for (var i = 0; i < cols.Items.Count; i++)
                {
                    var cp = I(J(ep, "columns"), i);
                    if (Map(cols.Items[i], cp, "A column", cols) is not { } cm) continue;
                    CheckKeysAt(cm, Keys["table_column"], "a table column", cp);
                    if (Required(cm, "field", cp) is { } fn && Field(fn, J(cp, "field"), "`field`") is { } f) fields.Add(new("columns", f, J(cp, "field"), fn.Line, fn.Column));
                    if (cm.Get("label") is { } lb && lb is not YamlScalar) Bad(ViewsCodes.InvalidValue, lb, J(cp, "label"), "`label` is text.");
                    if (cm.Get("format") is { } fmt) FormatValue(fmt, J(cp, "format"));
                    if (cm.Get("align") is { } al) Choice(al, J(cp, "align"), "`align`", Choices["align"]);
                    if (cm.Get("width") is { } wd) Whole(wd, J(cp, "width"), "`width` (a hint in CSS pixels)", 20, 2000);
                }
            if (enc.Get("sort") is { } sn) SortKey(sn, J(ep, "sort"));
        }

        // ---- text, image, control ----

        private void TextBody(YamlMapping widget, string path)
        {
            var node = Required(widget, "text", path);
            if (node is not YamlScalar text) { if (node is not null) Bad(ViewsCodes.InvalidValue, node, J(path, "text"), "`text` is markdown text."); return; }
            if (text.Value.Length is 0 or > 20_000) { Bad(ViewsCodes.InvalidValue, text, J(path, "text"), "`text` is 1 to 20000 characters."); return; }
            var first = text.Quoted && text.Value.Contains('\n') && !JsonInput.IsJsonFile(File) ? text.Line + 1 : text.Line;
            var lines = text.Value.Split('\n');
            var tp = J(path, "text");
            for (var i = 0; i < lines.Length; i++)
            {
                var at = first + i;
                void Unsafe(int column, string message, string? fix = null) =>
                    Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.UnsafeText, File, at, column, message, fix ?? "Write markdown (headings, lists, bold, italic, `code`, links to a path on the host) only.", tp));
                if (RawHtml.Match(lines[i]) is { Success: true } html) Unsafe(html.Index + 1, "Text has no HTML or script: this looks like a tag.");
                if (MarkdownImage.Match(lines[i]) is { Success: true } img) Unsafe(img.Index + 1, "Text carries no image: use an `image` widget, which holds the picture by hash.");
                foreach (Match link in MarkdownLink.Matches(lines[i]))
                    if (!link.Value.StartsWith('!') && !(link.Index > 0 && lines[i][link.Index - 1] == '!') && !IsSafeTextLink(link.Groups["href"].Value))
                        Unsafe(link.Index + 1, $"`{link.Groups["href"].Value}` is not a link text may carry: only a path on the host that starts with a single `/`.");
                foreach (Match use in ParamUse.Matches(lines[i]))
                    if (!IsDeclared(use.Groups["name"].Value)) Bad(ViewsCodes.UndeclaredParameter, text, tp, $"`{{{{params.{use.Groups["name"].Value}}}}}` names a parameter the document does not declare.", "Declare it under `parameters`.");
            }
        }

        private void ImageBody(YamlMapping widget, string path)
        {
            if (Required(widget, "hash", path) is { } h && !(h is YamlScalar hs && HashRule.IsMatch(hs.Value)))
                Bad(ViewsCodes.UnsafeImage, h, J(path, "hash"), "An image is held by hash: `sha256:` and 64 lower-case hex digits, never a URL.");
            if (Required(widget, "media_type", path) is { } m)
            {
                if (m is YamlScalar { Value: var mv } && Choices["mediaType"].Contains(mv)) { }
                else Bad(ViewsCodes.UnsafeImage, m, J(path, "media_type"), $"An image is a raster ({string.Join(", ", Choices["mediaType"])}); SVG can script and is refused.");
            }
            if (Required(widget, "alt", path) is { } a) Text(a, J(path, "alt"), "`alt`", 500);
            if (widget.Get("fit") is { } f) Choice(f, J(path, "fit"), "`fit`", Choices["fit"]);
        }

        private void ControlBody(YamlMapping widget, string path)
        {
            if (Required(widget, "parameter", path) is { } p && Name(p, J(path, "parameter"), "`parameter`") is { } pn && !IsDeclared(pn))
                Bad(ViewsCodes.UndeclaredParameter, p, J(path, "parameter"), $"The control edits `{pn}`, a parameter the document does not declare.", "Declare it under `parameters`.");
            if (widget.Get("control") is { } c) Choice(c, J(path, "control"), "`control`", Choices["control"]);
            if (widget.Get("label") is { } l) Text(l, J(path, "label"), "`label`");
            if (widget.Get("values") is { } v && Map(v, J(path, "values"), "`values`", widget) is { } vm)
            {
                CheckKeysAt(vm, Keys["control.values"], "`values`", J(path, "values"));
                if (vm.Get("attribute") is { } at) Field(at, J(J(path, "values"), "attribute"), "`attribute`");
                if (vm.Get("limit") is { } lm) Whole(lm, J(J(path, "values"), "limit"), "`limit`", 1, MaximumQueryLimit);
            }
        }

        // ---- cross-filter and drill ----

        private void OnSelect(YamlNode node, string path)
        {
            if (Map(node, path, "`on_select`", node) is not { } m) return;
            CheckKeysAt(m, Keys["on_select"], "`on_select`", path);
            if (Required(m, "set", path) is { } s && Name(s, J(path, "set"), "`set`") is { } sn && !IsDeclared(sn))
                Bad(ViewsCodes.UndeclaredParameter, s, J(path, "set"), $"Selecting sets `{sn}`, a parameter the document does not declare.", "Declare it under `parameters`.");
            if (Required(m, "from", path) is { } f) Field(f, J(path, "from"), "`from`");
        }

        private void Drill(YamlNode node, string path)
        {
            if (Map(node, path, "`drill`", node) is not { } m) return;
            CheckKeysAt(m, Keys["drill"], "`drill`", path);
            string? target = null; string? targetKind = null;
            if (Required(m, "to", path) is { } to && Map(to, J(path, "to"), "`to`", m) is { } tm)
            {
                CheckKeysAt(tm, Keys["drill.to"], "`to`", J(path, "to"));
                if (tm.Entries.Count != 1) Bad(ViewsCodes.InvalidValue, tm, J(path, "to"), "`to` names exactly one of `dashboard`, `report` or `view`.");
                else if (tm.Entries[0] is { } te && Keys["drill.to"].Contains(te.Key.Value) && Name(te.Value, J(J(path, "to"), te.Key.Value), "A drill target") is { } tn) { target = tn; targetKind = te.Key.Value; }
            }
            var given = new HashSet<string>();
            if (m.Get("with") is { } w && Map(w, J(path, "with"), "`with`", m) is { } wm)
                foreach (var e in wm.Entries)
                {
                    var wp = J(J(path, "with"), e.Key.Value);
                    if (!NameRule.IsMatch(e.Key.Value)) Bad(ViewsCodes.InvalidValue, e.Key, wp, "A parameter name is lower-case letters, digits, '-' and '_'.");
                    given.Add(e.Key.Value);
                    if (e.Value is not YamlScalar v) { Bad(ViewsCodes.InvalidValue, e.Value, wp, "A value is `$field` of the clicked row, `$params.name`, or a literal."); continue; }
                    if (v.Value.StartsWith("$params.", StringComparison.Ordinal) && !IsDeclared(v.Value[8..]))
                        Bad(ViewsCodes.UndeclaredParameter, v, wp, $"`{v.Value}` names a parameter the document does not declare.", "Declare it under `parameters`.");
                }
            if (target is not null && targets is not null && targets.TryGetValue($"{targetKind}:{target}", out var theirs))
                foreach (var p in theirs.Where(p => p.Required && !p.HasDefault && !given.Contains(p.Name)))
                    Bad(ViewsCodes.DrillMissingParameter, m, path, $"The drill to {targetKind} `{target}` does not give its required parameter `{p.Name}`.", $"Add `{p.Name}:` under `with`.");
        }

        // ---- the query ----

        private WidgetQuery? ReadQuery(YamlNode node, string path, out HashSet<string>? outputs)
        {
            outputs = null;
            if (Map(node, path, "`query`", node) is not { } q) return null;
            var forms = new[] { "semantic", "saved", "store", "ref" }.Where(k => q.Get(k) is not null).ToList();
            var modeNode = q.Get("mode");
            var mode = modeNode is null ? null : Choice(modeNode, J(path, "mode"), "`mode`", ["derived", "explicit"], ViewsCodes.BadQuery);
            if (forms.Count > 1) { Bad(ViewsCodes.BadQuery, q, path, $"A query is one form: {string.Join(", ", forms.Select(f => $"`{f}`"))} are written together.", "Keep one of `semantic`, `saved`, `store` or `ref`."); return null; }
            if (forms.Count == 0)
            {
                if (mode == "explicit") { Bad(ViewsCodes.BadQuery, modeNode!, J(path, "mode"), "`mode: explicit` needs the query itself: `semantic`, `saved`, `store` or `ref`."); return null; }
                return ReadDerived(q, path);
            }
            if (mode != "explicit")
            { Bad(ViewsCodes.BadQuery, (YamlNode?)modeNode ?? q, modeNode is null ? path : J(path, "mode"), $"A written query says `mode: explicit`, so a reader knows the encodings do not drive it (the query has `{forms[0]}`).", "Add `mode: explicit`, or drop the query's form and let the encodings derive it."); return null; }
            var form = forms[0];
            CheckKeysAt(q, Keys["query." + form], $"a `{form}` query", path);
            if (q.Get("params") is { } pm) ParamMap(pm, J(path, "params"));
            var raw = ToObject(q);
            var known = new HashSet<string>(StringComparer.Ordinal);
            string? model = null;
            switch (form)
            {
                case "semantic":
                    if (Map(q.Get("semantic"), J(path, "semantic"), "`semantic`", q) is not { } sm) return null;
                    CheckKeysAt(sm, Keys["semantic"], "`semantic`", J(path, "semantic"));
                    model = Required(sm, "model", J(path, "semantic")) is { } mn ? Name(mn, J(J(path, "semantic"), "model"), "`model`") : null;
                    var hasRequest = sm.Get("request") is not null; var hasSql = sm.Get("sql") is not null;
                    if (hasRequest == hasSql) { Bad(ViewsCodes.BadQuery, sm, J(path, "semantic"), "`semantic` has exactly one of `request` (a semantic request) and `sql` (semantic SQL)."); return null; }
                    if (hasSql)
                    {
                        Text(sm.Get("sql"), J(J(path, "semantic"), "sql"), "`sql`", 20_000);
                        form = "semantic_sql";
                        break;
                    }
                    if (Map(sm.Get("request"), J(J(path, "semantic"), "request"), "`request`", sm) is not { } req) return null;
                    foreach (var key in new[] { "measures", "attributes" })
                        if (req.Get(key) is YamlSequence entries)
                            foreach (var entry in entries.Items)
                                switch (entry)
                                {
                                    case YamlScalar es: known.Add(es.Value); break;
                                    case YamlMapping em when (em.Get("as") ?? em.Get("measure")) is YamlScalar name: known.Add(name.Value); break;
                                }
                    outputs = known;
                    break;
                case "saved":
                    Name(q.Get("saved"), J(path, "saved"), "`saved`");
                    outputs = Outputs(q, "outputs", path);
                    break;
                case "store":
                    if (Map(q.Get("store"), J(path, "store"), "`store`", q) is not { } st) return null;
                    CheckKeysAt(st, Keys["store"], "`store`", J(path, "store"));
                    model = Required(st, "model", J(path, "store")) is { } smn ? Name(smn, J(J(path, "store"), "model"), "`model`") : null;
                    if (Required(st, "entity", J(path, "store")) is { } en) Name(en, J(J(path, "store"), "entity"), "`entity`");
                    if (st.Get("limit") is { } lim) Whole(lim, J(J(path, "store"), "limit"), "`limit`", 1, MaximumQueryLimit);
                    outputs = Outputs(st, "columns", J(path, "store"));
                    break;
                case "ref":
                    if (q.Get("ref") is not YamlScalar { Value.Length: > 0 }) Bad(ViewsCodes.InvalidValue, q.Get("ref")!, J(path, "ref"), "`ref` names an already run result.");
                    outputs = Outputs(q, "outputs", path);
                    break;
            }
            CheckMarkers(q, path, q.Get("params") as YamlMapping);
            return new WidgetQuery(form, true, model, raw);
        }

        private HashSet<string>? Outputs(YamlMapping map, string key, string path)
        {
            if (map.Get(key) is not { } node || Seq(node, J(path, key), $"`{key}`", map, 1) is not { } list) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < list.Items.Count; i++)
                if (Field(list.Items[i], I(J(path, key), i), "An output") is { } f) set.Add(f);
            return set;
        }

        private WidgetQuery? ReadDerived(YamlMapping q, string path)
        {
            CheckKeysAt(q, Keys["query.derived"], "a derived query", path);
            var model = Required(q, "model", path) is { } mn ? Name(mn, J(path, "model"), "`model`") : null;
            if (q.Get("filters") is { } f) Seq(f, J(path, "filters"), "`filters`", q);
            if (q.Get("segments") is { } sg && Seq(sg, J(path, "segments"), "`segments`", q) is { } sgs)
                for (var i = 0; i < sgs.Items.Count; i++) Field(sgs.Items[i], I(J(path, "segments"), i), "A segment");
            if (q.Get("order") is { } o) Seq(o, J(path, "order"), "`order`", q);
            if (q.Get("limit") is { } l) Whole(l, J(path, "limit"), "`limit`", 1, MaximumQueryLimit);
            if (q.Get("totals") is { } t) Map(t, J(path, "totals"), "`totals`", q);
            if (q.Get("timezone") is { } tz) Text(tz, J(path, "timezone"), "`timezone`");
            if (q.Get("params") is { } pm) ParamMap(pm, J(path, "params"));
            CheckMarkers(q, path, q.Get("params") as YamlMapping);
            return model is null ? null : new WidgetQuery("derived", false, model, ToObject(q));
        }

        private void ParamMap(YamlNode node, string path)
        {
            if (Map(node, path, "`params`", node) is not { } m) return;
            foreach (var e in m.Entries)
            {
                var ep = J(path, e.Key.Value);
                if (!NameRule.IsMatch(e.Key.Value)) Bad(ViewsCodes.InvalidValue, e.Key, ep, "A request's parameter name is lower-case letters, digits, '-' and '_'.");
                if (Name(e.Value, ep, "The document parameter that supplies it") is { } n && !IsDeclared(n))
                    Bad(ViewsCodes.UndeclaredParameter, e.Value, ep, $"`{n}` is not a parameter of the document.", "Declare it under `parameters`.");
            }
        }

        /// <summary>A <c>{param: name}</c> marker in a request stands for a document parameter of that name, or for the one `params` maps it to.</summary>
        private void CheckMarkers(YamlNode node, string path, YamlMapping? map)
        {
            switch (node)
            {
                case YamlMapping m when m.Entries.Count == 1 && m.Entries[0].Key.Value == "param" && m.Entries[0].Value is YamlScalar { Value: var name }:
                    if (!IsDeclared(name) && map?.Get(name) is null)
                        Bad(ViewsCodes.UndeclaredParameter, m.Entries[0].Value, J(path, "param"), $"`{{param: {name}}}` names a parameter the document does not declare.", "Declare it under `parameters`, or map it in the query's `params`.");
                    break;
                case YamlMapping m:
                    foreach (var e in m.Entries.Where(e => !(e.Key.Value == "params" && path.EndsWith(".query", StringComparison.Ordinal)))) CheckMarkers(e.Value, J(path, e.Key.Value), map);
                    break;
                case YamlSequence s:
                    for (var i = 0; i < s.Items.Count; i++) CheckMarkers(s.Items[i], I(path, i), map);
                    break;
            }
        }
    }
}
