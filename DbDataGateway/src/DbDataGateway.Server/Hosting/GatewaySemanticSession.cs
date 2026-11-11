using Apache.Arrow;
using DbDataKit.Cli;
using DbDataKit.Semantic.Cli;
using DbDataGateway.Client;
using Grpc.Net.Client;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The Gateway's way of connecting for the <c>semantic</c> noun (plan 19, J15): a Flight SQL session as a user of a running gateway. The password is read from <c>DBDATAKIT_GATEWAY_PASSWORD</c> (or <c>--password</c>, which no usage advertises), so an agent that
/// is offered these commands never puts it in a tool call. The statement goes to the model's source, <c>semantic:&lt;model&gt;</c>, and runs as that user.
/// </summary>
internal sealed class GatewaySemanticSession(GatewayFlightClient client) : ISemanticSession
{
    public const string Usage = "[--address URL] [--user U] [--insecure]   (a token is DBDATAKIT_GATEWAY_TOKEN; with --user, the password is DBDATAKIT_GATEWAY_PASSWORD)";
    public const string TokenVariable = "DBDATAKIT_GATEWAY_TOKEN";
    public static readonly string[] Options = ["address", "user", "password"];
    public static readonly string[] Flags = ["insecure"];

    public static async Task<ISemanticSession> ConnectAsync(CliArgs arguments, CliContext context)
    {
        // a personal access token or an access token of the identity home (plan 14a SL21, D24): read from the environment, never a flag, sent as the bearer credential of the session; --user names an account that signs in with a password instead
        if (arguments.Get("user") is null)
        {
            var token = Environment.GetEnvironmentVariable(TokenVariable);
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException($"Name the person to run as: the token in the {TokenVariable} environment variable, or --user and the password in DBDATAKIT_GATEWAY_PASSWORD.");
            return ForToken(arguments.Get("address") ?? "https://localhost:8815", token.Trim(), arguments.Has("insecure"));
        }
        var user = arguments.Get("user")!;
        var password = arguments.Get("password") ?? Environment.GetEnvironmentVariable("DBDATAKIT_GATEWAY_PASSWORD") ?? throw new InvalidOperationException("The password is the DBDATAKIT_GATEWAY_PASSWORD environment variable.");
        var handler = new SocketsHttpHandler();
        if (arguments.Has("insecure")) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var client = new GatewayFlightClient(arguments.Get("address") ?? "https://localhost:8815", new GrpcChannelOptions { HttpHandler = handler });
        try { await client.AuthenticateAsync(user, password, context.Cancellation); }
        catch { client.Dispose(); throw; }
        return new GatewaySemanticSession(client);
    }

    /// <summary>A session that sends <paramref name="token"/> as <c>Authorization: Bearer</c> on every call: no handshake, so a token revoked on the Gateway is refused at the next statement with the Gateway's own message.</summary>
    internal static ISemanticSession ForToken(string address, string token, bool insecure)
    {
        var handler = new SocketsHttpHandler();
        if (insecure) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new GatewaySemanticSession(new GatewayFlightClient(address, new GrpcChannelOptions { HttpHandler = handler }) { Authorization = "Bearer " + token });
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
