namespace DbDataKit.Connections;

/// <summary>The environment variable that holds a connection's login: <c>DBDATA_&lt;APP&gt;_&lt;CONN&gt;[_READ|_WRITE]</c>. One rule for every product, so a name means the same
/// thing wherever it is set.</summary>
public static class LoginEnvironment
{
    /// <summary>Upper case, other characters become underscores (<c>my-dw.1</c> is <c>MY_DW_1</c>).</summary>
    public static string Name(string app, string connection, LoginRole role) =>
        $"DBDATA_{Sanitize(app)}_{Sanitize(connection)}" + role switch { LoginRole.Read => "_READ", LoginRole.Write => "_WRITE", _ => "" };

    public static string Sanitize(string text) =>
        new(text.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
}
