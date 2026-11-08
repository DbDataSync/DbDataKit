namespace DbDataKit.Config.Yaml;

/// <summary>
/// The place of the <c>checks:</c> section in format 2 of the semantic package, of DbDataStore's <c>model</c> and of the serving data source (plan 24a D5, UC03). Plan 23's check model (<c>check/1</c>, 23a FQ01) gives
/// the entries their keys; until it lands the section is a list of mappings whose keys are not read, so a definition that already carries checks is published in format 2 and an instance that reads format 2 keeps the
/// section as it is. Adding keys to the entries later is additive and needs no new format. The section is refused in a format-1 file, like <c>cache</c>.
/// </summary>
public static class ChecksPlace
{
    public const string Key = "checks";

    /// <summary>Reads the place: a list of mappings. Returns the number of entries, or null with diagnostics (file, line, column) when it is anything else.</summary>
    public static int? Read(YamlNode node, string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog = null)
    {
        var made = catalog ?? DefaultDiagnosticCatalog.Instance;
        if (node is not YamlSequence list)
        {
            diagnostics.Add(made.Create(SharedCodes.InvalidValue, file, node.Line, node.Column, "`checks` is a list of checks, one mapping each.", "Write `checks:` with a `- ` entry for each check.", Key));
            return null;
        }

        var ok = true;
        foreach (var item in list.Items.Where(i => i is not YamlMapping))
        {
            diagnostics.Add(made.Create(SharedCodes.InvalidValue, file, item.Line, item.Column, "Each entry of `checks` is a mapping.", null, Key));
            ok = false;
        }

        return ok ? list.Items.Count : null;
    }
}
