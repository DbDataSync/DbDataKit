namespace DbDataKit.Access;

/// <summary>The JSON Schema of the section (<c>schemas/access.schema.json</c>), for the hosts that embed it in their own schema and for the tests that keep it equal to the loader.</summary>
public static class AccessSchema
{
    /// <summary>The schema's text. Its root is the section itself; <c>$defs.dataset</c>, <c>$defs.group</c>, <c>$defs.rule</c> and <c>$defs.columnRule</c> are its parts.</summary>
    public static string Text()
    {
        var assembly = typeof(AccessSchema).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("access.schema.json", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}
