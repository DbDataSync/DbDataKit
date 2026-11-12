using System.Globalization;

namespace DbDataKit.Runner.Kit.Formats;

/// <summary>How the writers of the text formats spell a value: invariant culture, ISO 8601 dates, <c>true</c> and <c>false</c>, bytes as Base64. The same rule everywhere, so a file written by one format reads back the same through another.</summary>
public static class ValueText
{
    public static string? Format(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid g => g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
