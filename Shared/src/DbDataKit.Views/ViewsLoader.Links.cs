using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

public static partial class ViewsLoader
{
    /// <summary>A name inside a link's <c>with</c>, <c>new</c>, <c>edit</c> or <c>lock</c>: a parameter or a field of the target, whose own rule allows either case.</summary>
    private static readonly Regex LinkValueName = new("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A <c>$field</c> of the clicked row: a logical field (<c>orders.id</c>) or the alias of a query output.</summary>
    private static readonly Regex RowField = new(@"^\$[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a value of type <paramref name="given"/> (a literal's JSON type, or a document parameter's type) fits a parameter or field of type <paramref name="wanted"/>: the same type, an integer for a number, and a
    /// string for a date (a date is written <c>yyyy-MM-dd</c>); a list only for a list. The rule is the one of <c>linkTypeFits</c> in <c>@dbdatakit/visuals</c>.
    /// </summary>
    public static bool LinkTypeFits(string given, bool givenMany, string wanted, bool wantedMany) =>
        givenMany == wantedMany && (wanted == "any" || given == wanted || (wanted == "number" && given == "integer") || (wanted == "date" && given == "string"));

    private sealed partial class Reader
    {
        private readonly Dictionary<string, ViewLink> links = [];
        private readonly List<(string Name, YamlNode At, string Path)> linkRefs = [];

        /// <summary>Reads the document's <c>links</c> (the widgets are known, so <c>then</c> can name them).</summary>
        private void ReadLinks(YamlNode node, IReadOnlyList<ViewWidget>? widgets)
        {
            if (Map(node, "links", "`links`", node) is not { } map) return;
            if (map.Entries.Count > MaximumLinks) { Bad(ViewsCodes.InvalidValue, map, "links", $"A document has at most {MaximumLinks} links."); return; }
            foreach (var e in map.Entries)
            {
                if (!NameRule.IsMatch(e.Key.Value)) { Bad(ViewsCodes.InvalidValue, e.Key, J("links", e.Key.Value), $"`{e.Key.Value}` is not a link name: lower-case letters, digits, '-' and '_', starting with a letter."); continue; }
                if (ReadLink(e.Key.Value, e.Value, widgets) is { } link) links[link.Name] = link;
            }
        }

        private void AddAt(string code, int line, int column, string message, string? fix, string path) =>
            Diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(code, File, line, column, message, fix, path));

        private ViewLink? ReadLink(string name, YamlNode node, IReadOnlyList<ViewWidget>? widgets)
        {
            var path = J("links", name);
            if (Map(node, path, $"The link `{name}`", node) is not { } m) return null;
            CheckKeysAt(m, Keys["link"], "a link", path);

            var targets = Choices["linkTarget"].Where(k => m.Get(k) is not null).ToList();
            if (targets.Count != 1)
            {
                Bad(ViewsCodes.InvalidValue, m, path, targets.Count == 0
                    ? "A link names its target: one of `dashboard`, `report`, `view`, `page` or `form`."
                    : $"A link has one target: {string.Join(", ", targets.Select(t => $"`{t}`"))} are written together.");
                return null;
            }
            var kind = targets[0];
            var target = Name(m.Get(kind), J(path, kind), $"A link's `{kind}`");
            var isForm = kind == "form";

            foreach (var key in new[] { "with", "new", "edit" })
                if (m.Get(key) is { } misplaced && (isForm ? key == "with" : key != "with"))
                    Bad(ViewsCodes.InvalidValue, m.Entries.First(e => e.Key.Value == key).Key, J(path, key),
                        isForm ? "A form is given its fields with `new` (a row to create) or `edit` (a row to change), not `with`." : $"`{key}` belongs to a form link; a {kind} is given its parameters with `with`.");
            if (isForm && m.Get("new") is not null && m.Get("edit") is not null)
                Bad(ViewsCodes.InvalidValue, m.Entries.First(e => e.Key.Value == "edit").Key, J(path, "edit"), "A form link creates a row (`new`) or changes one (`edit`), not both.");

            var valuesKey = isForm ? (m.Get("edit") is not null && m.Get("new") is null ? "edit" : "new") : "with";
            var values = new Dictionary<string, LinkValue>();
            if (m.Get(valuesKey) is { } vn) ReadLinkValues(vn, J(path, valuesKey), values);

            var @as = m.Get("as") is { } an ? Choice(an, J(path, "as"), "`as`", Choices["linkAs"]) ?? "navigate" : "navigate";

            var thenKind = isForm ? "refresh" : "none";
            var thenWidgets = new List<string>();
            if (m.Get("then") is { } then)
            {
                var tp = J(path, "then");
                if (then is YamlSequence list)
                {
                    thenKind = "widgets";
                    if (Seq(list, tp, "`then`", m, 1, MaximumWidgets) is not null)
                        for (var i = 0; i < list.Items.Count; i++)
                            if (Name(list.Items[i], I(tp, i), "A widget in `then`") is { } w)
                            {
                                if (widgets is not null && !widgets.Any(x => x.Id == w)) Bad(ViewsCodes.UnknownWidget, list.Items[i], I(tp, i), $"`then` names `{w}`, which is not a widget of this document.");
                                else thenWidgets.Add(w);
                            }
                }
                else if (Choice(then, tp, "`then` (or a list of widgets)", Choices["linkThen"]) is { } tk) thenKind = tk;
            }

            var lockList = new List<string>();
            if (m.Get("lock") is { } lk)
            {
                var lp = J(path, "lock");
                if (!isForm) Bad(ViewsCodes.InvalidValue, m.Entries.First(e => e.Key.Value == "lock").Key, lp, "`lock` belongs to a form link: it makes a prefilled field read-only.");
                else if (Seq(lk, lp, "`lock`", m, 1, 100) is { } ls)
                    for (var i = 0; i < ls.Items.Count; i++)
                        if (ls.Items[i] is YamlScalar { Value: var f } && LinkValueName.IsMatch(f))
                        {
                            if (!values.ContainsKey(f)) Bad(ViewsCodes.InvalidValue, ls.Items[i], I(lp, i), $"`{f}` is locked but not given: lock a field that `{valuesKey}` fills.");
                            else lockList.Add(f);
                        }
                        else Bad(ViewsCodes.InvalidValue, ls.Items[i], I(lp, i), "A locked field is a field name of the form.");
            }

            var print = m.Get("print") is { } pr ? Choice(pr, J(path, "print"), "`print`", Choices["linkPrint"]) : null;
            var image = m.Get("image") is { } im ? Choice(im, J(path, "image"), "`image`", Choices["linkPrint"]) : null;
            var label = m.Get("label") is { } lb ? Text(lb, J(path, "label"), "`label`") : null;
            if (print == "link" && @as != "navigate")
                Bad(ViewsCodes.LinkNoAddress, m.Get("print")!, J(path, "print"), $"`print: link` needs a target with an address of its own, and `as: {@as}` shows it in a frame of the shell.", "Use `print: normal` or `print: hidden`, or `as: navigate`.");

            if (target is null) return null;
            var link = new ViewLink(name, kind, target, valuesKey, values, @as, thenKind, thenWidgets, lockList, print, image, label, m.Line, m.Column);
            CheckLinkTarget(link, m, path);
            return link;
        }

        private void ReadLinkValues(YamlNode node, string path, Dictionary<string, LinkValue> values)
        {
            if (Map(node, path, "A link's values", node) is not { } map) return;
            foreach (var e in map.Entries)
            {
                var vp = J(path, e.Key.Value);
                if (!LinkValueName.IsMatch(e.Key.Value)) { Bad(ViewsCodes.InvalidValue, e.Key, vp, "A name is a parameter or field of the target: letters, digits, '-' and '_', starting with a letter."); continue; }
                if (e.Value is not YamlScalar s) { Bad(ViewsCodes.InvalidValue, e.Value, vp, "A value is `$field` of the clicked row, `$params.name`, or a literal."); continue; }
                if (s.Value.StartsWith("$params.", StringComparison.Ordinal))
                {
                    var p = s.Value[8..];
                    if (!IsDeclared(p)) { Bad(ViewsCodes.UndeclaredParameter, s, vp, $"`{s.Value}` names a parameter the document does not declare.", "Declare it under `parameters`."); continue; }
                    values[e.Key.Value] = new LinkValue(LinkValueKind.Param, p, "", s.Line, s.Column);
                }
                else if (s.Value.StartsWith('$'))
                {
                    if (!RowField.IsMatch(s.Value)) { Bad(ViewsCodes.InvalidValue, s, vp, "`$` starts a field of the clicked row (`$orders.id`) or a parameter (`$params.name`)."); continue; }
                    values[e.Key.Value] = new LinkValue(LinkValueKind.Field, s.Value[1..], "", s.Line, s.Column);
                }
                else
                {
                    var type = ToJson(s)?.GetValueKind() switch
                    {
                        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
                        System.Text.Json.JsonValueKind.Number => long.TryParse(s.Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _) ? "integer" : "number",
                        _ => "string",
                    };
                    values[e.Key.Value] = new LinkValue(LinkValueKind.Literal, s.Value, type, s.Line, s.Column);
                }
            }
        }

        /// <summary>The checks that need the app: the target exists, every name is the target's, each value fits its type, a required parameter is given.</summary>
        private void CheckLinkTarget(ViewLink link, YamlMapping at, string path)
        {
            if (targets is null) return;
            var targetPath = J(path, link.TargetKind);
            if (!targets.TryGetValue(link.TargetKey, out var theirs))
            {
                Bad(ViewsCodes.LinkTargetUnknown, at.Get(link.TargetKind)!, targetPath, $"The link `{link.Name}` leads to the {link.TargetKind} `{link.Target}`, which this app does not have: a link stays in the app instance.",
                    "Name a view of the app, or add it to the app first.");
                return;
            }
            var byName = theirs.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var valuesPath = J(path, link.ValuesKey);
            foreach (var (name, value) in link.Values)
            {
                var vp = J(valuesPath, name);
                if (!byName.TryGetValue(name, out var param))
                {
                    AddAt(ViewsCodes.LinkNameUnknown, value.Line, value.Column, $"The {link.TargetKind} `{link.Target}` has no {(link.TargetKind == "form" ? "field" : "parameter")} `{name}`.", $"It has: {string.Join(", ", byName.Keys.Order(StringComparer.Ordinal))}.", vp);
                    continue;
                }
                if (value.Kind == LinkValueKind.Field) continue; // the clicked row's type is the catalog's to say
                var given = value.Kind == LinkValueKind.Literal ? value.LiteralType : parameters.First(p => p.Name == value.Text).Type;
                var givenMany = value.Kind == LinkValueKind.Param && parameters.First(p => p.Name == value.Text).Many;
                if (!LinkTypeFits(given, givenMany, param.Type, param.Many))
                    AddAt(ViewsCodes.LinkTypeMismatch, value.Line, value.Column,
                        $"`{name}` of the {link.TargetKind} `{link.Target}` is {(param.Many ? "a list of " : "")}{param.Type}, and this value is {(givenMany ? "a list of " : "")}{given}.", "Give a value of the target's type.", vp);
            }
            for (var i = 0; i < link.Lock.Count; i++)
                if (!byName.ContainsKey(link.Lock[i]))
                    AddAt(ViewsCodes.LinkNameUnknown, at.Line, at.Column, $"The form `{link.Target}` has no field `{link.Lock[i]}` to lock.", null, I(J(path, "lock"), i));
            if (link.ValuesKey != "new")
                foreach (var p in theirs.Where(p => p.Required && !p.HasDefault && !link.Values.ContainsKey(p.Name)))
                    Bad(ViewsCodes.DrillMissingParameter, at, path, $"The link `{link.Name}` to the {link.TargetKind} `{link.Target}` does not give its required {(link.TargetKind == "form" ? "key" : "parameter")} `{p.Name}`.", $"Add `{p.Name}:` under `{link.ValuesKey}`.");
        }

        /// <summary>A widget's <c>on_click</c>: a link of the document, by name. Resolved once the links are read.</summary>
        private void ReadOnClick(YamlMapping widget, string path)
        {
            if (widget.Get("on_click") is not { } oc) return;
            var cp = J(path, "on_click");
            if (Name(oc, cp, "`on_click`") is not { } name) return;
            if (widget.Get("drill") is not null) { Bad(ViewsCodes.InvalidValue, oc, cp, "A widget follows `on_click` (a link of the document) or `drill` (the same thing written in place), not both."); return; }
            linkRefs.Add((name, oc, cp));
        }

        /// <summary>Every <c>on_click</c> names a declared link. Not made for a widgets map read alone: it has no links.</summary>
        private void ResolveLinkRefs()
        {
            foreach (var (name, at, path) in linkRefs)
                if (!links.ContainsKey(name))
                    Bad(ViewsCodes.LinkNotDeclared, at, path, $"`{name}` is not a link of this document.", links.Count == 0 ? "Declare it under `links`." : $"Links: {string.Join(", ", links.Keys)}.");
        }
    }
}
