using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>
/// Turns the schema templates into statements for one engine. The engines disagree about four things in DDL (how an auto-assigned key is declared, what
/// unbounded text is called, what bounded text is called, and what an integer is called), so a schema is written once in tokens (<c>{{text}}</c>,
/// <c>{{key}}</c>, <c>{{int}}</c>, <c>{{addcolumn}}</c>, <c>{{identity:Id}}</c>, <c>{{dropindex:Index:Table}}</c>) and rendered here. <c>{{key}}</c> rather than
/// <c>{{text}}</c> marks every column in a key or an index: SQL Server cannot index <c>NVARCHAR(MAX)</c>.
/// </summary>
public static class StateTemplates
{
    public static string Render(string template, StateDialect dialect) => DropIndexToken.Replace(
        template
            .Replace("{{text}}", dialect.Text)
            .Replace("{{key}}", dialect.KeyText)
            .Replace("{{int}}", dialect.Integer)
            .Replace("{{addcolumn}}", dialect.AddColumn)
            .Replace("{{identity:Id}}", dialect.IdentityKey("Id")),
        match => dialect.DropIndex(match.Groups["index"].Value, match.Groups["table"].Value));

    /// <summary><c>{{dropindex:IndexName:TableName}}</c> — the table is part of the token because SQL
    /// Server needs it and the other two do not, so the schema has to state it either way.</summary>
    private static readonly Regex DropIndexToken = new(
        @"\{\{dropindex:(?<index>\w+):(?<table>\w+)\}\}");

    /// <summary>
    /// Splits a script into statements on top-level semicolons.
    /// <para>
    /// Comment-aware, and not optionally: these scripts carry more prose than DDL, and several of
    /// those comments contain a semicolon mid-sentence. Splitting naively would cut a statement in
    /// half at a word boundary inside a comment and produce two fragments that are each a syntax
    /// error.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        var inLineComment = false;
        var inString = false;

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];

            if (inLineComment)
            {
                if (c == '\n') inLineComment = false;
            }
            else if (inString)
            {
                // '' is an escaped quote inside a literal, not the end of one.
                if (c == '\'' && (i + 1 >= script.Length || script[i + 1] != '\'')) inString = false;
                else if (c == '\'') { current.Append(c); i++; }
            }
            else if (c == '-' && i + 1 < script.Length && script[i + 1] == '-') inLineComment = true;
            else if (c == '\'') inString = true;
            else if (c == ';')
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return statements;

        void Flush()
        {
            var statement = current.ToString().Trim();
            current.Clear();
            // A trailing fragment of nothing but comments is not a statement.
            if (statement.Split('\n').Any(line => line.TrimStart().Length > 0 && !line.TrimStart().StartsWith("--")))
                statements.Add(statement);
        }
    }
}
