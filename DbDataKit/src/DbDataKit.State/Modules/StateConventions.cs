using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>
/// The conventions of plan 29 section 3.3 as a check over a module's migrations, so a module that breaks one fails a test that names the table and column. Every module's tests call
/// <see cref="Violations"/> and expect none.
/// <list type="bullet">
/// <item>Column names are lower case with underscores (table names are, by the <c>{table:}</c> token's own pattern).</item>
/// <item>A column of the time type (<c>{{time}}</c>) is named <c>*_at</c>, and a column named <c>*_at</c> is of the time type.</item>
/// <item>Types are written as tokens (<c>{{uuid}}</c>, <c>{{time}}</c>, <c>{{json}}</c>, <c>{{key}}</c>, ...), never as one engine's type name, so a template means the same on all three engines.</item>
/// <item>A column in a key or an index is not <c>{{text}}</c>, <c>{{json}}</c> or <c>{{blob}}</c>: SQL Server cannot index them. Bounded text is <c>{{key}}</c>.</item>
/// <item><c>row_version</c> is <c>{{int}}</c>.</item>
/// </list>
/// </summary>
public static partial class StateConventions
{
    [GeneratedRegex(@"CREATE\s+TABLE\s+\{table:(?<table>[a-z0-9_]+)\}\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable { get; }

    [GeneratedRegex(@"ALTER\s+TABLE\s+\{table:(?<table>[a-z0-9_]+)\}\s+\{\{addcolumn\}\}\s+(?<name>\w+)\s+(?<type>\{\{\w+(?::\w+)?\}\}|\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex AddColumn { get; }

    [GeneratedRegex(@"CREATE\s+(?:UNIQUE\s+)?INDEX\s+\{index:\w+\}\s+ON\s+\{table:(?<table>[a-z0-9_]+)\}\s*\((?<columns>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateIndex { get; }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment { get; }

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral { get; }

    [GeneratedRegex(@"\{\{[^}]*\}\}|\{(?:table|index):[^}]*\}")]
    private static partial Regex Token { get; }

    [GeneratedRegex(@"\b(NVARCHAR|VARCHAR|NCHAR|CHAR|TEXT|INTEGER|BIGINT|SMALLINT|INT|BLOB|BYTEA|VARBINARY|JSONB|JSON|UUID|UNIQUEIDENTIFIER|TIMESTAMPTZ|TIMESTAMP|DATETIMEOFFSET|DATETIME2|DATETIME|BOOLEAN|BIT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RawType { get; }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ColumnName { get; }

    private static readonly HashSet<string> NotAKey = ["{{text}}", "{{json}}", "{{blob}}"];

    private static readonly HashSet<string> ConstraintWords = new(StringComparer.OrdinalIgnoreCase) { "PRIMARY", "UNIQUE", "FOREIGN", "CONSTRAINT", "CHECK" };

    /// <summary>What in the module breaks a convention, one line each, naming the migration, the table and the column.</summary>
    public static IReadOnlyList<string> Violations(StateModule module)
    {
        var problems = new List<string>();
        for (var i = 0; i < module.Migrations.Count; i++)
        {
            var where = $"{module.Name} migration {i + 1}";
            var text = LineComment.Replace(module.Migrations[i], "");

            foreach (Match create in CreateTable.Matches(text))
            {
                var table = create.Groups["table"].Value;
                var columns = new Dictionary<string, string>(StringComparer.Ordinal);
                var keyColumns = new List<string>();
                foreach (var item in TopLevelItems(text, create.Index + create.Length))
                {
                    var words = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 0) continue;
                    if (ConstraintWords.Contains(words[0]))
                    {
                        keyColumns.AddRange(ColumnList(item));
                        continue;
                    }

                    if (words[0].StartsWith("{{identity:", StringComparison.Ordinal)) continue;
                    var name = words[0];
                    var type = words.Length > 1 ? words[1] : "";
                    columns[name] = type;
                    CheckColumn(problems, where, table, name, type, item);
                    if (item.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) || item.Contains(" UNIQUE", StringComparison.OrdinalIgnoreCase))
                        keyColumns.Add(name);
                }

                foreach (var key in keyColumns.Distinct())
                {
                    if (columns.TryGetValue(key, out var type) && NotAKey.Contains(type))
                        problems.Add($"{where}, {table}.{key}: a column in a key or index is {type}; use {{{{key}}}} (SQL Server cannot index unbounded text).");
                }
            }

            foreach (Match add in AddColumn.Matches(text))
                CheckColumn(problems, where, add.Groups["table"].Value, add.Groups["name"].Value, add.Groups["type"].Value, add.Value);

            foreach (Match index in CreateIndex.Matches(text))
            {
                // the type of an indexed column is known only where this migration declared it, or an earlier one did
                var table = index.Groups["table"].Value;
                foreach (var column in ColumnList("(" + index.Groups["columns"].Value + ")"))
                {
                    var declared = DeclaredType(module, table, column);
                    if (declared is not null && NotAKey.Contains(declared))
                        problems.Add($"{where}, {table}.{column}: an index over a column that is {declared}; use {{{{key}}}}.");
                }
            }
        }

        return problems;
    }

    private static void CheckColumn(List<string> problems, string where, string table, string name, string type, string definition)
    {
        if (!ColumnName.IsMatch(name))
            problems.Add($"{where}, {table}.{name}: column names are lower case with underscores.");
        if (type == "{{time}}" && !name.EndsWith("_at", StringComparison.Ordinal))
            problems.Add($"{where}, {table}.{name}: a time column is named *_at.");
        if (name.EndsWith("_at", StringComparison.Ordinal) && type != "{{time}}")
            problems.Add($"{where}, {table}.{name}: a column named *_at is {{{{time}}}}, not {type}.");
        if (name == "row_version" && type != "{{int}}")
            problems.Add($"{where}, {table}.{name}: row_version is {{{{int}}}}.");

        var stripped = Token.Replace(StringLiteral.Replace(definition, "''"), " ");
        if (RawType.Match(stripped) is { Success: true } raw)
            problems.Add($"{where}, {table}.{name}: '{raw.Value}' is one engine's type; write the token ({{{{uuid}}}}, {{{{time}}}}, {{{{json}}}}, {{{{bool}}}}, {{{{blob}}}}, {{{{int}}}}, {{{{key}}}}, {{{{text}}}}).");
    }

    private static string? DeclaredType(StateModule module, string table, string column)
    {
        foreach (var migration in module.Migrations)
        {
            var text = LineComment.Replace(migration, "");
            foreach (Match create in CreateTable.Matches(text))
            {
                if (create.Groups["table"].Value != table) continue;
                foreach (var item in TopLevelItems(text, create.Index + create.Length))
                {
                    var words = item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 1 && words[0] == column) return words[1];
                }
            }

            foreach (Match add in AddColumn.Matches(text))
            {
                if (add.Groups["table"].Value == table && add.Groups["name"].Value == column) return add.Groups["type"].Value;
            }
        }

        return null;
    }

    /// <summary>The comma-separated items of a parenthesised list that starts at <paramref name="start"/> (just after the opening parenthesis), up to its matching close.</summary>
    private static IEnumerable<string> TopLevelItems(string text, int start)
    {
        var depth = 1;
        var inString = false;
        var item = new System.Text.StringBuilder();
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\'') inString = false;
            }
            else if (c == '\'') inString = true;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0)
            {
                yield return item.ToString().Trim();
                yield break;
            }
            else if (c == ',' && depth == 1)
            {
                yield return item.ToString().Trim();
                item.Clear();
                continue;
            }

            item.Append(c);
        }

        yield return item.ToString().Trim();
    }

    /// <summary>The column names inside the first parenthesised list of <paramref name="item"/>.</summary>
    private static IEnumerable<string> ColumnList(string item)
    {
        var open = item.IndexOf('(', StringComparison.Ordinal);
        if (open < 0) yield break;
        foreach (var part in TopLevelItems(item, open + 1))
        {
            var name = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(name)) yield return name;
        }
    }
}
