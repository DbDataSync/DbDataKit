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

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(WorkerRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Json);

    public static WorkerRequest Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<WorkerRequest>(bytes, Json) ?? throw new InvalidDataException("Empty worker request.");
}

/// <summary>An ODBC statement to run inside the worker. Connection strings stay inside the proxy host's machine.</summary>
public sealed record WorkerRequest(string ConnectionString, string Sql, int BatchSize, int? TimeoutSeconds);

/// <summary>Result of a non-query executed by the worker.</summary>
public sealed record WorkerNonQueryResult(long Affected);
