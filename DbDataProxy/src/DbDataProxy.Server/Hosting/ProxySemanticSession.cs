using Apache.Arrow;
using DbDataKit.Cli;
using DbDataKit.Semantic.Cli;
using DbDataProxy.Client;
using Grpc.Net.Client;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// The Proxy's way of connecting for the <c>semantic</c> noun (plan 19, J15): a Flight SQL session as a user of a running proxy. The password is read from <c>DBPROXY_PASSWORD</c> (or <c>--password</c>, which no usage advertises), so an agent that
/// is offered these commands never puts it in a tool call. The statement goes to the model's source, <c>semantic:&lt;model&gt;</c>, and runs as that user.
/// </summary>
internal sealed class ProxySemanticSession(ProxyFlightClient client) : ISemanticSession
{
    public const string Usage = "[--address URL] --user U [--insecure]   (the password is DBPROXY_PASSWORD)";
    public static readonly string[] Options = ["address", "user", "password"];
    public static readonly string[] Flags = ["insecure"];

    public static async Task<ISemanticSession> ConnectAsync(CliArgs arguments, CliContext context)
    {
        var user = arguments.Get("user") ?? throw new InvalidOperationException("--user names the proxy account to run as.");
        var password = arguments.Get("password") ?? Environment.GetEnvironmentVariable("DBPROXY_PASSWORD") ?? throw new InvalidOperationException("The password is the DBPROXY_PASSWORD environment variable.");
        var handler = new SocketsHttpHandler();
        if (arguments.Has("insecure")) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var client = new ProxyFlightClient(arguments.Get("address") ?? "https://localhost:8815", new GrpcChannelOptions { HttpHandler = handler });
        try { await client.AuthenticateAsync(user, password, context.Cancellation); }
        catch { client.Dispose(); throw; }
        return new ProxySemanticSession(client);
    }

    public async Task<SemanticTable> RunAsync(string model, string statement, int maxRows, CancellationToken cancellationToken)
    {
        var result = await client.QueryAsync(statement, new QueryOptions { DataSource = "semantic:" + model }, cancellationToken);
        var columns = result.Schema.FieldsList.Select(f => new SemanticColumn(f.Name, f.DataType.Name)).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        await foreach (var batch in result.WithCancellation(cancellationToken))
            for (int r = 0; r < batch.Length; r++)
            {
                if (rows.Count >= maxRows) { truncated = true; continue; }
                rows.Add([.. Enumerable.Range(0, batch.ColumnCount).Select(c => Value(batch.Column(c), r))]);
            }
        var notes = (result.Schema.Metadata ?? new Dictionary<string, string>()).Where(m => m.Key.StartsWith("qis.", StringComparison.Ordinal) && m.Key != "qis.descriptor").ToDictionary(m => m.Key, m => m.Value);
        return new SemanticTable(columns, rows, truncated, notes);
    }

    private static object? Value(IArrowArray array, int row) => array.IsNull(row) ? null : array switch
    {
        StringArray a => a.GetString(row),
        BooleanArray a => a.GetValue(row),
        Int8Array a => (long)a.GetValue(row)!.Value,
        Int16Array a => (long)a.GetValue(row)!.Value,
        Int32Array a => (long)a.GetValue(row)!.Value,
        Int64Array a => a.GetValue(row),
        FloatArray a => (double)a.GetValue(row)!.Value,
        DoubleArray a => a.GetValue(row),
        Decimal128Array a => a.GetValue(row),
        _ => array.GetType().Name,
    };

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
