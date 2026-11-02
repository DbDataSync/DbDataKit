using System.Net;
using System.Net.Sockets;
using System.Text;
using DbDataKit.Apps;
using DbDataKit.State;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataProxy.Tests.Config;

/// <summary>Plan 26, AP05: the apps host as a module of the serving layer: off by default, a listener of its own, serving the instances published in the Proxy's state with the sandbox and egress policy.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsModuleTests : IAsyncLifetime
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbproxy-apps-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int appsPort;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public ValueTask InitializeAsync()
    {
        var config = ProxyConfigFile.DirectoryIn(dataDirectory);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(dataDirectory);
    }

    private static HttpClient Trusting(string url) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(url) };

    [Fact]
    public async Task An_instance_published_in_the_state_is_served_by_subpath_with_the_sandbox_policy()
    {
        // the apps host alone: no Flight listener is opened
        appsPort = FreePort();
        app = ProxyHost.Build([$"--Proxy:DataDirectory={dataDirectory}", "--Proxy:Modules:0=apps", "--Proxy:Apps:Address=127.0.0.1", $"--Proxy:Apps:Port={appsPort}"]);
        var store = new AppInstanceStore(new DefinitionStore(app.Services.GetRequiredService<StateDatabase>()));
        store.PublishApp(new AppPackage("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\n",
            new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes("<html><head></head><body>hello notes</body></html>"), ["app.js"] = "1"u8.ToArray() }), "ada");
        store.Save(new AppInstance("acme", "notes"), "ada");
        await app.StartAsync();

        using var http = Trusting($"https://127.0.0.1:{appsPort}");
        var page = await http.GetAsync("/apps/acme/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadAsStringAsync();
        Assert.Contains("hello notes", body);
        Assert.Contains("<base href=\"/apps/acme/\">", body);
        Assert.Contains("sandbox allow-scripts", string.Join(' ', page.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(HttpStatusCode.Redirect, (await http.GetAsync("/apps/acme")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/apps/acme/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/apps/globex/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/apps/acme/missing.js")).StatusCode);
    }

    [Fact]
    public void The_options_refuse_the_apps_host_without_tls_or_on_another_listeners_port()
    {
        static IReadOnlyList<DbDataKit.Config.Yaml.ConfigDiagnostic> Load(string yaml)
        {
            using var file = TempConfig.Create(("server.yaml", yaml));
            return ConfigTestSupport.LoadDirectory(file.Directory).Diagnostics;
        }

        Assert.Contains(Load("tls: { mode: none }\nmodules: [apps]\n"), d => d.KeyPath == "Modules");
        Assert.Contains(Load("listen: { address: 127.0.0.1, port: 8818 }\nmodules: [flightsql, apps]\n"), d => d.KeyPath == "Apps.Port");
        Assert.Contains(Load("management: { enabled: true, port: 8818 }\nmodules: [apps]\n"), d => d.KeyPath == "Apps.Port");
        Assert.Contains(Load("identity: { enabled: true, port: 8818 }\nmodules: [apps]\n"), d => d.KeyPath == "Apps.Port");
        Assert.Contains(Load("modules: [apps]\napps: { address: nowhere }\n"), d => d.KeyPath == "Apps.Address");
        Assert.DoesNotContain(Load("modules: [apps]\n"), d => d.KeyPath?.StartsWith("Apps", StringComparison.Ordinal) == true);
    }
}
