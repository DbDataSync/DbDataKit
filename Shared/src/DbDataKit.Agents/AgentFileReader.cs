using System.Globalization;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Agents;

/// <summary>What the readers of <c>agent/1</c> and <c>agent-policy/1</c> share: a diagnostic in the file's own code series and the reading of a set of limits.</summary>
internal abstract class AgentFileReader(string fileName, List<ConfigDiagnostic> diags, string badCode) : YamlFieldReader(fileName, diags)
{
    protected void Bad(YamlNode at, string message, string? fix = null) => Add(badCode, at, message, fix);

    protected LimitSet ReadLimits(YamlMapping m)
    {
        int? Whole(string key, int max)
        {
            if (m.Get(key) is not { } n) return null;
            if (n is YamlScalar s && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1 && v <= max) return v;
            Bad(n, $"`{key}` is a whole number from 1 to {max}.");
            return null;
        }
        long? Long(string key)
        {
            if (m.Get(key) is not { } n) return null;
            if (n is YamlScalar s && long.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1) return v;
            Bad(n, $"`{key}` is a whole number of at least 1.");
            return null;
        }
        decimal? Money(string key)
        {
            if (m.Get(key) is not { } n) return null;
            if (n is YamlScalar s && decimal.TryParse(s.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v > 0) return v;
            Bad(n, $"`{key}` is an amount above zero, such as `5.00`.");
            return null;
        }
        return new LimitSet(Whole("turns", 100_000), Whole("tool-calls", 1_000_000), Long("tokens"), Money("cost"));
    }
}
