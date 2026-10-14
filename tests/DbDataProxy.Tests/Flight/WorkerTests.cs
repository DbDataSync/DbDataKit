using Apache.Arrow;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// Uses the Microsoft Text ODBC driver, which exists only as a 32-bit driver, to prove queries really run in the
/// 32-bit worker: the same data source fails in the 64-bit proxy process.
/// </summary>
public sealed class WorkerTests : IAsyncLifetime
{
    private const string Driver = "{Microsoft Text Driver (*.txt; *.csv)}";

    private string _dir = "";
    private WebApplication? _app;
    private string _address = "";

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "Requires 64-bit Windows with the 32-bit text ODBC driver.");

        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "people.csv"), "name,age\r\nAda,36\r\nGrace,45\r\nLinus,28\r\n");

        string connection = $"Driver={Driver};Dbq={_dir};Extensions=asc,csv,tab,txt;";
        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:Worker:Path"] = WorkerBuild.EnsurePublished(),
            ["Proxy:DataSources:text32:Provider"] = "odbc",
            ["Proxy:DataSources:text32:Bitness"] = "x86",
            ["Proxy:DataSources:text32:ConnectionString"] = connection,
            ["Proxy:DataSources:text64:Provider"] = "odbc",
            ["Proxy:DataSources:text64:Bitness"] = "x64",
            ["Proxy:DataSources:text64:ConnectionString"] = connection,
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
            ["Proxy:Users:0:DataSources:0"] = "*",
        };
        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { if (_dir.Length > 0) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<ProxyClient> LoginAsync()
    {
        var client = new ProxyClient(_address);
        await client.AuthenticateAsync("alice", "pw");
        return client;
    }

    [Fact]
    public async Task Queries_a_32_bit_only_driver_through_the_worker()
    {
        using var client = await LoginAsync();
        var (schema, batches) = await client.QueryAsync("SELECT name, age FROM [people.csv] ORDER BY age", dataSource: "text32");

        Assert.Equal(["name", "age"], schema.FieldsList.Select(f => f.Name));
        var batch = Assert.Single(batches);
        Assert.Equal(3, batch.Length);
        Assert.Equal("Linus", ((StringArray)batch.Column("name")).GetString(0));
        Assert.Equal("Grace", ((StringArray)batch.Column("name")).GetString(2));
    }

    [Fact]
    public async Task The_same_driver_is_unavailable_to_the_64_bit_proxy_process()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT name FROM [people.csv]", dataSource: "text64"));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Worker_reports_sql_errors_to_the_client()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT nothing FROM [missing.csv]", dataSource: "text32"));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Worker_serves_many_sequential_and_concurrent_queries()
    {
        using var client = await LoginAsync();
        var tasks = Enumerable.Range(0, 8).Select(_ => client.QueryAsync("SELECT name FROM [people.csv]", dataSource: "text32"));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(3, r.Batches.Sum(b => b.Length)));
    }
}
