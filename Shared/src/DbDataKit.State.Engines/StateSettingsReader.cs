using System.Globalization;

namespace DbDataKit.State;

/// <summary>
/// Reads the <c>state:</c> section of a service's <c>server.yaml</c> (plan 29 sections 3.2 and 3.3), strictly: a key that is not a setting is an error naming its path and the nearest
/// key that is, never ignored. The section arrives as what a YAML reader produces (dictionaries, lists, scalars), so this does not depend on a YAML library.
/// <code>
/// state:
///   engine: postgres
///   connection: Host=db;Database=dbdatakit;Username=svc;Password=secret:state-db
///   registry:   { schema: dbdatakit }
///   namespaces:
///     default:  { schema: dbdatakit, prefix: "{module}_" }   # for every module that has no entry of its own
///     identity: { schema: ident }
///   retention:
///     days: 30                          # a number of days, 0.25 is six hours
///     modules: { runs: 7 }
///     tables:  { runs.run: 14, audit.entry: forever }
///   housekeeping:                       # the server's own upkeep of this state, in its own process (plan 35 I1)
///     retention_every_minutes: 60
///     backup_directory: backups         # none: no snapshot
///     backup_every_hours: 24
///     backup_keep: 7
/// </code>
/// </summary>
public static class StateSettingsReader
{
    private static readonly string[] SectionKeys = ["engine", "connection", "registry", "namespaces", "retention", "housekeeping"];
    private static readonly string[] NamespaceKeys = ["schema", "prefix", "file"];
    private static readonly string[] RetentionKeys = ["days", "modules", "tables"];
    private static readonly string[] HousekeepingKeys = ["retention_every_minutes", "backup_directory", "backup_every_hours", "backup_keep"];

    /// <summary>The settings in <paramref name="section"/>; null or empty is the defaults (SQLite, a file).</summary>
    /// <param name="section">The section as a YAML reader gives it: dictionaries, lists and scalars.</param>
    /// <param name="path">What the section is called in messages (<c>state</c>).</param>
    public static StateSettings Read(IReadOnlyDictionary<string, object?>? section, string path = "state")
    {
        if (section is null) return new StateSettings();
        Known(section, SectionKeys, path);

        return new StateSettings(Text(section, "engine", path), Text(section, "connection", path))
        {
            Namespaces = ReadNamespaces(section, path),
            Retention = ReadRetention(section, path),
            Housekeeping = ReadHousekeeping(section, path),
        };
    }

    private static StateHousekeepingSettings ReadHousekeeping(IReadOnlyDictionary<string, object?> section, string path)
    {
        var map = Map(section, "housekeeping", path);
        if (map is null) return StateHousekeepingSettings.Defaults;
        var at = $"{path}.housekeeping";
        Known(map, HousekeepingKeys, at);
        var result = StateHousekeepingSettings.Defaults;
        if (Positive(map, "retention_every_minutes", at) is { } minutes) result = result with { RetentionEvery = TimeSpan.FromMinutes(minutes) };
        if (Text(map, "backup_directory", at) is { Length: > 0 } directory) result = result with { BackupDirectory = directory };
        if (Positive(map, "backup_every_hours", at) is { } hours) result = result with { BackupEvery = TimeSpan.FromHours(hours) };
        if (Text(map, "backup_keep", at) is { } keepText)
        {
            if (!int.TryParse(keepText, NumberStyles.None, CultureInfo.InvariantCulture, out var keep) || keep < 1)
                throw new InvalidOperationException($"{at}.backup_keep is '{keepText}'; write a whole number of at least 1.");
            result = result with { BackupKeep = keep };
        }

        return result;
    }

