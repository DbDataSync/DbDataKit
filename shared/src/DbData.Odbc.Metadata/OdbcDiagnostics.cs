using System.Text;

namespace DbData.Odbc.Metadata;

/// <summary>A SQLSTATE in plain words: what it usually means and what to try.</summary>
public sealed record OdbcDiagnosis(string SqlState, string Summary, string? Advice)
{
    public override string ToString() => Advice is null ? Summary : Summary + " " + Advice;
}

/// <summary>
/// Explains the SQLSTATEs a connect most often fails with, above all IM002 and IM014, which are the same symptom ("data source
/// name not found / architecture mismatch") whether the driver is missing or installed for the other word size. With an
/// <see cref="OdbcInventory"/> and the connection string it can say which of those it is. Only driver and DSN names are quoted, never other values.
/// </summary>
public static class OdbcDiagnostics
{
    /// <summary>The word size of this process.</summary>
    public static OdbcBitness ProcessBitness => Environment.Is64BitProcess ? OdbcBitness.X64 : OdbcBitness.X86;

    /// <summary>Explains the exception's SQLSTATE without any machine context.</summary>
    public static OdbcDiagnosis Explain(OdbcCatalogException exception) => Explain(exception, null, null, null);

    /// <summary>Explains the exception's SQLSTATE; with an inventory and the connection string it names the driver or DSN and its bitness.</summary>
    public static OdbcDiagnosis Explain(OdbcCatalogException exception, string? connectionString, OdbcInventory? inventory, OdbcBitness? processBitness)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Explain(exception.SqlState, connectionString, inventory, processBitness);
    }

    /// <summary>Explains a SQLSTATE without any machine context.</summary>
    public static OdbcDiagnosis Explain(string sqlState) => Explain(sqlState, null, null, null);

    /// <summary>Explains a SQLSTATE; with an inventory and the connection string it names the driver or DSN and its bitness. <paramref name="processBitness"/> defaults to this process.</summary>
    public static OdbcDiagnosis Explain(string sqlState, string? connectionString, OdbcInventory? inventory, OdbcBitness? processBitness)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        var process = processBitness ?? ProcessBitness;
        string state = sqlState.Trim().ToUpperInvariant();

        switch (state)
        {
            case "IM002":
            case "IM014":
            case "IM003":
                return Architecture(state, connectionString, inventory, process);
            case "IM004": return new(state, "The driver could not allocate its environment.", "Reinstall the driver or check it matches the driver manager.");
            case "IM010": return new(state, "The data source name is too long.", "A DSN is limited to 32 characters; use a connection string with Driver= and Server= instead.");
            case "08001": return new(state, "The client could not establish a connection to the server.", "Check the server name or address, the port, a firewall, and that the server is running.");
            case "28000": return new(state, "The server rejected the login.", "Check the user name and password, or the integrated-security setting.");
            case "HYT00":
            case "HYT01": return new(state, "A timeout expired (login or statement).", "Raise OdbcCatalogOptions.LoginTimeout or StatementTimeout, or check the server is reachable and not blocked.");
            case "HY008": return new(state, "The operation was cancelled.", null);
            case "01004": return new(state, "A value was too long for its buffer and would have been cut off.", null);
            case "IM001":
            case "HYC00": return new(state, "The driver does not implement this function.", "Use another source for this metadata, for example INFORMATION_SCHEMA.");
            default: return new(state, $"The ODBC driver reported SQLSTATE {state}.", null);
        }
    }

    private static OdbcDiagnosis Architecture(string state, string? connectionString, OdbcInventory? inventory, OdbcBitness process)
    {
        string bits = process == OdbcBitness.X86 ? "32-bit" : "64-bit";
        string other = process == OdbcBitness.X86 ? "64-bit" : "32-bit";
        string headline = state switch
        {
            "IM014" => "The data source's driver and this application are different word sizes.",
            "IM003" => "The driver could not be loaded.",
            _ => "The data source name or driver was not found (or is installed for the other word size).",
        };
        string general = $"This process is {bits}; it can only use {bits} drivers and DSNs. Install the {bits} driver, create the DSN in the {bits} ODBC administrator, or run the work in a {other} process.";
        if (inventory is null || connectionString is null) return new(state, headline, general);

        var pairs = OdbcConnectionString.Parse(OdbcConnectionString.Normalize(connectionString));
        string? driver = pairs.FirstOrDefault(p => p.Key == "driver").Value;
        string? dsn = pairs.FirstOrDefault(p => p.Key == "dsn").Value;

        if (!string.IsNullOrEmpty(driver) && !driver.Contains('/') && !driver.Contains('\\'))
        {
            var found = inventory.DriverBitnesses(driver);
            if (found.Count == 0)
                return new(state, headline, $"Driver '{driver}' is not installed under that name. Installed: {Names(inventory.Drivers.Select(d => d.Name))}.");
            if (!found.Contains(process) && !found.Contains(OdbcBitness.Unknown))
                return new(state, headline, $"Driver '{driver}' is installed for {Describe(found)} only, but this process is {bits}. Install the {bits} driver or run the work in a {Describe(found)} process (for DbDataProxy, the 32-bit worker).");
            return new(state, headline, $"Driver '{driver}' is installed for this process's word size; check its library and its dependencies.");
        }
        if (!string.IsNullOrEmpty(dsn))
        {
            var found = inventory.DsnBitnesses(dsn);
            if (found.Count == 0)
                return new(state, headline, $"DSN '{dsn}' is not defined. Defined: {Names(inventory.Dsns.Select(d => d.Name))}.");
            if (!found.Contains(process) && !found.Contains(OdbcBitness.Unknown))
                return new(state, headline, $"DSN '{dsn}' is defined for {Describe(found)} only, but this process is {bits}. Create it in the {bits} ODBC administrator or run the work in a {Describe(found)} process.");
        }
        return new(state, headline, general);
    }

    private static string Describe(IReadOnlyList<OdbcBitness> bitnesses) =>
        string.Join(" and ", bitnesses.Where(b => b != OdbcBitness.Unknown).Select(b => b == OdbcBitness.X86 ? "32-bit" : "64-bit"));

    private static string Names(IEnumerable<string> names)
    {
        var list = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return "none";
        var sb = new StringBuilder(string.Join(", ", list.Take(12)));
        if (list.Count > 12) sb.Append(", ...");
        return sb.ToString();
    }
}
