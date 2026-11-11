using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

public static partial class ViewsLoader
{
    private static readonly Regex DurationRule = new("^[1-9][0-9]*[smh]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed partial class Reader
    {
        /// <summary>The kind the caller wants; null accepts either.</summary>
        public ViewKind? Expected { get; init; }

        private ViewKind kind;
        private readonly List<ViewParameter> parameters = [];

        // ---- the document ----

        public ViewDocument? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(ViewsCodes.InvalidValue, root, "", "A dashboard or a report is a mapping with `schemaVersion`, `kind`, `title`, `widgets` and `layouts`."); return null; }

            ViewKind? fileKind = map.Get("kind") is YamlScalar { Value: "dashboard" } ? ViewKind.Dashboard : map.Get("kind") is YamlScalar { Value: "report" } ? ViewKind.Report : null;
            if (!CheckFormat(map, fileKind ?? Expected ?? ViewKind.Dashboard)) return null;
            var kindNode = map.Get("kind");
            if (kindNode is null) { Add(ViewsCodes.MissingKey, map, "Required key `kind` is missing.", "Add `kind: dashboard` or `kind: report`.", "kind"); return null; }
            if (fileKind is null) { Bad(ViewsCodes.InvalidValue, kindNode, "kind", $"`kind` is one of: {string.Join(", ", Choices["kind"])}."); return null; }
            if (Expected is { } want && want != fileKind)
            {
                var is_ = fileKind == ViewKind.Dashboard ? "dashboard" : "report";
                var wants = want == ViewKind.Dashboard ? "dashboard" : "report";
                Bad(ViewsCodes.WrongKind, kindNode, "kind", $"'{File}' is a {is_}, not a {wants}.", $"Read it as a {is_} (`{is_} validate`, the {is_} loader).");
                return null;
            }
            kind = fileKind.Value;
            var self = kind == ViewKind.Dashboard ? "dashboard" : "report";
            var other = kind == ViewKind.Dashboard ? "report" : "dashboard";

            CheckKeysAt(map, Keys[self], $"a {self}", "", e =>
            {
                if (!Keys[other].Contains(e.Key.Value)) return false;
                Bad(ViewsCodes.KeyOfOtherKind, e.Key, e.Key.Value, kind == ViewKind.Report
                    ? $"`{e.Key.Value}` belongs to a dashboard; a report is a run and has no `{e.Key.Value}`."
                    : $"`{e.Key.Value}` belongs to a report; a dashboard has no `{e.Key.Value}`.", $"Keys of a {self}: {string.Join(", ", Keys[self])}.");
                return true;
            });

            var title = map.Get("title") is { } t ? Text(t, "title", "`title`") : MissingKey(map, "title");
            if (map.Get("parameters") is { } p) ReadParameters(p);
            string? font = null;
            if (map.Get("theme") is { } theme && Map(theme, "theme", "`theme`", map) is { } tm)
            {
                CheckKeysAt(tm, Keys["theme"], "`theme`", "theme");
                if (tm.Get("font") is { } f) font = Choice(f, "theme.font", "`theme.font` (a bundled family)", Choices["font"]);
            }

            var locale = map.Get("locale") is { } loc ? Choice(loc, "locale", "`locale` (the language numbers and dates are written in)", Choices["locale"]) : null;

            string? refresh = null, kioskLayout = null; YamlNode? kioskNode = null;
            bool? strict = null, cover = null; int? pages = null, rows = null;
            if (kind == ViewKind.Dashboard)
            {
                if (map.Get("refresh") is { } r && Map(r, "refresh", "`refresh`", map) is { } rm)
                {
                    CheckKeysAt(rm, Keys["refresh"], "`refresh`", "refresh");
                    var every = Required(rm, "every", "refresh");
                    if (every is YamlScalar { Value: var v } && DurationRule.IsMatch(v)) refresh = v;
                    else if (every is not null) Bad(ViewsCodes.InvalidValue, every, "refresh.every", "`every` is a duration: a whole number and s, m or h (30s, 5m, 1h).");
                }
                if (map.Get("kiosk") is { } k && Map(k, "kiosk", "`kiosk`", map) is { } km)
                {
                    CheckKeysAt(km, Keys["kiosk"], "`kiosk`", "kiosk");
                    if (km.Get("layout") is { } kl) { kioskLayout = Name(kl, "kiosk.layout", "`kiosk.layout`"); kioskNode = kl; }
                }
            }
            else
            {
                if (map.Get("strict") is { } s) strict = Flag(s, "strict", "`strict`");
                if (map.Get("limits") is { } l && Map(l, "limits", "`limits`", map) is { } lm)
                {
                    CheckKeysAt(lm, Keys["limits"], "`limits`", "limits");
                    if (lm.Get("pages") is { } lp) pages = Whole(lp, "limits.pages", "`limits.pages`", 1, 500);
                    if (lm.Get("rows") is { } lr) rows = Whole(lr, "limits.rows", "`limits.rows`", 1, 100_000);
                }
                if (map.Get("cover") is { } c && Map(c, "cover", "`cover`", map) is { } cm)
                {
                    CheckKeysAt(cm, Keys["cover"], "`cover`", "cover");
                    if (cm.Get("parameters") is { } cp) cover = Flag(cp, "cover.parameters", "`cover.parameters`");
                }
            }

            var widgets = map.Get("widgets") is { } w ? ReadWidgets(w) : MissingList<ViewWidget>(map, "widgets");
            if (map.Get("links") is { } lk) ReadLinks(lk, widgets);
            ResolveLinkRefs();
            var layouts = map.Get("layouts") is { } ls ? ReadLayouts(ls, widgets) : MissingList<ViewLayout>(map, "layouts");
            if (kind == ViewKind.Report && widgets is not null)
                foreach (var embed in widgets.Where(w => w.Type == "embed"))
                    Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.EmbedInReport, File, embed.Line, embed.Column,
                        "A report has no `embed` widget: a report is paged and exported, and a frame is neither.", "Put the embed in a dashboard, or link to the report server's report from a text widget.", J(J("widgets", embed.Id), "type")));
            if (title is null || widgets is null || layouts is null || Diagnostics.HasErrors()) return null;

            CheckDocument(map, widgets, layouts, kioskLayout, kioskNode);
            if (Diagnostics.HasErrors()) return null;
            return new ViewDocument(kind, title, parameters, widgets, layouts, font, refresh, kioskLayout, strict, pages, rows, cover, locale, links);
        }

        private string? MissingKey(YamlMapping map, string key)
        {
            Add(ViewsCodes.MissingKey, map, $"Required key `{key}` is missing.", $"Add `{key}:`.", key);
            return null;
        }

        private List<T>? MissingList<T>(YamlMapping map, string key)
        {
            MissingKey(map, key);
            return null;
        }

        /// <summary><c>schemaVersion</c>: a whole number this program reads. A newer one is refused naming the kind, the file and both numbers.</summary>
        private bool CheckFormat(YamlMapping map, ViewKind forKind)
        {
            var node = map.Get("schemaVersion");
            if (node is null) { Add(ViewsCodes.MissingKey, map, "Required key `schemaVersion` is missing.", "Write `schemaVersion: 1` first.", "schemaVersion"); return false; }
            if (node is not YamlScalar { Quoted: false } s || !int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            { Bad(ViewsCodes.FormatInvalid, node, "schemaVersion", "`schemaVersion` must be a positive whole number.", "Write `schemaVersion: 1`."); return false; }
            if (number > ViewsFormat.Current) { Bad(ViewsCodes.FormatNewer, node, "schemaVersion", ViewsFormat.Refusal(forKind, File, number), "Upgrade this program; the file was written by a newer version."); return false; }
            if (number < ViewsFormat.Previous) { Bad(ViewsCodes.FormatNewer, node, "schemaVersion", ViewsFormat.Refusal(forKind, File, number)); return false; }
            return true;
        }

        // ---- parameters ----

        private void ReadParameters(YamlNode node)
        {
            if (Seq(node, "parameters", "`parameters`", node, 0, MaximumParameters) is not { } list) return;
            for (var i = 0; i < list.Items.Count; i++)
            {
                var path = I("parameters", i);
                if (Map(list.Items[i], path, "A parameter", list) is not { } m) continue;
                CheckKeysAt(m, Keys["parameter"], "a parameter", path);
                var nameNode = Required(m, "name", path);
                var name = nameNode is null ? null : Name(nameNode, J(path, "name"), "A parameter's `name`");
                var typeNode = Required(m, "type", path);
                var type = typeNode is null ? null : Choice(typeNode, J(path, "type"), "A parameter's `type`", Choices["parameterType"]);
                string? @for = m.Get("for") is { } f ? Field(f, J(path, "for"), "`for` (the attribute the parameter is a value of)") : null;
                var many = m.Get("many") is { } mn && Flag(mn, J(path, "many"), "`many`") == true;
                var required = m.Get("required") is { } rq && Flag(rq, J(path, "required"), "`required`") == true;
                var label = m.Get("label") is { } lb ? Text(lb, J(path, "label"), "`label`") : null;
                if (m.Get("default") is { } d && type is not null) CheckDefault(d, many, type, J(path, "default"));
                if (name is null || type is null) continue;
                if (!declared.Add(name)) { Bad(ViewsCodes.DuplicateName, nameNode!, J(path, "name"), $"The parameter `{name}` is declared twice."); continue; }
                parameters.Add(new ViewParameter(name, type, @for, many, required, m.Get("default") is not null, label, m.Line, m.Get("default") is { } dflt ? ViewsLoader.ToJson(dflt) : null));
            }
        }

        private void CheckDefault(YamlNode node, bool many, string type, string path)
        {
            if (many)
            {
                if (node is not YamlSequence list) { Bad(ViewsCodes.InvalidValue, node, path, "A parameter with `many: true` has a list as its default."); return; }
                foreach (var item in list.Items) CheckDefaultValue(item, type, path);
            }
            else if (node is YamlSequence) Bad(ViewsCodes.InvalidValue, node, path, "A default is one value; make the parameter `many: true` for a list.");
            else CheckDefaultValue(node, type, path);
        }

        private void CheckDefaultValue(YamlNode node, string type, string path)
        {
            if (node is not YamlScalar s) { Bad(ViewsCodes.InvalidValue, node, path, "A default value is a plain value."); return; }
            var fits = type switch
            {
                "integer" => !s.Quoted && long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
                "number" => !s.Quoted && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                "boolean" => !s.Quoted && s.Value is "true" or "false",
                _ => true,
            };
            if (!fits) Bad(ViewsCodes.InvalidValue, node, path, $"`{s.Value}` is not a value of type {type}.");
        }

    }
}
