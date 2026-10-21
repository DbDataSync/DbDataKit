namespace DbDataSync.Drivers.DuckDb.Tests;

/// <summary>
/// A test that needs the internet (DuckDB's extension server). It runs only when <c>DBDATASYNC_TEST_NETWORK</c> is <c>true</c> — the
/// integration job in CI sets it — and is skipped, with the reason, everywhere else.
/// </summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public const string Variable = "DBDATASYNC_TEST_NETWORK";

    public NetworkFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(Variable), "true", StringComparison.OrdinalIgnoreCase))
            Skip = $"Needs network access: set {Variable}=true.";
    }
}
