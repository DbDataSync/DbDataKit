using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

public static partial class ViewsLoader
{
    /// <summary>The most groups a document declares, and the levels one group has (plan 32 section 9h, DBR52).</summary>
    public const int MaximumGroups = 50;
    public const int MinimumGroupLevels = 2;
    public const int MaximumGroupLevels = 4;

    private static readonly Regex TemplatePlaceholder = new(@"\{([0-9]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed partial class Reader
    {
        public IReadOnlyDictionary<string, ViewGroup>? ReadGroupsRoot(YamlNode root) => ReadGroups(root);

        /// <summary>
        /// A document's <c>groups</c> (DBR52): <c>{name: {fields, label, template}}</c>. <c>fields</c> are two to four attributes, outermost first, each a name or <c>{field, format}</c> (the format its text reads under in the
        /// label); <c>template</c> says how the label is made from the levels (<c>{1}</c> is the first level's text), the levels joined by a space when it is left out. A field may not be listed twice in a group.
        /// </summary>
        private IReadOnlyDictionary<string, ViewGroup>? ReadGroups(YamlNode node)
        {
            var result = new Dictionary<string, ViewGroup>(StringComparer.Ordinal);
            if (Map(node, "groups", "`groups`", node) is not { } map) return null;
            if (map.Entries.Count > MaximumGroups) { Bad(ViewsCodes.InvalidValue, map, "groups", $"A document has at most {MaximumGroups} groups."); return null; }
            foreach (var e in map.Entries)
            {
                var path = J("groups", e.Key.Value);
                if (!NameRule.IsMatch(e.Key.Value)) { Bad(ViewsCodes.InvalidValue, e.Key, path, $"`{e.Key.Value}` is not a group name: lower-case letters, digits, '-' and '_', starting with a letter."); continue; }
                if (Map(e.Value, path, $"The group `{e.Key.Value}`", e.Value) is not { } gm) continue;
                CheckKeysAt(gm, Keys["group"], "a group", path);
                var levels = new List<GroupLevel>();
                if (Required(gm, "fields", path) is { } fn && Seq(fn, J(path, "fields"), "`fields`", gm, MinimumGroupLevels, MaximumGroupLevels) is { } list)
                    for (var i = 0; i < list.Items.Count; i++)
                    {
                        var lp = I(J(path, "fields"), i);
                        if (list.Items[i] is YamlMapping lm)
                        {
                            CheckKeysAt(lm, Keys["group_level"], "a level of a group", lp);
                            if (Required(lm, "field", lp) is { } ff && Field(ff, J(lp, "field"), "`field`") is { } field)
                            {
                                if (lm.Get("format") is { } fmt) FormatValue(fmt, J(lp, "format"));
                                levels.Add(new GroupLevel(field, lm.Get("format") is { } f2 ? ToJson(f2) : null));
                            }
                        }
                        else if (Field(list.Items[i], lp, "A level of a group") is { } plain) levels.Add(new GroupLevel(plain, null));
                    }
                var distinct = levels.Select(l => l.Field).Distinct().Count();
                if (distinct != levels.Count) Bad(ViewsCodes.InvalidValue, gm.Get("fields") ?? gm, J(path, "fields"), "A field is listed once in a group.");
                var label = gm.Get("label") is { } lb ? Text(lb, J(path, "label"), "`label`") : null;
                string? template = null;
                if (gm.Get("template") is { } tn)
                {
                    template = Text(tn, J(path, "template"), "`template`", 200);
                    if (template is not null)
                        foreach (Match m in TemplatePlaceholder.Matches(template))
                            if (!int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > Math.Max(levels.Count, MinimumGroupLevels))
                                Bad(ViewsCodes.InvalidValue, tn, J(path, "template"), $"`{m.Value}` names a level the group does not have: use {{1}} to {{{Math.Max(levels.Count, MinimumGroupLevels)}}}.");
                }
                if (levels.Count >= MinimumGroupLevels) result[e.Key.Value] = new ViewGroup(e.Key.Value, levels, label, template, e.Value.Line);
            }
            return result;
        }

        /// <summary>A reference to a group of the document: the name must be declared (when the groups are known).</summary>
        private bool GroupKnown(string name, YamlNode at, string path)
        {
            if (groups is null || groups.ContainsKey(name)) return true;
            Bad(ViewsCodes.UnknownGroup, at, path, $"`{name}` is not a group of this document.", groups.Count == 0 ? "Declare it under `groups`." : $"Groups: {string.Join(", ", groups.Keys)}.");
            return false;
        }

        /// <summary>A chart channel that is a group (<c>{group: name}</c>, with a <c>title</c> and a <c>sort</c>): one field, made of the group's levels.</summary>
        private void GroupChannel(string channel, YamlMapping m, string path)
        {
            if (channel is "y" or "tooltip") Bad(ViewsCodes.InvalidValue, m.Get("group")!, J(path, "group"), $"A group is a set of attributes: it goes in `x`, `color`, `column` or `row`, not `{channel}`.");
            if (m.Get("field") is { } f) Bad(ViewsCodes.InvalidValue, f, J(path, "field"), "A channel is a field or a group, not both.");
            foreach (var bad in new[] { "format", "alias", "filters", "type", "stack" })
                if (m.Get(bad) is { } b) Bad(ViewsCodes.InvalidValue, b, J(path, bad), $"`{bad}` is not for a group: the group's own `template` and the levels' formats say how it reads.");
            if (Name(m.Get("group"), J(path, "group"), "`group`") is { } name) GroupKnown(name, m.Get("group")!, J(path, "group"));
        }
    }
}
