namespace DbData.Config.Yaml;

/// <summary>Closest-match suggestion for unknown keys (case-insensitive edit distance).</summary>
public static class DidYouMean
{
    public static string? Closest(string input, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Distance(input.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        var limit = Math.Max(1, Math.Min(3, input.Length / 3));
        return best != null && bestDistance <= limit ? best : null;
    }

    public static int Distance(string a, string b)
    {
        // optimal string alignment: insert, delete, substitute, and swap of two adjacent characters each cost 1
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}

/// <summary>Shared field-reading helpers for definition and configuration loaders. Problems become diagnostics at the node's position.</summary>
public abstract class YamlFieldReader(string file, List<ConfigDiagnostic> diags, IDiagnosticCatalog? catalog = null)
{
    private readonly IDiagnosticCatalog _catalog = catalog ?? DefaultDiagnosticCatalog.Instance;

    /// <summary>For a mapping merged from several files: the file each node was read from, so a diagnostic names the file the problem is in.</summary>
    public IReadOnlyDictionary<YamlNode, string>? NodeFiles { get; init; }

    protected string File => file;
    protected List<ConfigDiagnostic> Diagnostics => diags;

    protected void Add(string code, YamlNode at, string message, string? fix = null, string? keyPath = null) =>
        diags.Add(_catalog.Create(code, NodeFiles != null && NodeFiles.TryGetValue(at, out var origin) ? origin : file, at.Line, at.Column, message, fix, keyPath));

    protected void CheckKeys(YamlMapping map, IEnumerable<string> allowed, string where)
    {
        var allowedList = allowed.ToList();
        foreach (var e in map.Entries.Where(e => !allowedList.Contains(e.Key.Value)))
        {
            var near = DidYouMean.Closest(e.Key.Value, allowedList);
            Add(SharedCodes.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in {where}." + (near != null ? $" Did you mean `{near}`?" : ""), $"Keys: {string.Join(", ", allowedList)}.");
        }
    }

    protected YamlScalar? Scalar(YamlMapping map, string key, bool required, YamlNode at)
    {
        var node = map.Get(key);
        if (node == null)
        {
            if (required) Add(SharedCodes.MissingKey, at, $"Required key `{key}` is missing.", $"Add `{key}:`.");
            return null;
        }
        if (node is YamlScalar s && s.Value.Length > 0) return s;
        Add(SharedCodes.InvalidValue, node, $"`{key}` must be a non-empty string.");
        return null;
    }

    /// <summary>Reads a list of non-empty strings. An empty list is reported as invalid unless <paramref name="allowEmpty"/>.</summary>
    protected List<YamlScalar>? StringList(YamlMapping map, string key, bool required, bool allowEmpty = true, bool unique = false)
    {
        var node = map.Get(key);
        if (node == null)
        {
            if (required) Add(SharedCodes.MissingKey, map, $"Required key `{key}` is missing.");
            return null;
        }
        if (node is not YamlSequence seq) { Add(SharedCodes.InvalidValue, node, $"`{key}` must be a list, for example `{key}: [a, b]`."); return null; }
        var list = new List<YamlScalar>();
        foreach (var item in seq.Items)
        {
            if (item is YamlScalar s && s.Value.Length > 0) list.Add(s);
            else Add(SharedCodes.InvalidValue, item, $"Entries of `{key}` must be non-empty strings.");
        }
        if (!allowEmpty && seq.Items.Count == 0) Add(SharedCodes.InvalidValue, seq, $"`{key}` must not be an empty list.");
        if (unique)
            foreach (var dup in list.GroupBy(x => x.Value).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)))
                Add(SharedCodes.InvalidValue, dup, $"`{dup.Value}` is listed more than once in `{key}`.");
        return list;
    }
}
