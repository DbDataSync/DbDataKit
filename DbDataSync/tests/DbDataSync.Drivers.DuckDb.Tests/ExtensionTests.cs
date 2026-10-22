using System.Net;
using System.Text;
using DuckDB.NET.Data;

namespace DbDataSync.Drivers.DuckDb.Tests;

/// <summary>
/// A DuckDB source that reads a file over HTTP needs the <c>httpfs</c> extension, which DuckDB installs for the engine's own version
/// from its extension server (plan 13, K06). This installs it for the pinned 2.0.0 engine into a fresh directory and reads a file through
/// it, served from a listener on this machine so that only the extension download needs the internet.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExtensionTests : IDisposable
{
    private readonly string _extensions = Path.Combine(Path.GetTempPath(), "dbdatasync-duckdb-ext-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_extensions, recursive: true); } catch (IOException) { }
    }

    [NetworkFact]
    public async Task Httpfs_InstallsForTheEngineVersion_AndReadsAFileOverHttp()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var served = Task.Run(async () =>
        {
            // httpfs asks for the size first (HEAD) and then for the bytes (GET): answer every request until stopped.
            var body = Encoding.UTF8.GetBytes("id,name\n1,Ada\n2,Grace\n3,Linus\n");
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                context.Response.ContentType = "text/csv";
                context.Response.ContentLength64 = body.Length;
                if (context.Request.HttpMethod != "HEAD") await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });

        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, $"SET extension_directory = '{_extensions.Replace("\\", "/")}'");
        Execute(connection, "INSTALL httpfs");
        Execute(connection, "LOAD httpfs");

        Assert.Equal("v2.0.0", Scalar(connection, "SELECT version()"));
        Assert.Equal("true", Scalar(connection, "SELECT installed::VARCHAR FROM duckdb_extensions() WHERE extension_name = 'httpfs'"));
        Assert.Equal("3", Scalar(connection, $"SELECT count(*)::VARCHAR FROM read_csv('http://127.0.0.1:{port}/orders.csv')"));
        Assert.Equal("Grace", Scalar(connection, $"SELECT name FROM read_csv('http://127.0.0.1:{port}/orders.csv') WHERE id = 2"));
        Assert.NotEmpty(Directory.EnumerateFiles(_extensions, "httpfs*", SearchOption.AllDirectories));   // it was installed here, not found elsewhere

        listener.Stop();
        await served;
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Scalar(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}