    private static double? Positive(IReadOnlyDictionary<string, object?> map, string key, string path)
    {
        if (Text(map, key, path) is not { } text) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0 || double.IsInfinity(value))
            throw new InvalidOperationException($"{path}.{key} is '{text}'; write a number above zero.");
        return value;
    }

    private static StateNamespaceSettings ReadNamespaces(IReadOnlyDictionary<string, object?> section, string path)
    {
        var registry = Namespace(Map(section, "registry", path), $"{path}.registry");
        var modules = new Dictionary<string, NamespaceSetting>(StringComparer.Ordinal);
        var all = Map(section, "namespaces", path);
        NamespaceSetting defaults = new();
        if (all is not null)
        {
            foreach (var (name, value) in all)
            {
                var setting = Namespace(value as IReadOnlyDictionary<string, object?> ?? (value is null ? null : throw NotAMap($"{path}.namespaces.{name}")), $"{path}.namespaces.{name}") ?? new NamespaceSetting();
                if (name == "default") defaults = setting;
                else modules[name] = setting;
            }
        }

        return new StateNamespaceSettings { Default = defaults, Modules = modules, Registry = registry ?? new NamespaceSetting() };
    }

    private static NamespaceSetting? Namespace(IReadOnlyDictionary<string, object?>? map, string path)
    {
        if (map is null) return null;
        Known(map, NamespaceKeys, path);
        return new NamespaceSetting(Text(map, "schema", path), Text(map, "prefix", path), Text(map, "file", path));
    }

    private static RetentionSettings ReadRetention(IReadOnlyDictionary<string, object?> section, string path)
    {
        var map = Map(section, "retention", path);
        if (map is null) return new RetentionSettings();
        var at = $"{path}.retention";
        Known(map, RetentionKeys, at);
        return new RetentionSettings
        {
            Days = map.TryGetValue("days", out var days) && days is not null ? Days(days, $"{at}.days") : null,
            Modules = DaysMap(Map(map, "modules", at), $"{at}.modules"),
            Tables = DaysMap(Map(map, "tables", at), $"{at}.tables"),
        };
    }

    private static Dictionary<string, double> DaysMap(IReadOnlyDictionary<string, object?>? map, string path) =>
        map is null ? [] : map.ToDictionary(pair => pair.Key, pair => Days(pair.Value, $"{path}.{pair.Key}"), StringComparer.Ordinal);

    private static double Days(object? value, string path) => value switch
    {
        string text when text.Trim().Equals("forever", StringComparison.OrdinalIgnoreCase) => RetentionSettings.Forever,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0 => number,
        int or long or double or float or decimal when Convert.ToDouble(value, CultureInfo.InvariantCulture) > 0 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"{path} is '{value}'; write a number of days above zero (0.25 is six hours), or the word forever."),
    };

    private static void Known(IReadOnlyDictionary<string, object?> map, string[] known, string path)
    {
        foreach (var key in map.Keys)
        {
            if (known.Contains(key, StringComparer.Ordinal)) continue;
            var near = known.OrderBy(k => Distance(k, key)).First();
            var hint = Distance(near, key) <= 3 ? $"; did you mean '{near}'?" : "";
            throw new InvalidOperationException($"{path}.{key} is not a state setting (the settings here are {string.Join(", ", known)}){hint}");
        }
    }

    private static string? Text(IReadOnlyDictionary<string, object?> map, string key, string path) => map.TryGetValue(key, out var value) ? value switch
    {
        null => null,
        string text => text,
        bool or int or long or double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"{path}.{key} is a list or a map; it is text."),
    } : null;

    private static IReadOnlyDictionary<string, object?>? Map(IReadOnlyDictionary<string, object?> map, string key, string path) => map.TryGetValue(key, out var value) ? value switch
    {
        null => null,
        IReadOnlyDictionary<string, object?> nested => nested,
        _ => throw NotAMap($"{path}.{key}"),
    } : null;

    private static InvalidOperationException NotAMap(string path) => new($"{path} has to be a set of keys (a map), not a single value or a list.");

    /// <summary>Edit distance, for suggesting the key an operator meant.</summary>
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }

        return row[b.Length];
    }
}
