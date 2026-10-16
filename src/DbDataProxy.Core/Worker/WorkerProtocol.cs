using System.Text.Json;

namespace DbDataProxy.Worker;

/// <summary>
/// Wire contract between the proxy and its 32-bit worker process. The worker is a small Flight server on loopback:
/// a query is a DoGet whose ticket is a JSON <see cref="WorkerRequest"/>; a non-query is a DoAction.
/// </summary>
public static class WorkerProtocol
{
    public const string SecretHeader = "x-worker-secret";
    public const string NonQueryAction = "execute-nonquery";
    public const string ListTablesAction = "list-tables";
    public const string DialectAction = "dialect";
    public const string ListColumnsAction = "list-columns";
    public const string ListPrimaryKeysAction = "list-primary-keys";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(WorkerRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Json);

    public static WorkerRequest Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<WorkerRequest>(bytes, Json) ?? throw new InvalidDataException("Empty worker request.");
}

/// <summary>An ODBC statement to run inside the worker. Connection strings stay inside the proxy host's machine. <paramref name="ReadOnly"/> asks the worker to open the connection read-only (the shared provider's recipe).</summary>
public sealed record WorkerRequest(
    string ConnectionString, string Sql, int BatchSize, int? TimeoutSeconds, IReadOnlyList<DbDataProxy.Data.QueryParameter>? Parameters = null,
    bool ReadOnly = false);

/// <summary>Result of a non-query executed by the worker.</summary>
public sealed record WorkerNonQueryResult(long Affected);

/// <summary>Asks the worker for the declared columns of one table.</summary>
public sealed record WorkerColumnsRequest(string ConnectionString, string? Catalog, string? Schema, string Table, bool ReadOnly = false);

/// <summary>Asks the worker for the primary key of one table.</summary>
public sealed record WorkerPrimaryKeysRequest(string ConnectionString, string? Catalog, string? Schema, string Table, bool ReadOnly = false);
