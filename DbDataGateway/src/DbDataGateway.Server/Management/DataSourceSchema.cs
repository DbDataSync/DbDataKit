using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Serving;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// What a data source's catalog holds, as the Workspace's catalog cache wants it (plan 33 SC20j, decision D20): the tables and views with their columns (name, type text, nullable) and the primary key columns. **A read of the catalog only**: no
/// row is read (the shapes come from the catalog readers and zero-row queries behind <see cref="IServingCatalog"/>), and the data source's login, address and connection string are never in the answer. The Gateway holds the login; the Workspace gets the
/// shape and writes it into a draft as <c>catalog-cache.yaml</c>.
/// </summary>
public static class DataSourceSchema
{
    public const int MaxTables = 20_000;
    public const int MaxColumns = 5_000;

    private static readonly HashSet<string> SystemSchemas = new(["information_schema", "pg_catalog", "pg_toast", "sys", "guest", "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator", "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The type of an Arrow field as text the way a person reads a column type (<c>int64</c>, <c>decimal(18,2)</c>, <c>string</c>, <c>timestamp</c>).</summary>
    public static string TypeText(IArrowType type) => type switch
    {
        Decimal128Type d => $"decimal({d.Precision},{d.Scale})",
        Decimal256Type d => $"decimal({d.Precision},{d.Scale})",
        TimestampType t => t.Timezone is { Length: > 0 } ? "timestamp with time zone" : "timestamp",
        _ => type.Name,
    };

    public static async Task<JsonObject> ReadAsync(IServingCatalog catalog, string dataSource, string engine, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = new ServingContext(new GatewayCaller(new GatewayPrincipal("management", "management", ["*"])), ClientAddress: null);
        var tables = new JsonArray();
        var columns = 0;
        var truncated = false;
        await foreach (var o in catalog.DescribeAsync(context, [], withColumns: true, withPrimaryKeys: true, source: dataSource, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (o.Kind is not (ServingObjectKind.Table or ServingObjectKind.View)) continue;
            var schema = o.SchemaName ?? (o.Path.Count > 1 ? o.Path[^2] : "");
            if (SystemSchemas.Contains(schema)) continue;
            var fields = o.Columns?.FieldsList ?? [];
            if (tables.Count >= MaxTables || columns + fields.Count > MaxColumns * 4) { truncated = true; break; }
            var key = new HashSet<string>(o.PrimaryKey ?? [], StringComparer.OrdinalIgnoreCase);
            var list = new JsonArray();
            foreach (var f in fields.Take(MaxColumns))
                list.Add(new JsonObject { ["name"] = f.Name, ["type"] = TypeText(f.DataType), ["nullable"] = f.IsNullable, ["key"] = key.Contains(f.Name) });
            columns += list.Count;
            tables.Add(new JsonObject { ["schema"] = schema, ["name"] = o.Name, ["kind"] = o.Kind == ServingObjectKind.View ? "view" : "table", ["columns"] = list });
        }
        return new JsonObject { ["data_source"] = dataSource, ["engine"] = engine, ["generated_at"] = now, ["tables"] = tables, ["truncated"] = truncated };
    }
}
