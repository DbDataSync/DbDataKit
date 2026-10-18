namespace DbDataBuild.Execution;

/// <summary>Process-wide driver settings that must be in place before a driver first reads them.</summary>
public static class DriverSettings
{
    /// <summary>
    /// Npgsql turns the smallest and largest date and timestamp (0001-01-01 and 9999-12-31) into PostgreSQL's `-infinity` and `infinity`. A warehouse writes 9999-12-31 as "no end date" and means that date, so the conversion
    /// is off for every connection this tool opens. A value that is truly infinite is then an error, not a date. Called first thing by the command line and again by <c>LoginSettings</c>, before a connection can be opened.
    /// </summary>
    public static void Apply() => AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);
}
