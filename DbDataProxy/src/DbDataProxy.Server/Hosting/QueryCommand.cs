using System.Text;
using Apache.Arrow;
using DbDataProxy.Client;
using Grpc.Net.Client;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// "DbDataProxy query": a small built-in client for smoke-testing a running proxy (connectivity, credentials, data
/// sources, compression) without any other tooling.
/// </summary>
public static class QueryCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? address = Option(args, "--address") ?? "https://localhost:8815";
        string? user = Option(args, "--user");
        string? password = Option(args, "--password") ?? Environment.GetEnvironmentVariable("DBPROXY_PASSWORD");
        string? sql = Option(args, "--sql");
        if (user is null || password is null || sql is null)
        {
            Console.Error.WriteLine("Usage: DbDataProxy query --user U --password P --sql \"SELECT ...\" [--address URL] " +
                                    "[--datasource NAME] [--compression none|gzip|zstd|lz4] [--max-rows N] [--insecure]");
            return 2;
        }

        int maxRows = int.TryParse(Option(args, "--max-rows"), out int m) ? m : 20;
        var handler = new SocketsHttpHandler();
        if (args.Contains("--insecure"))
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        using var client = new ProxyFlightClient(address, new GrpcChannelOptions { HttpHandler = handler });
        try
        {
            await client.AuthenticateAsync(user, password);
            var options = new QueryOptions { DataSource = Option(args, "--datasource"), Compression = Option(args, "--compression") };
            var result = await client.QueryAsync(sql, options);

            Console.WriteLine(string.Join(" | ", result.Schema.FieldsList.Select(f => $"{f.Name}:{f.DataType.Name}")));
            long rows = 0;
            await foreach (var batch in result)
            {
                for (int r = 0; r < batch.Length; r++, rows++)
                    if (rows < maxRows) Console.WriteLine(Format(batch, r));
            }
            Console.WriteLine($"({rows} rows)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.InnerException?.Message ?? ex.Message);
            return 1;
        }
    }

    private static string Format(RecordBatch batch, int row)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < batch.ColumnCount; c++)
        {
            if (c > 0) sb.Append(" | ");
            var column = batch.Column(c);
            sb.Append(column.IsNull(row) ? "NULL" : Value(column, row));
        }
        return sb.ToString();
    }

    private static string Value(IArrowArray array, int row) => array switch
    {
        StringArray s => s.GetString(row),
        Int8Array a => a.GetValue(row)!.Value.ToString(),
        Int16Array a => a.GetValue(row)!.Value.ToString(),
        Int32Array a => a.GetValue(row)!.Value.ToString(),
        Int64Array a => a.GetValue(row)!.Value.ToString(),
        UInt8Array a => a.GetValue(row)!.Value.ToString(),
        UInt16Array a => a.GetValue(row)!.Value.ToString(),
        UInt32Array a => a.GetValue(row)!.Value.ToString(),
        UInt64Array a => a.GetValue(row)!.Value.ToString(),
        FloatArray a => a.GetValue(row)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DoubleArray a => a.GetValue(row)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BooleanArray a => a.GetValue(row)!.Value.ToString(),
        Decimal128Array a => a.GetValue(row)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Date32Array a => a.GetDateTimeOffset(row)!.Value.ToString("yyyy-MM-dd"),
        TimestampArray a => a.GetTimestamp(row)!.Value.ToString("yyyy-MM-dd HH:mm:ss.ffffff"),
        _ => "<" + array.Data.DataType.Name + ">",
    };

    private static string? Option(string[] args, string name)
    {
        int i = System.Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
