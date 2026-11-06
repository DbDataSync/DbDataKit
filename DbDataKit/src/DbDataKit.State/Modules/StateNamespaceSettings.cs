namespace DbDataKit.State;

/// <summary>One namespace's two settings (plan 29 section 3.2); a null field is "not said", so the next level's value or the built-in default applies.</summary>
/// <param name="Schema">The database schema the tables are created in. May contain <c>{module}</c> and <c>{scope}</c>. An empty string means the engine's own default schema (<c>dbo</c>, <c>public</c>). Ignored on SQLite, which has no schemas in a file.</param>
/// <param name="Prefix">Put before each table and index name. May contain <c>{module}</c> and <c>{scope}</c>. An empty string means none.</param>
public sealed record NamespaceSetting(string? Schema = null, string? Prefix = null);

/// <summary>
/// What an administrator chose for the namespaces of one database: a setting for everything (<see cref="Default"/>), a setting per module (<see cref="Modules"/>, which wins),
/// and where the registry table lives. What is not said gets the built-in default: on SQL Server and PostgreSQL the schema <c>dbdatakit_&lt;module&gt;</c> and no prefix, on
/// SQLite the prefix <c>dbdatakit_&lt;module&gt;_</c>; a module kept per group or per service adds the scope's name to either.
/// </summary>
public sealed class StateNamespaceSettings
{
    public static StateNamespaceSettings Defaults { get; } = new();

    /// <summary>Applies to every module that has no setting of its own, for each field it names.</summary>
    public NamespaceSetting Default { get; init; } = new();

    /// <summary>By module name.</summary>
    public IReadOnlyDictionary<string, NamespaceSetting> Modules { get; init; } = new Dictionary<string, NamespaceSetting>();

    /// <summary>
    /// Where <c>dbdatakit_modules</c> is. Its table is always named <c>modules</c>; the schema defaults to <c>dbdatakit</c> and, on SQLite, the prefix to <c>dbdatakit_</c> (so the table
    /// is <c>dbdatakit_modules</c> there). Written with no placeholders: there is one registry per database.
    /// </summary>
    public NamespaceSetting Registry { get; init; } = new();
}
