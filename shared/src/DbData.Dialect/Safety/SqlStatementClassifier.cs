namespace DbData.Dialect;

public enum SqlStatementKind { Select, Insert, Update, Delete, Merge, Ddl, Dcl, Exec, Utility, Unknown }

/// <param name="Code">empty-statement, deny-word, deny-prefix, executable-comment, unterminated</param>
public sealed record SqlFinding(string Code, string Message, int Offset);

public sealed record ClassifiedStatement(SqlStatementKind Kind, int Start, int Length, string? Verb);

public sealed record SqlClassification(IReadOnlyList<ClassifiedStatement> Statements, IReadOnlyList<SqlFinding> Findings);

/// <summary>Optional AST-backed second opinion (e.g. a product plugging in a real parser). It can only make the policy stricter.</summary>
public interface ISqlParser
{
    /// <summary>Returns the kinds of all statements, or null when the text could not be parsed.</summary>
    IReadOnlyList<SqlStatementKind>? Parse(string sql, EngineId engine);
}

/// <summary>Lexical classification of SQL text. Defence in depth, not a parser: see the README.</summary>
public static class SqlStatementClassifier
{
    public static SqlClassification Classify(string sql, ISqlDialect dialect)
    {
        var tokens = SqlLexer.Tokenize(sql ?? "", dialect.Syntax);
        var findings = new List<SqlFinding>();
        var statements = new List<ClassifiedStatement>();
        var current = new List<SqlToken>();
        var rules = dialect.ReadOnly;

        void Flush()
        {
            var sig = current.Where(t => !t.IsTrivia).ToList();
            if (sig.Count > 0)
            {
                int start = sig[0].Start;
                statements.Add(Kind(sig, dialect, start, sig[^1].End - start));
            }
            current.Clear();
        }

        foreach (var t in tokens)
        {
            if (!t.Terminated) findings.Add(new("unterminated", $"unterminated {t.Kind} at offset {t.Start}", t.Start));
            if (t.Executable) findings.Add(new("executable-comment", "executable comment (/*! ... */) is run by the server", t.Start));
            if (t.Kind == SqlTokenKind.Separator) { Flush(); continue; }
            current.Add(t);
            if (t.Kind != SqlTokenKind.Word) continue;
            string w = t.Text.ToLowerInvariant();
            if (rules.DenyWords.Contains(w)) findings.Add(new("deny-word", $"`{t.Text}` is not allowed in a read-only statement", t.Start));
            else if (rules.DenyPrefixes.FirstOrDefault(p => w.StartsWith(p, StringComparison.Ordinal)) is { } pre)
                findings.Add(new("deny-prefix", $"`{t.Text}` (prefix {pre}) is not allowed in a read-only statement", t.Start));
        }
        Flush();
        return new SqlClassification(statements, findings);
    }

    private static ClassifiedStatement Kind(List<SqlToken> sig, ISqlDialect dialect, int start, int length)
    {
        int i = 0;
        while (i < sig.Count && sig[i].Kind == SqlTokenKind.Punctuation && sig[i].Text == "(") i++;
        if (i >= sig.Count || sig[i].Kind != SqlTokenKind.Word) return new(SqlStatementKind.Unknown, start, length, null);
        string verb = sig[i].Text.ToLowerInvariant();
        if (verb == "with")
        {
            int depth = 0;
            for (int k = i + 1; k < sig.Count; k++)
            {
                var t = sig[k];
                if (t.Kind == SqlTokenKind.Punctuation) { if (t.Text == "(") depth++; else if (t.Text == ")") depth--; continue; }
                if (depth != 0 || t.Kind != SqlTokenKind.Word) continue;
                string w = t.Text.ToLowerInvariant();
                if (w is "select" or "insert" or "update" or "delete" or "merge") return new(Map(w, dialect), start, length, "with " + w);
            }
            return new(SqlStatementKind.Unknown, start, length, "with");
        }
        return new(Map(verb, dialect), start, length, verb);
    }

    private static SqlStatementKind Map(string verb, ISqlDialect dialect)
    {
        if (dialect.ReadOnly.SelectStarters.Contains(verb)) return SqlStatementKind.Select;
        return verb switch
        {
            "select" or "values" => SqlStatementKind.Select,
            "insert" or "replace" => SqlStatementKind.Insert,
            "update" => SqlStatementKind.Update,
            "delete" => SqlStatementKind.Delete,
            "merge" or "upsert" => SqlStatementKind.Merge,
            "create" or "alter" or "drop" or "truncate" or "rename" or "comment" => SqlStatementKind.Ddl,
            "grant" or "revoke" or "deny" => SqlStatementKind.Dcl,
            "exec" or "execute" or "call" or "do" => SqlStatementKind.Exec,
            "set" or "use" or "show" or "explain" or "vacuum" or "analyze" or "analyse" or "copy" or "begin" or "commit"
                or "rollback" or "declare" or "pragma" or "attach" or "detach" or "install" or "load" or "lock" or "savepoint" or "start" => SqlStatementKind.Utility,
            _ => SqlStatementKind.Unknown,
        };
    }
}

public sealed record ReadOnlyDecision(bool Allowed, string? Reason, IReadOnlyList<SqlFinding> Findings);

/// <summary>
/// Allows exactly one SELECT (or WITH ... SELECT) with no deny-listed words. It can refuse harmless SQL (an unquoted column named
/// like a keyword); quote such names. Real enforcement is a read-only login or session; this is the second line.
/// </summary>
public static class ReadOnlyPolicy
{
    public static ReadOnlyDecision Check(string sql, ISqlDialect dialect, ISqlParser? parser = null)
    {
        var c = SqlStatementClassifier.Classify(sql, dialect);
        string? reason = null;
        if (c.Statements.Count == 0) reason = "the statement is empty";
        else if (c.Statements.Count > 1) reason = "it contains more than one statement";
        else if (c.Statements[0].Kind != SqlStatementKind.Select) reason = $"it is a {c.Statements[0].Kind} statement, not a SELECT";
        else if (c.Findings.Count > 0) reason = c.Findings[0].Message;
        if (reason is null && parser is not null)
        {
            var kinds = parser.Parse(sql, dialect.Id);
            if (kinds is null) reason = "the parser could not parse the statement";
            else if (kinds.Count != 1 || kinds[0] != SqlStatementKind.Select) reason = "the parser did not classify it as a single SELECT";
        }
        return new ReadOnlyDecision(reason is null, reason, c.Findings);
    }
}
