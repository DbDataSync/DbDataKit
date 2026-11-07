using System.Text.RegularExpressions;

namespace DbDataKit.State.Snapshots;

/// <summary>One table of a module as its migrations declare it: the columns in order, each with its logical type, and the primary key.</summary>
public sealed record StateTableDefinition(string Module, string Name, IReadOnlyList<SnapshotColumn> Columns, IReadOnlyList<string> PrimaryKey);

/// <summary>
/// Reads what tables a module has, and the logical type of every column, from its migration templates (the convention tokens <c>{{uuid}} {{time}} {{json}} {{bool}} {{blob}} {{decimal}} {{int}}
/// {{key}} {{text}}</c> and the older <c>{{identity:column}}</c>). The templates are the one place that says what a column is on every engine: asking an engine for its column types would give three
/// answers (<c>TEXT</c> for a SQLite uuid, <c>BIT</c> for a SQL Server bool) that have to be mapped back to what the template said, and a SQLite column has no type that tells a time from a key.
/// <para>
/// It follows <c>CREATE TABLE</c>, <c>ALTER TABLE ... {{addcolumn}}</c> (and a plain <c>ADD [COLUMN]</c>), <c>DROP COLUMN</c> and <c>DROP TABLE</c> across the migrations in order, the same constructs
/// <see cref="StateConventions"/> understands. A table in the module's layout that this does not find, a column type that is not a token, or a table without a primary key is an error naming the
/// module and the table: a snapshot that silently left a table out would be worse than none. (The tests also compare every module's result with the columns each engine really has.)
/// </para>
/// </summary>
public static partial class StateTableDefinitions
{
    [GeneratedRegex(@"CREATE\s+TABLE\s+\{table:(?<table>[a-z0-9_]+)\}\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable { get; }

    [GeneratedRegex(@"ALTER\s+TABLE\s+\{table:(?<table>[a-z0-9_]+)\}\s+(?:\{\{addcolumn\}\}|ADD(?:\s+COLUMN)?)\s+(?<name>\w+)\s+(?<type>\{\{\w+\}\})(?<rest>[^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AddColumn { get; }

    [GeneratedRegex(@"ALTER\s+TABLE\s+\{table:(?<table>[a-z0-9_]+)\}\s+DROP\s+COLUMN\s+(?<name>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex DropColumn { get; }

    [GeneratedRegex(@"DROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?\{table:(?<table>[a-z0-9_]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex DropTable { get; }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment { get; }

    [GeneratedRegex(@"^\{\{(?<token>[a-z]+)\}\}$")]
    private static partial Regex TypeToken { get; }

    [GeneratedRegex(@"^\{\{identity:(?<column>\w+)\}\}$")]
    private static partial Regex IdentityItem { get; }

    private static readonly HashSet<string> ConstraintWords = new(StringComparer.OrdinalIgnoreCase) { "PRIMARY", "UNIQUE", "FOREIGN", "CONSTRAINT", "CHECK" };

    private static readonly Dictionary<string, string> LogicalTypes = new(StringComparer.Ordinal)
    {
        ["uuid"] = SnapshotColumnTypes.Uuid,
        ["time"] = SnapshotColumnTypes.Time,
        ["json"] = SnapshotColumnTypes.Json,
        ["bool"] = SnapshotColumnTypes.Bool,
        ["blob"] = SnapshotColumnTypes.Blob,
        ["decimal"] = SnapshotColumnTypes.Decimal,
        ["int"] = SnapshotColumnTypes.Int,
        ["key"] = SnapshotColumnTypes.Text,
        ["text"] = SnapshotColumnTypes.Text,
    };

    private sealed class Table(string name)
    {
        public string Name { get; } = name;
        public List<SnapshotColumn> Columns { get; } = [];
        public List<string> PrimaryKey { get; } = [];
    }

    /// <summary>The tables of <paramref name="module"/> in the order its migrations create them (a table after the tables it refers to).</summary>
    public static IReadOnlyList<StateTableDefinition> Read(StateModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var tables = new List<Table>();

        for (var i = 0; i < module.Migrations.Count; i++)
        {
            var where = $"the module '{module.Name}', migration {i + 1}";
            var text = LineComment.Replace(module.Migrations[i], "");

            foreach (Match create in CreateTable.Matches(text))
            {
                var name = create.Groups["table"].Value;
                if (tables.Any(t => t.Name == name)) throw new InvalidOperationException($"{Capitalise(where)} creates the table '{name}' a second time.");
                tables.Add(ParseCreate(module, where, name, text, create.Index + create.Length));
            }

            foreach (Match alter in AddColumn.Matches(text))
            {
                var table = Find(tables, alter.Groups["table"].Value, module, where);
                var rest = alter.Groups["rest"].Value;
                var type = LogicalType(alter.Groups["type"].Value, module, table.Name, alter.Groups["name"].Value);
                table.Columns.Add(new SnapshotColumn(alter.Groups["name"].Value, type, !rest.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase)));
            }

            foreach (Match drop in DropColumn.Matches(text))
            {
                var table = Find(tables, drop.Groups["table"].Value, module, where);
                if (table.Columns.RemoveAll(c => c.Name == drop.Groups["name"].Value) == 0)
                    throw new InvalidOperationException($"{Capitalise(where)} drops the column '{table.Name}.{drop.Groups["name"].Value}', which the table does not have.");
                table.PrimaryKey.Remove(drop.Groups["name"].Value);
            }

            foreach (Match drop in DropTable.Matches(text))
                tables.Remove(Find(tables, drop.Groups["table"].Value, module, where));
        }

        foreach (var table in tables)
        {
            if (table.PrimaryKey.Count == 0)
                throw new InvalidOperationException($"The table '{module.Name}.{table.Name}' declares no primary key, and a snapshot reads a table in key order; give it one.");
            foreach (var key in table.PrimaryKey)
            {
                if (table.Columns.All(c => c.Name != key))
                    throw new InvalidOperationException($"The primary key of '{module.Name}.{table.Name}' names the column '{key}', which the table does not have.");
            }
        }

        return [.. tables.Select(t => new StateTableDefinition(module.Name, t.Name, t.Columns, t.PrimaryKey))];
    }

    /// <summary>
    /// The tables of the namespace's module checked against the layout's (every table a migration mentions is there, and nothing is missing from what was read), so a construct this reader does not
    /// understand stops the export instead of dropping a table.
    /// </summary>
    internal static IReadOnlyList<StateTableDefinition> ReadFor(StateNamespace ns)
    {
        var tables = Read(ns.Module);
        // a table a migration dropped is among the layout's names and not in the result, which is right
        var dropped = ns.Module.Migrations.SelectMany(m => DropTable.Matches(LineComment.Replace(m, ""))).Select(d => d.Groups["table"].Value).ToHashSet(StringComparer.Ordinal);
        var missing = ns.Tables.Keys.FirstOrDefault(k => tables.All(t => t.Name != k) && !dropped.Contains(k));
        if (missing is not null)
            throw new InvalidOperationException($"The module '{ns.Module.Name}' has the table '{missing}' in its layout, and no migration creates it in a form the snapshot understands (CREATE TABLE {{table:name}} (...)).");
        return tables;
    }

    private static Table Find(List<Table> tables, string name, StateModule module, string where) =>
        tables.FirstOrDefault(t => t.Name == name) ?? throw new InvalidOperationException($"{Capitalise(where)} alters or drops the table '{name}', which no earlier migration of the module creates.");

    private static Table ParseCreate(StateModule module, string where, string name, string text, int start)
    {
        var table = new Table(name);
        foreach (var item in StateConventions.TopLevelItems(text, start))
        {
            if (item.Length == 0) continue;
            if (IdentityItem.Match(item) is { Success: true } identity)
            {
                var column = identity.Groups["column"].Value;
                table.Columns.Add(new SnapshotColumn(column, SnapshotColumnTypes.Int, Nullable: false, Identity: true));
                table.PrimaryKey.Add(column);
                continue;
            }

            var words = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (ConstraintWords.Contains(words[0]))
            {
                if (item.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(item, @"^(?:CONSTRAINT\s+\w+\s+)?PRIMARY\s+KEY", RegexOptions.IgnoreCase))
                {
                    table.PrimaryKey.Clear();
                    table.PrimaryKey.AddRange(StateConventions.ColumnList(item));
                }

                continue;
            }

            if (words.Length < 2) throw new InvalidOperationException($"{Capitalise(where)}: the column definition '{item}' of the table '{name}' has no type.");
            var inline = item.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
            var type = LogicalType(words[1], module, name, words[0]);
            table.Columns.Add(new SnapshotColumn(words[0], type, !inline && !item.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase)));
            if (inline) table.PrimaryKey.Add(words[0]);
        }

        return table;
    }

    private static string LogicalType(string token, StateModule module, string table, string column) =>
        TypeToken.Match(token) is { Success: true } match && LogicalTypes.TryGetValue(match.Groups["token"].Value, out var type)
            ? type
            : throw new InvalidOperationException($"The column '{module.Name}.{table}.{column}' has the type '{token}', which is not one of the convention tokens ({string.Join(", ", LogicalTypes.Keys.Select(k => "{{" + k + "}}"))}); a snapshot cannot say what it holds.");

    private static string Capitalise(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
