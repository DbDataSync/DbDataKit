using DbDataKit.Connections;
using DbDataKit.Dialect;

namespace DbDataKit.Lineage;

/// <summary>
/// A dataset by the shape OpenLineage gives one: a <see cref="Namespace"/> (the engine and the server) and a <see cref="Name"/> (database, schema and table). <see cref="ToString"/> is the node id.
/// </summary>
public sealed record DatasetIdentity(string Namespace, string Name)
{
    public override string ToString() => $"{Namespace}/{Name}";
}

/// <summary>
/// Who a dataset or column is (plan 18, section 4), worked out from a <c>connection/1</c> definition and not from each product's own names, so Sync's target table, Build's source and Proxy's served table are one node. Two definitions of one server meet in
/// one identity: the host is lower-cased, the machine's own names (<c>.</c>, <c>(local)</c>, <c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>) are one, a port that is the engine's default is left out, and SQL Server's <c>tcp:</c> prefix and <c>host,port</c> form
/// are read. Names are taken as the engine stores them: a dialect that folds unquoted names folds them here; one that keeps the case keeps what it was given.
/// </summary>
public static class LineageIdentity
{
    private static readonly Dictionary<string, int> DefaultPorts = new(StringComparer.Ordinal) { ["sqlserver"] = 1433, ["fabric"] = 1433, ["postgres"] = 5432, ["mysql"] = 3306, ["oracle"] = 1521 };

    private static readonly HashSet<string> ThisMachine = new(StringComparer.Ordinal) { ".", "(local)", "localhost", "127.0.0.1", "::1", "[::1]" };

    /// <summary>The dataset a table of this connection is.</summary>
    public static DatasetIdentity Dataset(ConnectionDefinition connection, string? schema, string table)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var engine = connection.Engine.Trim().ToLowerInvariant();
        var (host, port, database) = Parts(connection);
        var rules = Rules(engine);
        string Fold(string? name) => name is null ? "" : rules.FoldCase(name);
        var name = string.Join('.', new[] { Fold(database), Fold(schema), Fold(table) }.Where(p => p.Length > 0));
        var server = host.Length == 0 ? "" : port is { } p && !(DefaultPorts.TryGetValue(engine, out var normal) && normal == p) ? $"{host}:{p}" : host;
        return new DatasetIdentity($"{engine}://{server}", name);
    }

    /// <summary>A column of a dataset, as the engine stores its name.</summary>
    public static string Column(ConnectionDefinition connection, DatasetIdentity dataset, string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return $"{dataset}#{Rules(connection.Engine.Trim().ToLowerInvariant()).FoldCase(column)}";
    }

    /// <summary>An object of the suite (a replication, a model, a measure, an entity): its server's instance id and its own name.</summary>
    public static string SuiteObject(string instanceId, string kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"suite://{instanceId}/{kind}/{name}";
    }

    private static IdentifierRules Rules(string engine)
    {
        // an engine with no dialect of its own (flightsql, jdbc, odbc) keeps the case it is given
        return DialectCatalog.Builtin.TryGet(new EngineId(engine), out var dialect) && dialect is not null ? dialect.Identifiers : new IdentifierRules(IdentifierCase.Preserve, 128, new HashSet<string>());
    }

    private static (string Host, int? Port, string Database) Parts(ConnectionDefinition connection)
    {
        string? host = connection.Address?.Host, database = connection.Address?.Database;
        int? port = connection.Address?.Port;
        if (host is null && database is null && connection.ConnectionString is { } text)
        {
            var pairs = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim(), StringComparer.Ordinal);
            string? Pick(params string[] keys) => keys.Select(k => pairs.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            host = Pick("server", "data source", "host", "address", "addr", "network address");
            database = Pick("database", "initial catalog", "dbname");
            if (Pick("port") is { } p && int.TryParse(p, out var parsed)) port = parsed;
        }
        return Normalize(host, port, database, connection.Engine.Trim().ToLowerInvariant());
    }

    private static (string Host, int? Port, string Database) Normalize(string? host, int? port, string? database, string engine)
    {
        var text = (host ?? "").Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        // SQL Server writes the port after a comma: host,1433
        if (text.Split(',') is [var before, var after] && int.TryParse(after, out var comma)) (text, port) = (before, comma);
        // host:port, unless it is an IPv6 address
        else if (!text.StartsWith('[') && text.Count(c => c == ':') == 1 && int.TryParse(text[(text.IndexOf(':') + 1)..], out var colon)) (text, port) = (text[..text.IndexOf(':')], colon);
        text = text.ToLowerInvariant();
        if (text.Length > 1) text = text.TrimEnd('.');       // a fully qualified name may end in a dot; a lone dot is the machine itself
        var instance = "";
        if (text.IndexOf('\\') is var slash and >= 0) (text, instance) = (text[..slash], text[slash..]);     // a named instance: the host and the instance are the server, the port is the instance's to choose
        if (ThisMachine.Contains(text)) text = "localhost";
        if (instance.Length > 0) port = null;
        // a file database (DuckDB) is its path
        var db = (database ?? "").Trim();
        if (engine == "duckdb") db = db.Replace('\\', '/');
        return (text + instance, port, db);
    }
}
