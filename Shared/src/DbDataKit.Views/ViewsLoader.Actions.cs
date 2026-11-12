using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

public static partial class ViewsLoader
{
    /// <summary>A <c>{{field}}</c> placeholder of a confirmation's title: a field of the clicked row, or <c>ask.name</c> of what the person fills in.</summary>
    public static readonly Regex TitlePlaceholder = new(@"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModelName = new("^[A-Za-z][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EntityOrField = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed partial class Reader
    {
        /// <summary>
        /// An action link (plan 32 DBR53): <c>change</c> (an insert, an edit or a delete of a Store row) or <c>task</c> (a runner task), with the popup's <c>confirm</c> and the <c>ask</c> fields. The shell opens no target for it:
        /// its popup is the shell's own, so <c>as</c>, <c>lock</c>, <c>with</c>, <c>new</c> and <c>edit</c> are refused.
        /// </summary>
        private ViewLink? ReadAction(string name, YamlMapping m, string path, string kind, IReadOnlyList<string> otherTargets)
        {
            if (otherTargets.Count > 0)
            {
                Bad(ViewsCodes.InvalidValue, m, path, $"A link has one target: `{kind}` and {string.Join(", ", otherTargets.Select(t => $"`{t}`"))} are written together.");
                return null;
            }
            foreach (var key in new[] { "with", "new", "edit", "as", "lock" })
                if (m.Entries.FirstOrDefault(e => e.Key.Value == key) is { } e)
                    Bad(ViewsCodes.InvalidValue, e.Key, J(path, key), $"`{key}` does not belong to an action: its popup is the shell's, and what it changes is under `{kind}`.");

            var asks = ReadAsks(m, path);
            var node = m.Get(kind)!;
            var kp = J(path, kind);
            var set = new Dictionary<string, LinkValue>();
            var key2 = new Dictionary<string, LinkValue>();
            LinkValue? version = null;
            string operation = "run";
            string? model = null, entity = null, package = null, task = null;
            if (Map(node, kp, $"A link's `{kind}`", node) is { } a)
            {
                if (kind == "change")
                {
                    CheckKeysAt(a, ["model", "entity", "insert", "edit", "delete", "set"], "a change", kp);
                    model = ReadWord(a, "model", kp, ModelName, "a model name");
                    entity = ReadWord(a, "entity", kp, EntityOrField, "an entity name");
                    var ops = new[] { "insert", "edit", "delete" }.Where(k => a.Get(k) is not null).ToList();
                    if (ops.Count != 1) Bad(ViewsCodes.InvalidValue, a, kp, ops.Count == 0 ? "A change is an `insert`, an `edit` or a `delete`." : $"A change is one of `insert`, `edit`, `delete`: {string.Join(", ", ops.Select(o => $"`{o}`"))} are written together.");
                    else
                    {
                        operation = ops[0];
                        var op = a.Get(operation)!;
                        var opPath = J(kp, operation);
                        if (operation == "insert")
                        {
                            ReadLinkValues(op, opPath, set, asks);
                            if (a.Get("set") is { } stray) Bad(ViewsCodes.InvalidValue, stray, J(kp, "set"), "An insert gives its values under `insert`; `set` belongs to an `edit`.");
                        }
                        else
                        {
                            ReadKeyAndVersion(op, opPath, operation, key2, ref version, asks);
                            if (operation == "edit")
                            {
                                if (a.Get("set") is { } sn) ReadLinkValues(sn, J(kp, "set"), set, asks);
                                else Bad(ViewsCodes.MissingKey, a, J(kp, "set"), "An edit says what it sets: add `set:` with the fields and their new values.");
                                if (set.Count == 0 && a.Get("set") is { } empty && empty is YamlMapping) Bad(ViewsCodes.InvalidValue, empty, J(kp, "set"), "An edit sets at least one field.");
                            }
                            else if (a.Get("set") is { } stray) Bad(ViewsCodes.InvalidValue, stray, J(kp, "set"), "A delete sets nothing.");
                        }
                    }
                }
                else
                {
                    CheckKeysAt(a, ["package", "task", "inputs"], "a task", kp);
                    package = ReadWord(a, "package", kp, ModelName, "a package name");
                    task = ReadWord(a, "task", kp, ModelName, "a task name");
                    if (a.Get("inputs") is { } inn) ReadLinkValues(inn, J(kp, "inputs"), set, asks);
                }
            }

            var confirm = ReadConfirm(m, path, asks, operation);
            var then = m.Get("then") is { } tn ? ReadActionThen(tn, J(path, "then")) : ("refresh", (IReadOnlyList<string>)[]);
            var print = m.Get("print") is { } pr ? Choice(pr, J(path, "print"), "`print`", Choices["linkPrint"]) : null;
            var image = m.Get("image") is { } im ? Choice(im, J(path, "image"), "`image`", Choices["linkPrint"]) : null;
            var label = m.Get("label") is { } lb ? Text(lb, J(path, "label"), "`label`") : null;
            if (print == "link") Bad(ViewsCodes.LinkNoAddress, m.Get("print")!, J(path, "print"), "`print: link` needs a target with an address of its own, and an action has none: its popup is the shell's.", "Use `print: normal` or `print: hidden`.");

            var target = kind == "change" ? $"{model}.{entity}" : $"{package}.{task}";
            var definition = new ActionDefinition(kind, operation, model, entity, package, task, key2, version, set, confirm, asks);
            return new ViewLink(name, kind, target, kind == "change" ? operation : "inputs", set, "dialog", then.Item1, then.Item2, [], print, image, label, m.Line, m.Column, definition);
        }

        private string? ReadWord(YamlMapping map, string key, string path, Regex rule, string what)
        {
            if (map.Get(key) is not { } n) { Add(ViewsCodes.MissingKey, map, $"Required key `{key}` is missing.", $"Add `{key}:`.", J(path, key)); return null; }
            if (n is YamlScalar s && rule.IsMatch(s.Value)) return s.Value;
            Bad(ViewsCodes.InvalidValue, n, J(path, key), $"`{key}` is {what}.");
            return null;
        }

        private (string, IReadOnlyList<string>) ReadActionThen(YamlNode then, string tp)
        {
            if (then is YamlSequence list)
            {
                var names = new List<string>();
                if (Seq(list, tp, "`then`", then, 1, MaximumWidgets) is not null)
                    for (var i = 0; i < list.Items.Count; i++)
                        if (Name(list.Items[i], I(tp, i), "A widget in `then`") is { } w)
                        {
                            if (!actionWidgets.Contains(w)) Bad(ViewsCodes.UnknownWidget, list.Items[i], I(tp, i), $"`then` names `{w}`, which is not a widget of this document.");
                            else names.Add(w);
                        }
                return ("widgets", names);
            }
            return (Choice(then, tp, "`then` (or a list of widgets)", Choices["linkThen"]) ?? "refresh", []);
        }

        private HashSet<string> actionWidgets = [];

        private void ReadKeyAndVersion(YamlNode op, string path, string operation, Dictionary<string, LinkValue> key, ref LinkValue? version, IReadOnlyDictionary<string, ActionAsk> asks)
        {
            if (Map(op, path, $"A {operation}'s row", op) is not { } m) return;
            CheckKeysAt(m, ["key", "version"], $"an {operation}", path, e => false);
            if (m.Get("key") is not { } kn) Add(ViewsCodes.MissingKey, m, "The row is named by its `key`.", "Add `key:` with `$field` of the row.", J(path, "key"));
            else if (kn is YamlMapping km) ReadLinkValues(km, J(path, "key"), key, asks);
            else if (ReadActionValue(kn, J(path, "key"), asks) is { } kv) key["key"] = kv;
            if (m.Get("version") is { } vn && ReadActionValue(vn, J(path, "version"), asks) is { } v) version = v;
        }

        /// <summary>One value of an action: as a link's, and <c>$ask.name</c> is what the person fills in.</summary>
        private LinkValue? ReadActionValue(YamlNode node, string path, IReadOnlyDictionary<string, ActionAsk> asks)
        {
            if (ReadLinkValue(node, path) is not { } v) return null;
            if (v.Kind != LinkValueKind.Field || !v.Text.StartsWith("ask.", StringComparison.Ordinal)) return v;
            if (asks.ContainsKey(v.Text[4..])) return v with { Kind = LinkValueKind.Ask, Text = v.Text[4..] };
            AddAt(ViewsCodes.ActionAskUnknown, v.Line, v.Column, $"`${v.Text}` names a field the action does not ask for.", asks.Count == 0 ? "Declare it under `ask`." : $"It asks for: {string.Join(", ", asks.Keys)}.", path);
            return null;
        }

        private IReadOnlyDictionary<string, ActionAsk> ReadAsks(YamlMapping m, string path)
        {
            var result = new Dictionary<string, ActionAsk>();
            if (m.Get("ask") is not { } an) return result;
            var ap = J(path, "ask");
            if (Map(an, ap, "`ask`", an) is not { } map) return result;
            if (map.Entries.Count > 20) { Bad(ViewsCodes.InvalidValue, map, ap, "An action asks for at most 20 fields."); return result; }
            foreach (var e in map.Entries)
            {
                var fp = J(ap, e.Key.Value);
                if (!LinkValueName.IsMatch(e.Key.Value)) { Bad(ViewsCodes.InvalidValue, e.Key, fp, "An asked field has a name of letters, digits, '-' and '_', starting with a letter."); continue; }
                if (Map(e.Value, fp, $"The asked field `{e.Key.Value}`", e.Value) is not { } f) continue;
                CheckKeysAt(f, ["label", "type", "choices", "required", "default"], "an asked field", fp);
                var type = f.Get("type") is { } tn ? Choice(tn, J(fp, "type"), "`type`", Choices["askType"]) ?? "string" : "string";
                var choices = new List<string>();
                if (f.Get("choices") is { } cn && Seq(cn, J(fp, "choices"), "`choices`", f, 1, 200) is { } cs)
                    for (var i = 0; i < cs.Items.Count; i++)
                        if (Text(cs.Items[i], I(J(fp, "choices"), i), "A choice", 200) is { } c) choices.Add(c);
                if (choices.Count > 0 && type is not ("string" or "integer" or "number")) Bad(ViewsCodes.InvalidValue, f.Get("choices")!, J(fp, "choices"), "`choices` offers values of a text or a number field.");
                var label = f.Get("label") is { } ln ? Text(ln, J(fp, "label"), "`label`") ?? e.Key.Value : e.Key.Value;
                var required = f.Get("required") is { } rn ? Flag(rn, J(fp, "required"), "`required`") ?? true : true;
                string? def = f.Get("default") is YamlScalar { } ds ? ds.Value : null;
                if (f.Get("default") is { } dn && dn is not YamlScalar) Bad(ViewsCodes.InvalidValue, dn, J(fp, "default"), "A `default` is one value.");
                if (def is not null && choices.Count > 0 && !choices.Contains(def)) Bad(ViewsCodes.InvalidValue, f.Get("default")!, J(fp, "default"), $"The default `{def}` is not one of the choices.");
                result[e.Key.Value] = new ActionAsk(e.Key.Value, label, type, choices, required, def);
            }
            return result;
        }

        private ActionConfirm ReadConfirm(YamlMapping m, string path, IReadOnlyDictionary<string, ActionAsk> asks, string operation)
        {
            if (m.Get("confirm") is not { } cn) return new ActionConfirm(null, [], null);
            var cp = J(path, "confirm");
            if (Map(cn, cp, "`confirm`", cn) is not { } c) return new ActionConfirm(null, [], null);
            CheckKeysAt(c, ["title", "show", "button"], "a confirmation", cp);
            string? title = null;
            if (c.Get("title") is { } tn && Text(tn, J(cp, "title"), "`title`", 300) is { } t)
            {
                title = t;
                foreach (Match ph in TitlePlaceholder.Matches(t))
                    CheckPlaceholder(ph.Groups[1].Value, tn, J(cp, "title"), asks);
                if (t.Contains("{{", StringComparison.Ordinal) && TitlePlaceholder.Replace(t, "").Contains("{{", StringComparison.Ordinal))
                    Bad(ViewsCodes.InvalidValue, tn, J(cp, "title"), "A placeholder is `{{orders.number}}`: the name of a field of the row, or `{{ask.name}}`.");
            }
            var show = new List<string>();
            if (c.Get("show") is { } sn && Seq(sn, J(cp, "show"), "`show`", c, 1, 20) is { } ss)
                for (var i = 0; i < ss.Items.Count; i++)
                    if (ss.Items[i] is YamlScalar { Value: var f } && RowFieldName.IsMatch(f)) { CheckPlaceholder(f, ss.Items[i], I(J(cp, "show"), i), asks); show.Add(f); }
                    else Bad(ViewsCodes.InvalidValue, ss.Items[i], I(J(cp, "show"), i), "A field shown is a field of the row (`orders.number`) or `ask.name`.");
            var button = c.Get("button") is { } bn ? Text(bn, J(cp, "button"), "`button`", 40) : null;
            return new ActionConfirm(title, show, button);
        }

        private static readonly Regex RowFieldName = new(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private void CheckPlaceholder(string name, YamlNode at, string path, IReadOnlyDictionary<string, ActionAsk> asks)
        {
            if (name.StartsWith("ask.", StringComparison.Ordinal) && !asks.ContainsKey(name[4..]))
                Bad(ViewsCodes.ActionAskUnknown, at, path, $"`{name}` names a field the action does not ask for.", asks.Count == 0 ? "Declare it under `ask`." : $"It asks for: {string.Join(", ", asks.Keys)}.");
        }

        private void ReadLinkValues(YamlNode node, string path, Dictionary<string, LinkValue> values, IReadOnlyDictionary<string, ActionAsk> asks)
        {
            if (Map(node, path, "An action's values", node) is not { } map) return;
            foreach (var e in map.Entries)
            {
                var vp = J(path, e.Key.Value);
                if (!LinkValueName.IsMatch(e.Key.Value)) { Bad(ViewsCodes.InvalidValue, e.Key, vp, "A name is a field of the entity (or an input of the task): letters, digits, '-' and '_', starting with a letter."); continue; }
                if (ReadActionValue(e.Value, vp, asks) is { } v) values[e.Key.Value] = v;
            }
        }
    }
}
