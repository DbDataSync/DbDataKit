using DbDataKit.Odbc.Metadata;

namespace DbDataBuild.Execution;

/// <summary>
/// The value of an ODBC connection's login: a connection string (`Driver={PostgreSQL Unicode};Server=...;UID=...;PWD=...`), a DSN with its credentials (`DSN=name;UID=...;PWD=...`) or just the name of a DSN. It is passed
/// to the driver manager as written (a bare name becomes `DSN=name`, <see cref="OdbcConnectionString.Normalize"/>); the tool only reads it to say who the login is, and never prints it. Reading the string is the shared
/// <see cref="OdbcConnectionString"/>'s: Build keeps only the question it asks of it.
/// </summary>
public static class OdbcLogin
{
    private static readonly string[] UserKeys = ["uid", "user", "username", "user id", "userid"];

    /// <summary>The user the login names (`UID`, `User`, `Username`), or null when it names none (a trusted connection, or a DSN that holds the credentials).</summary>
    public static string? UserOf(string value)
    {
        foreach (var (key, v) in OdbcConnectionString.Parse(OdbcConnectionString.Normalize(value)))
            if (UserKeys.Contains(key) && v.Length > 0) return v;
        return null;
    }
}
