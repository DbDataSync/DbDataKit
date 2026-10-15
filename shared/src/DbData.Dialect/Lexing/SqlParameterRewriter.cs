namespace DbData.Dialect;

/// <summary>The rewritten SQL plus, for every marker in the output order, the name it came from (null for positional markers).</summary>
/// <param name="BindingOrder">One entry per marker occurrence in the rewritten text, in order. Positional targets bind in this order.</param>
/// <param name="DistinctNames">Distinct parameters in first-seen order (names, or p1..pN for positional sources).</param>
public sealed record ParameterRewrite(string Sql, IReadOnlyList<string> BindingOrder, IReadOnlyList<string> DistinctNames);

/// <summary>Rewrites parameter markers from one style to another using the shared lexer, so markers inside strings, comments and quoted identifiers are left alone.</summary>
public static class SqlParameterRewriter
{
    /// <summary>All parameter tokens of <paramref name="sql"/> under the given syntax flags.</summary>
    public static IReadOnlyList<SqlToken> Find(string sql, SqlSyntax syntax)
        => SqlLexer.Tokenize(sql, syntax).Where(t => t.Kind == SqlTokenKind.Parameter).ToList();

    /// <param name="sourceSyntax">Flags describing the markers present in <paramref name="sql"/> (include QuestionParameter for "?" input).</param>
    public static ParameterRewrite Rewrite(string sql, SqlSyntax sourceSyntax, ISqlDialect target)
    {
        var sb = new System.Text.StringBuilder(sql.Length + 16);
        var order = new List<string>();
        var distinct = new List<string>();
        int positional = 0;
        foreach (var t in SqlLexer.Tokenize(sql, sourceSyntax))
        {
            if (t.Kind != SqlTokenKind.Parameter) { sb.Append(t.Text); continue; }
            string name;
            if (t.Parameter == SqlParameterKind.Question) name = "p" + (++positional).ToString(System.Globalization.CultureInfo.InvariantCulture);
            else name = t.Parameter == SqlParameterKind.Numbered ? "p" + t.ParameterText : t.ParameterText;
            int index = distinct.IndexOf(name);
            if (index < 0) { distinct.Add(name); index = distinct.Count - 1; }
            order.Add(name);
            switch (target.ParameterStyle)
            {
                case ParameterStyle.Positional: sb.Append('?'); break;
                case ParameterStyle.Numbered: sb.Append(target.ParameterName(index)); break;
                default: sb.Append(target.ParameterName(index, name)); break;
            }
        }
        return new ParameterRewrite(sb.ToString(), order, distinct);
    }
}
