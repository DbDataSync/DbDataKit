using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>One copy of one module: the module, its scope, and where its tables really are.</summary>
/// <param name="ScopeName">The scope's normalised name, or null for a module kept per install.</param>
/// <param name="Schema">The schema, or null where there is none (SQLite, or an empty setting).</param>
/// <param name="Prefix">Put before each table and index name.</param>
/// <param name="Tables">Each table's name within the module to its qualified, rendered name.</param>
public sealed record StateNamespace(StateModule Module, string? ScopeName, string? Schema, string Prefix, IReadOnlyDictionary<string, string> Tables)
{
    /// <summary>The module's name and, where there is one, its scope, the way a message or the registry says it.</summary>
    public string Key => ScopeName is null ? Module.Name : $"{Module.Name}/{ScopeName}";
}

/// <summary>
/// Everything about where a database's modules are, worked out and checked before the database is touched (plan 29 section 3.2): which modules (a service names what it needs and
/// their dependencies come with them, in dependency order), each one's namespace for the scope of the process, the real name of every table and index, and the registry's place.
/// Creating one fails, with a message naming the setting or module, for a name that is not valid on the engine, one that is too long (PostgreSQL 63 characters, SQL Server 128), two
/// namespaces that would render the same table, or a template that refers to a table nobody declares.
/// </summary>
public sealed partial class StateLayout
{
    private const int PostgresNameLimit = 63;
    private const int MsSqlNameLimit = 128;

    [GeneratedRegex(@"\{table:(?:(?<module>[a-z][a-z0-9_]*)\.)?(?<table>[a-z][a-z0-9_]*)\}")]
    private static partial Regex TableToken { get; }

    [GeneratedRegex(@"\{index:(?<index>[a-z][a-z0-9_]*)\}")]
    private static partial Regex IndexToken { get; }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier { get; }

    private readonly Dictionary<string, StateNamespace> _byModule = new(StringComparer.Ordinal);

    private StateLayout(string engine, string? registrySchema, string registryTable)
    {
        Engine = engine;
        RegistrySchema = registrySchema;
        RegistryTable = registryTable;
    }

    public string Engine { get; }

    /// <summary>The namespaces in the order they are applied: a module after every module it depends on.</summary>
    public IReadOnlyList<StateNamespace> Namespaces { get; private set; } = [];

    /// <summary>The registry's schema, or null where there is none.</summary>
    public string? RegistrySchema { get; }

    /// <summary>The registry's rendered, qualified table name.</summary>
    public string Registry => Qualify(RegistrySchema, RegistryTable);

    private string RegistryTable { get; }

