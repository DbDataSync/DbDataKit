namespace DbDataKit.Dialect;

public enum SqlTokenKind
{
    Whitespace,
    /// <summary>Unquoted identifier or keyword (also @@system variables).</summary>
    Word,
    Number,
    /// <summary>'..', E'..', N'..', $tag$..$tag$, q'[..]' and friends (whole token including prefix and delimiters).</summary>
    String,
    /// <summary>"..", [..] or `..` including delimiters.</summary>
    QuotedIdentifier,
    LineComment,
    BlockComment,
    Parameter,
    /// <summary>The statement separator ';'.</summary>
    Separator,
    /// <summary>One of ( ) , .</summary>
    Punctuation,
    /// <summary>Any other single character.</summary>
    Operator,
}

public enum SqlParameterKind { None, Question, NamedAt, NamedColon, Numbered, NamedDollar }

/// <summary>A lexical token. Tokens tile the input exactly: concatenating <see cref="Text"/> of all tokens yields the input.</summary>
/// <param name="Terminated">False for a string/comment/quoted identifier that runs to the end of input without its closer.</param>
/// <param name="Executable">A MySQL /*! ... */ comment, which the server executes.</param>
public readonly record struct SqlToken(
    SqlTokenKind Kind, int Start, string Text, bool Terminated = true,
    SqlParameterKind Parameter = SqlParameterKind.None, bool Executable = false)
{
    public int Length => Text.Length;
    public int End => Start + Text.Length;
    public bool IsTrivia => Kind is SqlTokenKind.Whitespace or SqlTokenKind.LineComment or SqlTokenKind.BlockComment;

    /// <summary>For a parameter: the name or number without its sigil ("" for ?).</summary>
    public string ParameterText => Kind != SqlTokenKind.Parameter ? "" : Parameter == SqlParameterKind.Question ? "" : Text[1..];

    public bool IsWord(string word) => Kind == SqlTokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Lexical scanner only; it never parses. Never throws, whatever the input: unterminated constructs become
/// unterminated tokens. Which forms exist is chosen by <see cref="SqlSyntax"/> flags (see each dialect's <c>Syntax</c>).
/// </summary>
public static class SqlLexer
{
    public static List<SqlToken> Tokenize(string sql, SqlSyntax syntax)
    {
        var tokens = new List<SqlToken>();
        if (string.IsNullOrEmpty(sql)) return tokens;
        int n = sql.Length;
        int i = 0;
        while (i < n)
        {
            int start = i;
            char c = sql[i];
            SqlToken tok;

            if (char.IsWhiteSpace(c))
            {
                while (i < n && char.IsWhiteSpace(sql[i])) i++;
                tok = new(SqlTokenKind.Whitespace, start, sql[start..i]);
            }
            else if (c == '-' && i + 1 < n && sql[i + 1] == '-' && (!Has(syntax, SqlSyntax.DashDashNeedsSpace) || i + 2 >= n || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2])))
            {
                i = LineEnd(sql, i);
                tok = new(SqlTokenKind.LineComment, start, sql[start..i]);
            }
            else if (c == '#' && Has(syntax, SqlSyntax.HashComments))
            {
                i = LineEnd(sql, i);
                tok = new(SqlTokenKind.LineComment, start, sql[start..i]);
            }
            else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                bool executable = Has(syntax, SqlSyntax.ExecutableComments) && i + 2 < n && sql[i + 2] == '!';
                i = ScanBlockComment(sql, i, Has(syntax, SqlSyntax.NestedBlockComments), out bool term);
                tok = new(SqlTokenKind.BlockComment, start, sql[start..i], term, Executable: executable);
            }
            else if (c == '\'')
            {
                i = ScanQuoted(sql, i, '\'', Has(syntax, SqlSyntax.BackslashEscapes), out bool term);
                tok = new(SqlTokenKind.String, start, sql[start..i], term);
            }
            else if (c == '"' && Has(syntax, SqlSyntax.DoubleQuoteString))
            {
                i = ScanQuoted(sql, i, '"', Has(syntax, SqlSyntax.BackslashEscapes), out bool term);
                tok = new(SqlTokenKind.String, start, sql[start..i], term);
            }
            else if (c == '"' && Has(syntax, SqlSyntax.DoubleQuoteIdentifier))
            {
                i = ScanQuoted(sql, i, '"', false, out bool term);
                tok = new(SqlTokenKind.QuotedIdentifier, start, sql[start..i], term);
            }
            else if (c == '`' && Has(syntax, SqlSyntax.BacktickIdentifier))
            {
                i = ScanQuoted(sql, i, '`', false, out bool term);
                tok = new(SqlTokenKind.QuotedIdentifier, start, sql[start..i], term);
            }
            else if (c == '[' && Has(syntax, SqlSyntax.BracketIdentifier))
            {
                i = ScanQuoted(sql, i, ']', false, out bool term, openerLength: 1);
                tok = new(SqlTokenKind.QuotedIdentifier, start, sql[start..i], term);
            }
            else if (c == '$' && TryDollar(sql, ref i, syntax, out tok)) { }
            else if (c == '?' && Has(syntax, SqlSyntax.QuestionParameter))
            {
                i++;
                tok = new(SqlTokenKind.Parameter, start, "?", Parameter: SqlParameterKind.Question);
            }
            else if (c == '@' && Has(syntax, SqlSyntax.AtParameter) && i + 1 < n && IsWordStart(sql[i + 1], syntax))
            {
                i++;
                while (i < n && IsWordPart(sql[i], syntax)) i++;
                tok = new(SqlTokenKind.Parameter, start, sql[start..i], Parameter: SqlParameterKind.NamedAt);
            }
            else if (c == '@' && Has(syntax, SqlSyntax.AtParameter) && i + 2 < n && sql[i + 1] == '@' && IsWordStart(sql[i + 2], syntax))
            {
                i += 2;
                while (i < n && IsWordPart(sql[i], syntax)) i++;
                tok = new(SqlTokenKind.Word, start, sql[start..i]);
            }
            else if (c == ':' && i + 1 < n && sql[i + 1] == ':')
            {
                i += 2;
                tok = new(SqlTokenKind.Operator, start, "::");
            }
            else if (c == ':' && Has(syntax, SqlSyntax.ColonParameter) && i + 1 < n && IsWordStart(sql[i + 1], syntax) && sql[i + 1] != '$')
            {
                i++;
                while (i < n && IsWordPart(sql[i], syntax)) i++;
                tok = new(SqlTokenKind.Parameter, start, sql[start..i], Parameter: SqlParameterKind.NamedColon);
            }
            else if (c == ';')
            {
                i++;
                tok = new(SqlTokenKind.Separator, start, ";");
            }
            else if (c is '(' or ')' or ',' or '.')
            {
                // ".5" is a number
                if (c == '.' && i + 1 < n && char.IsAsciiDigit(sql[i + 1]))
                {
                    i = ScanNumber(sql, i);
                    tok = new(SqlTokenKind.Number, start, sql[start..i]);
                }
                else
                {
                    i++;
                    tok = new(SqlTokenKind.Punctuation, start, c.ToString());
                }
            }
            else if (char.IsAsciiDigit(c))
            {
                i = ScanNumber(sql, i);
                tok = new(SqlTokenKind.Number, start, sql[start..i]);
            }
            else if (IsWordStart(c, syntax))
            {
                if (TryPrefixedString(sql, ref i, syntax, out tok)) { }
                else
                {
                    i++;
                    while (i < n && IsWordPart(sql[i], syntax)) i++;
                    tok = new(SqlTokenKind.Word, start, sql[start..i]);
                }
            }
            else
            {
                i++;
                // keep surrogate pairs together
                if (char.IsHighSurrogate(c) && i < n && char.IsLowSurrogate(sql[i])) i++;
                tok = new(SqlTokenKind.Operator, start, sql[start..i]);
            }
            tokens.Add(tok);
        }
        return tokens;
    }

    /// <summary>Tokens re-joined; always equals the input.</summary>
    public static string Join(IEnumerable<SqlToken> tokens) => string.Concat(tokens.Select(t => t.Text));

    private static bool Has(SqlSyntax s, SqlSyntax f) => (s & f) == f;

    private static bool IsWordStart(char c, SqlSyntax s)
        => char.IsLetter(c) || c == '_' || (c > 127 && !char.IsWhiteSpace(c) && !char.IsSurrogate(c) && !char.IsControl(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
           || (c == '#' && Has(s, SqlSyntax.HashIdentifiers) && !Has(s, SqlSyntax.HashComments));

    private static bool IsWordPart(char c, SqlSyntax s)
        => char.IsLetterOrDigit(c) || c == '_' || c == '$' || (c > 127 && IsWordStart(c, s)) || (c == '#' && Has(s, SqlSyntax.HashIdentifiers) && !Has(s, SqlSyntax.HashComments));

    private static int LineEnd(string sql, int i)
    {
        while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++;
        return i;
    }

    private static int ScanBlockComment(string sql, int i, bool nested, out bool terminated)
    {
        int depth = 1;
        i += 2;
        while (i < sql.Length)
        {
            if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0) { terminated = true; return i; }
            }
            else if (nested && sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; }
            else i++;
        }
        terminated = false;
        return sql.Length;
    }

    /// <summary>Scans from the opening delimiter at <paramref name="i"/>; <paramref name="close"/> is doubled to escape it.</summary>
    private static int ScanQuoted(string sql, int i, char close, bool backslash, out bool terminated, int openerLength = 1)
    {
        i += openerLength;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (backslash && c == '\\') { i = Math.Min(i + 2, sql.Length); continue; }
            if (c == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                terminated = true;
                return i + 1;
            }
            i++;
        }
        terminated = false;
        return sql.Length;
    }

    private static int ScanNumber(string sql, int i)
    {
        int n = sql.Length;
        if (sql[i] == '0' && i + 1 < n && (sql[i + 1] is 'x' or 'X'))
        {
            i += 2;
            while (i < n && char.IsAsciiHexDigit(sql[i])) i++;
            return i;
        }
        while (i < n && char.IsAsciiDigit(sql[i])) i++;
        if (i < n && sql[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(sql[i])) i++;
        }
        if (i < n && (sql[i] is 'e' or 'E'))
        {
            int j = i + 1;
            if (j < n && (sql[j] is '+' or '-')) j++;
            if (j < n && char.IsAsciiDigit(sql[j]))
            {
                while (j < n && char.IsAsciiDigit(sql[j])) j++;
                i = j;
            }
        }
        return i;
    }

    private static bool TryPrefixedString(string sql, ref int i, SqlSyntax syntax, out SqlToken tok)
    {
        int start = i;
        int n = sql.Length;
        char c = sql[i];
        tok = default;
        if (i + 1 < n && sql[i + 1] == '\'')
        {
            bool backslash = false, ok = false;
            switch (char.ToLowerInvariant(c))
            {
                case 'e' when Has(syntax, SqlSyntax.EStrings): ok = true; backslash = true; break;
                case 'n' when Has(syntax, SqlSyntax.NStrings): ok = true; backslash = Has(syntax, SqlSyntax.BackslashEscapes); break;
                case 'b' or 'x' when Has(syntax, SqlSyntax.PrefixedStrings): ok = true; backslash = Has(syntax, SqlSyntax.BackslashEscapes); break;
                case 'q' when Has(syntax, SqlSyntax.OracleQQuote):
                    return TryOracleQ(sql, ref i, start, 1, out tok);
            }
            if (!ok) return false;
            int end = ScanQuoted(sql, i + 1, '\'', backslash, out bool term);
            tok = new(SqlTokenKind.String, start, sql[start..end], term);
            i = end;
            return true;
        }
        // U&'...'  and  nq'[...]'
        if (Has(syntax, SqlSyntax.PrefixedStrings) && char.ToLowerInvariant(c) == 'u' && i + 2 < n && sql[i + 1] == '&' && sql[i + 2] == '\'')
        {
            int end = ScanQuoted(sql, i + 2, '\'', false, out bool term);
            tok = new(SqlTokenKind.String, start, sql[start..end], term);
            i = end;
            return true;
        }
        if (Has(syntax, SqlSyntax.OracleQQuote) && char.ToLowerInvariant(c) == 'n' && i + 2 < n && char.ToLowerInvariant(sql[i + 1]) == 'q' && sql[i + 2] == '\'')
            return TryOracleQ(sql, ref i, start, 2, out tok);
        return false;
    }

    // q'<d> ... <d>'  where <d> is a delimiter; ([{< pair with )]}>.
    private static bool TryOracleQ(string sql, ref int i, int start, int prefixLen, out SqlToken tok)
    {
        tok = default;
        int n = sql.Length;
        int quotePos = i + prefixLen;           // the ' after q
        if (quotePos + 1 >= n) return false;
        char d = sql[quotePos + 1];
        if (char.IsWhiteSpace(d) || d == '\'') return false;
        char closer = d switch { '(' => ')', '[' => ']', '{' => '}', '<' => '>', _ => d };
        int j = quotePos + 2;
        while (j < n)
        {
            if (sql[j] == closer && j + 1 < n && sql[j + 1] == '\'')
            {
                j += 2;
                tok = new(SqlTokenKind.String, start, sql[start..j]);
                i = j;
                return true;
            }
            j++;
        }
        tok = new(SqlTokenKind.String, start, sql[start..], Terminated: false);
        i = n;
        return true;
    }

    private static bool TryDollar(string sql, ref int i, SqlSyntax syntax, out SqlToken tok)
    {
        int start = i, n = sql.Length;
        tok = default;
        bool afterWordChar = start > 0 && (char.IsLetterOrDigit(sql[start - 1]) || sql[start - 1] == '_' || sql[start - 1] == '$');
        if (Has(syntax, SqlSyntax.DollarQuoting) && !afterWordChar)
        {
            int j = i + 1;
            if (j < n && (char.IsLetter(sql[j]) || sql[j] == '_' || sql[j] > 127))
                while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_' || sql[j] > 127)) j++;
            if (j < n && sql[j] == '$')
            {
                string tag = sql[i..(j + 1)];                       // $tag$
                int close = sql.IndexOf(tag, j + 1, StringComparison.Ordinal);
                if (close < 0) { tok = new(SqlTokenKind.String, start, sql[start..], Terminated: false); i = n; }
                else { int end = close + tag.Length; tok = new(SqlTokenKind.String, start, sql[start..end]); i = end; }
                return true;
            }
        }
        if (!afterWordChar && i + 1 < n)
        {
            if (Has(syntax, SqlSyntax.DollarNumberedParameter) && char.IsAsciiDigit(sql[i + 1]))
            {
                int j = i + 1;
                while (j < n && char.IsAsciiDigit(sql[j])) j++;
                tok = new(SqlTokenKind.Parameter, start, sql[start..j], Parameter: SqlParameterKind.Numbered);
                i = j;
                return true;
            }
            if (Has(syntax, SqlSyntax.DollarNamedParameter) && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                tok = new(SqlTokenKind.Parameter, start, sql[start..j], Parameter: SqlParameterKind.NamedDollar);
                i = j;
                return true;
            }
        }
        return false;
    }
}
