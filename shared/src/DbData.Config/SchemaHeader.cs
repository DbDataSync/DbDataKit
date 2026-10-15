using System.Globalization;

namespace DbData.Config;

/// <summary>The <c>schema: &lt;kind&gt;/&lt;int&gt;</c> header form, an alternative to the <c>schemaVersion: &lt;int&gt;</c> key.</summary>
public static class SchemaHeader
{
    public const string Key = "schema";

    public static bool TryParse(string? value, out string kind, out int version)
    {
        kind = "";
        version = 0;
        if (value is null) return false;
        var i = value.LastIndexOf('/');
        if (i <= 0 || i == value.Length - 1) return false;
        if (!int.TryParse(value.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1) return false;
        kind = value[..i];
        return true;
    }

    public static string Format(string kind, int version) => kind + "/" + version.ToString(CultureInfo.InvariantCulture);
}