    /// <summary>Every schema that has to exist before the first table is created, the registry's included; empty on SQLite.</summary>
    public IReadOnlyList<string> Schemas => [.. new[] { RegistrySchema }.Concat(Namespaces.Select(n => n.Schema)).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase)];

    public StateNamespace Namespace(string module) =>
        _byModule.TryGetValue(module, out var ns) ? ns : throw new InvalidOperationException($"The module '{module}' is not part of this database's layout (it has: {string.Join(", ", _byModule.Keys)}).");

    public bool Has(string module) => _byModule.ContainsKey(module);

    /// <summary>The rendered name of a table: <c>users</c> of the module <c>identity</c>.</summary>
    public string Table(string module, string table)
    {
        var ns = Namespace(module);
        return ns.Tables.TryGetValue(table, out var name) ? name : throw new InvalidOperationException($"The module '{module}' has no table '{table}' (it has: {string.Join(", ", ns.Tables.Keys)}).");
    }

    /// <summary>
    /// Replaces <c>{table:module.name}</c> and, where <paramref name="module"/> is given, <c>{table:name}</c> and <c>{index:name}</c> of that module. Used for statements written at run
    /// time as well as for migrations. A token that names nothing is an error, not text left in a statement.
    /// </summary>
    public string Render(string text, string? module = null)
    {
        var rendered = TableToken.Replace(text, match =>
        {
            var owner = match.Groups["module"].Success ? match.Groups["module"].Value : module
                ?? throw new InvalidOperationException($"'{match.Value}' does not say which module's table it means; write {{table:module.{match.Groups["table"].Value}}}.");
            return Table(owner, match.Groups["table"].Value);
        });
        return IndexToken.Replace(rendered, match =>
        {
            var owner = module ?? throw new InvalidOperationException($"'{match.Value}' is only meaningful inside a module's own statements.");
            return Namespace(owner).Prefix + match.Groups["index"].Value;
        });
    }

    /// <summary>
    /// Works out the layout for <paramref name="modules"/> (and what they depend on) on <paramref name="engine"/>, for the process described by <paramref name="scope"/>, and checks it.
    /// </summary>
    public static StateLayout Create(string engine, IEnumerable<StateModule> modules, StateScope? scope = null, StateNamespaceSettings? settings = null)
    {
        scope ??= StateScope.Install;
        settings ??= StateNamespaceSettings.Defaults;
        var sqlite = engine == StateEngineIds.Sqlite;
        var limit = engine switch { StateEngineIds.Postgres => PostgresNameLimit, StateEngineIds.MsSql => MsSqlNameLimit, _ => int.MaxValue };

        var ordered = Order(modules);
        foreach (var name in settings.Modules.Keys)
        {
            if (!ordered.Any(m => m.Name == name))
                throw new InvalidOperationException($"The namespace settings name the module '{name}', which this database does not use (it uses: {string.Join(", ", ordered.Select(m => m.Name))}).");
        }

        var registrySchema = RenderSetting(settings.Registry.Schema, sqlite ? null : "dbdatakit", "modules", null);
        var registryPrefix = RenderSetting(settings.Registry.Prefix, sqlite ? "dbdatakit_" : "", "modules", null) ?? "";
        var layout = new StateLayout(engine, sqlite ? null : NullIfEmpty(registrySchema), registryPrefix + "modules");
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Claim(layout.Registry, "the registry (dbdatakit_modules)", "the registry's setting");

        var namespaces = new List<StateNamespace>();
        foreach (var module in ordered)
        {
            var scopeName = Normalise(scope.NameFor(module.Scope, module.Name));
            var moduleSetting = settings.Modules.GetValueOrDefault(module.Name) ?? new NamespaceSetting();
            var schemaPattern = moduleSetting.Schema ?? settings.Default.Schema;
            var prefixPattern = moduleSetting.Prefix ?? settings.Default.Prefix;

            var schema = NullIfEmpty(RenderSetting(schemaPattern, $"dbdatakit_{module.Name}{(scopeName is null ? "" : "_" + scopeName)}", module.Name, scopeName));
            var prefix = RenderSetting(prefixPattern, sqlite ? $"dbdatakit_{module.Name}_{(scopeName is null ? "" : scopeName + "_")}" : "", module.Name, scopeName) ?? "";
            if (sqlite) schema = null;

            if (schema is not null) CheckIdentifier(schema, module, "schema", limit, SettingName(module, "schema"));
            if (prefix.Length > 0 && !Identifier.IsMatch(prefix + "x")) throw Invalid(module, "prefix", prefix);

            var tables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var table in DeclaredTables(module))
            {
                if (ReservedWords.IsReserved(prefix + table))
                    throw new InvalidOperationException($"The module '{module.Name}' names a table '{table}', which is a reserved word on at least one engine (SQL Server refuses a table called checkpoint). Rename the table, or give the namespace a prefix ({SettingName(module, "prefix")}).");
                var bare = prefix + table;
                CheckIdentifier(bare, module, "prefix", limit, SettingName(module, "prefix"), $"the table '{table}'");
                tables[table] = Qualify(schema, bare);
                Claim(tables[table], $"the table '{table}' of the module '{module.Name}'", SettingName(module, "prefix"));
            }

            foreach (var index in DeclaredIndexes(module))
            {
                if (ReservedWords.IsReserved(prefix + index))
                    throw new InvalidOperationException($"The module '{module.Name}' names an index '{index}', which is a reserved word on at least one engine. Rename it.");
                var bare = prefix + index;
                CheckIdentifier(bare, module, "prefix", limit, SettingName(module, "prefix"), $"the index '{index}'");
                Claim(Qualify(schema, bare), $"the index '{index}' of the module '{module.Name}'", SettingName(module, "prefix"));
            }

            foreach (var rule in module.Retention)
            {
                if (!tables.ContainsKey(rule.Table))
                    throw new InvalidOperationException($"The module '{module.Name}' has a retention rule for the table '{rule.Table}', which none of its migrations create.");
                foreach (var child in rule.Children ?? [])
                {
                    if (!tables.ContainsKey(child.Table))
                        throw new InvalidOperationException($"The retention rule for '{module.Name}.{rule.Table}' names the child table '{child.Table}', which none of the module's migrations create.");
                }
            }

            foreach (var owner in module.PayloadOwners)
            {
                if (!tables.ContainsKey(owner.Table))
                    throw new InvalidOperationException($"The module '{module.Name}' says its table '{owner.Table}' owns payloads, and none of its migrations create it.");
            }

            var ns = new StateNamespace(module, scopeName, schema, prefix, tables);
            namespaces.Add(ns);
            layout._byModule[module.Name] = ns;
        }

        layout.Namespaces = namespaces;
        layout.ValidateReferences();
        return layout;

        void Claim(string qualified, string what, string setting)
        {
            if (claimed.TryGetValue(qualified, out var other))
                throw new InvalidOperationException($"{what} and {other} would both be named '{qualified}'. Change {setting} (or the other namespace's) so that the names differ.");
            claimed[qualified] = what;
        }
    }

    /// <summary>Every module once, each after the modules it depends on; the order modules were given breaks ties, so the result is stable.</summary>
    private static List<StateModule> Order(IEnumerable<StateModule> modules)
    {
        var result = new List<StateModule>();
        var seen = new Dictionary<string, StateModule>(StringComparer.Ordinal);
        foreach (var module in modules) Visit(module);
        return result;

        void Visit(StateModule module)
        {
            if (seen.TryGetValue(module.Name, out var existing))
            {
                if (!ReferenceEquals(existing, module))
                    throw new InvalidOperationException($"Two different modules are both named '{module.Name}'.");
                return;
            }
            seen[module.Name] = module;
            foreach (var dependency in module.DependsOn) Visit(dependency);
            result.Add(module);
        }
    }

    private static IEnumerable<string> DeclaredTables(StateModule module) => module.Migrations
        .SelectMany(m => TableToken.Matches(m))
        .Where(m => !m.Groups["module"].Success || m.Groups["module"].Value == module.Name)
        .Select(m => m.Groups["table"].Value)
        .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> DeclaredIndexes(StateModule module) => module.Migrations
        .SelectMany(m => IndexToken.Matches(m))
        .Select(m => m.Groups["index"].Value)
        .Distinct(StringComparer.Ordinal);

    /// <summary>Every <c>{table:other.name}</c> in a module's migrations has to name a dependency that declares that table.</summary>
    private void ValidateReferences()
    {
        foreach (var ns in Namespaces)
        {
            foreach (var match in ns.Module.Migrations.SelectMany(m => TableToken.Matches(m)))
            {
                if (!match.Groups["module"].Success || match.Groups["module"].Value == ns.Module.Name) continue;
                var other = match.Groups["module"].Value;
                if (!ns.Module.DependsOn.Any(d => d.Name == other))
                    throw new InvalidOperationException($"The module '{ns.Module.Name}' uses {match.Value} but does not declare a dependency on '{other}'.");
                // Table() names the module and its tables when the dependency does not declare this one.
                _ = Table(other, match.Groups["table"].Value);
            }
        }
    }

    private static string? RenderSetting(string? pattern, string? builtIn, string module, string? scopeName)
    {
        var value = pattern ?? builtIn;
        return value?.Replace("{module}", module, StringComparison.Ordinal).Replace("{scope}", scopeName ?? "install", StringComparison.Ordinal);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Qualify(string? schema, string name) => schema is null ? name : $"{schema}.{name}";

    /// <summary>A scope name becomes part of a table or schema name, so it is lower case with every other character an underscore.</summary>
    private static string? Normalise(string? scopeName) => scopeName is null ? null : Regex.Replace(scopeName.ToLowerInvariant(), "[^a-z0-9]", "_");

    private static string SettingName(StateModule module, string field) => $"the {field} of the namespace '{module.Name}' (state.namespaces.{module.Name}.{field})";

    private static void CheckIdentifier(string name, StateModule module, string field, int limit, string setting, string? what = null)
    {
        if (!Identifier.IsMatch(name))
            throw new InvalidOperationException($"{Capitalise(setting)} gives {what ?? "a name"} the name '{name}', which is not a valid identifier (letters, digits and underscores, not starting with a digit).");
        if (name.Length > limit)
            throw new InvalidOperationException($"{Capitalise(setting)} gives {what ?? "a name"} the name '{name}', {name.Length} characters; the engine allows {limit}. Shorten the {field}.");
    }

    private static InvalidOperationException Invalid(StateModule module, string field, string value) =>
        new($"{Capitalise(SettingName(module, field))} is '{value}', which is not a valid identifier (letters, digits and underscores, not starting with a digit).");

    private static string Capitalise(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
