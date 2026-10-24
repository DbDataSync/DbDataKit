using DbDataSync.Libraries;

namespace DbDataSync.State;

public static class StateDialectRegistryExtensions
{
    /// <summary>Registers the SQL Server and PostgreSQL state dialects, resolving their ADO.NET providers through the installed libraries (<c>dbdatasync library install microsoft-data-sqlclient</c> / <c>npgsql</c>).</summary>
    public static void RegisterLibraryBackedEngines(this StateDialectRegistry registry, LibraryRegistry libraries) =>
        registry.RegisterLibraryBackedEngines(libraries.GetFactory);
}
