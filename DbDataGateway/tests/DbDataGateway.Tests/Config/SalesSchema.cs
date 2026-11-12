using DbDataGateway.Server.Management;
using DbDataKit.Semantic;

namespace DbDataGateway.Tests.Config;

/// <summary>The tables, with the columns, that a publish of the sample catalog checks on its SQL source (plan 14a SL06): one <c>CREATE TABLE</c> per table.</summary>
internal static class SalesSchema
{
    public static IEnumerable<string> Tables(string catalogText, string verb = "CREATE TABLE IF NOT EXISTS") =>
        MappedColumns.Of(CatalogLoader.LoadText(catalogText), "sqlserver_sales")
            .Select(t => $"{verb} {t.Key} ({string.Join(", ", (t.Value.Count == 0 ? ["x"] : t.Value).Select(c => c + " INTEGER"))})");
}
