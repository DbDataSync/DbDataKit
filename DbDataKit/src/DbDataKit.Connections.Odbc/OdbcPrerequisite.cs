using System.Data.Odbc;

namespace DbDataKit.Connections.Odbc;

/// <summary>
/// The one ODBC failure a person can fix on the machine: a missing driver manager, or a missing or unusable driver or data source. Described in a
/// sentence that says which, from the exception's type and the SQLSTATE alone; nothing the driver said is ever repeated, because it can quote a
/// user, a path or a value.
/// </summary>
public static class OdbcPrerequisite
{
    /// <summary>The sentence, or null when the exception (or anything inside it) is something else.</summary>
    public static string? Describe(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is DllNotFoundException or EntryPointNotFoundException)
                return "the ODBC driver manager is not installed on this machine (install unixODBC on Linux or macOS; Windows has one built in)";
            if (e is not OdbcException odbc) continue;
            var states = odbc.Errors.Cast<OdbcError>().Select(x => x.SQLState).ToList();
            if (states.Contains("IM002"))
                return "the ODBC driver or data source named in the login was not found (no such driver in odbcinst.ini and no such DSN; the vendor's driver must be installed)";
            // unixODBC reports a driver it cannot open as a warning (01000), and its text is the only thing that tells it from any other: the text is read, never repeated
            if (states.Contains("IM003") || odbc.Errors.Cast<OdbcError>().Any(x => x.SQLState == "01000" && x.Message.Contains("Can't open lib", StringComparison.Ordinal)))
                return "the ODBC driver named in the login is not installed or could not be loaded (it is not listed in odbcinst.ini, or its file is missing, or a library it needs is)";
            if (states.Contains("IM004") || states.Contains("IM014"))
                return "the ODBC driver manager could not use the driver named in the login (the driver does not match the manager, or the driver is not an ODBC driver)";
            if (states.Contains("IM012") || states.Contains("IM010"))
                return "the ODBC login is not a valid connection string or DSN";
        }
        return null;
    }
}
