using System.Net;
using System.Net.Sockets;
using System.Text;
using DbDataKit.Outbound;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Outbound.Tests;

public sealed class OutboundTests
{
    private sealed class Audit : IOutboundAudit
    {
        public List<OutboundRefusal> Entries { get; } = [];
        public void Refused(OutboundRefusal refusal) => Entries.Add(refusal);
    }

    private sealed class CapturedLog : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static OutboundList List(params string[] entries) => OutboundList.Parse(entries, "server.yaml");

    private static OutboundDestination D(string host, int port) => new(host, port);

    [Fact]
    public void An_entry_matches_its_host_and_the_port_its_scheme_stands_for()
    {
        var list = List("https://api.nuget.org", "ssh://github.com", "tcp://db.internal:5432", "http://[::1]:8080", "https://*.githubusercontent.com");
        Assert.True(list.Allows(D("api.nuget.org", 443)));
        Assert.True(list.Allows(D("API.NuGet.org.", 443)));
        Assert.False(list.Allows(D("api.nuget.org", 80)));
        Assert.True(list.Allows(D("github.com", 22)));
        Assert.False(list.Allows(D("github.com", 443)));
        Assert.True(list.Allows(D("db.internal", 5432)));
        Assert.True(list.Allows(D("::1", 8080)));
        Assert.True(list.Allows(D("raw.githubusercontent.com", 443)));
        Assert.False(list.Allows(D("githubusercontent.com", 443)));
        Assert.False(list.Allows(D("evilgithubusercontent.com", 443)));
        Assert.False(list.Allows(D("other.example", 443)));
    }

    [Fact]
    public void No_setting_allows_everything_and_an_empty_list_allows_nothing()
    {
        Assert.True(OutboundList.Unrestricted.Allows(D("any.example", 1)));
        Assert.False(OutboundList.Nothing.Allows(D("any.example", 443)));
        Assert.False(List().Allows(D("any.example", 443)));
    }

    [Theory]
    [InlineData("api.nuget.org", "has no scheme")]
    [InlineData("ftp://host", "none of http, https, ssh, git, tcp")]
    [InlineData("https://host/path", "more than a host and a port")]
    [InlineData("https://user@host", "more than a host and a port")]
    [InlineData("tcp://db.internal", "needs a port")]
    [InlineData("https://host:99999", "port")]
    [InlineData("https://*.com", "wildcard")]
    [InlineData("https://", "more than a host and a port")]
    public void An_entry_that_is_not_valid_fails_naming_the_file_and_the_entry(string entry, string why)
    {
        var ex = Assert.Throws<OutboundSettingsException>(() => OutboundList.Parse([entry], "/etc/server.yaml"));
        Assert.Contains("/etc/server.yaml", ex.Message);
        Assert.Contains($"`{entry}`", ex.Message);
        Assert.Contains(why, ex.Message);
    }

    private static string Settings(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"outbound-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public void The_setting_is_read_from_a_settings_file()
    {
        var path = Settings("listen: https://127.0.0.1:7860\noutbound:\n  - https://api.nuget.org\n  - ssh://github.com\n");
        try
        {
            var list = OutboundSettings.Load(path);
            Assert.True(list.Restricted);
            Assert.Equal(["https://api.nuget.org", "ssh://github.com"], list.Entries);
            Assert.True(list.Allows(D("github.com", 22)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_file_or_line_is_unrestricted_and_an_empty_list_is_nothing()
    {
        Assert.False(OutboundSettings.Load(Path.Combine(Path.GetTempPath(), "outbound-absent.yaml")).Restricted);
        var path = Settings("listen: https://127.0.0.1:7860\n");
        var empty = Settings("outbound: []\n");
        try
        {
            Assert.False(OutboundSettings.Load(path).Restricted);
            var none = OutboundSettings.Load(empty);
            Assert.True(none.Restricted);
            Assert.Empty(none.Entries);
        }
        finally { File.Delete(path); File.Delete(empty); }
    }

    [Theory]
    [InlineData("outbound:\n", "has no value")]
    [InlineData("outbound: https://api.nuget.org\n", "must be a list")]
    [InlineData("outbound:\n  - host: x\n", "not text")]
    [InlineData("outbound:\n  - nuget.org\n", "has no scheme")]
    public void A_wrong_setting_fails_naming_the_file(string yaml, string why)
    {
        var path = Settings(yaml);
        try
        {
            var ex = Assert.Throws<OutboundSettingsException>(() => OutboundSettings.Load(path));
            Assert.Contains(path, ex.Message);
            Assert.Contains(why, ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_refusal_is_logged_and_audited_with_the_operation_and_the_destination_and_nothing_else()
    {
        var audit = new Audit();
        var log = new CapturedLog();
        var time = new FixedTime(new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero));
        var gate = new OutboundGate(List("https://api.nuget.org"), "workspace", audit, log, time);

        gate.Check("nuget.restore", new Uri("https://api.nuget.org/v3/index.json"));
        Assert.Empty(audit.Entries);

        var ex = Assert.Throws<OutboundRefusedException>(() => gate.Check("runner.call", new Uri("https://runner.internal:7840/api/v1/runs?token=SECRET")));
        Assert.Equal("runner.internal", ex.Destination.Host);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(new OutboundRefusal(time.GetUtcNow(), "workspace", "runner.call", "runner.internal", 7840), entry);
        Assert.Contains("runner.call", ex.Message);
        Assert.Contains("runner.internal:7840", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message + string.Join('\n', log.Lines));
        Assert.Contains("Warning", Assert.Single(log.Lines));
    }

    [Fact]
    public void A_failing_audit_does_not_hide_the_refusal()
    {
        var gate = new OutboundGate(OutboundList.Nothing, "workspace", new ThrowingAudit());
        Assert.Throws<OutboundRefusedException>(() => gate.Check("git.fetch", "github.com", 22));
    }

    private sealed class ThrowingAudit : IOutboundAudit
    {
        public void Refused(OutboundRefusal refusal) => throw new IOException("audit is down");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", "github.com", 443)]
    [InlineData("ssh://git@github.com/org/repo.git", "github.com", 22)]
    [InlineData("ssh://git@git.example:2222/org/repo.git", "git.example", 2222)]
    [InlineData("git@github.com:org/repo.git", "github.com", 22)]
    [InlineData("git://host.example/repo.git", "host.example", 9418)]
    public void A_git_remote_names_its_destination(string remote, string host, int port) =>
        Assert.Equal(D(host, port), OutboundDestination.FromGitRemote(remote));

    [Theory]
    [InlineData("/srv/repos/a.git")]
    [InlineData("../a.git")]
    [InlineData("C:\\repos\\a")]
    [InlineData("file:///srv/a.git")]
    [InlineData("relative/a.git")]
    public void A_folder_on_this_machine_opens_no_connection(string remote)
    {
        Assert.Null(OutboundDestination.FromGitRemote(remote));
        new OutboundGate(OutboundList.Nothing, "workspace").CheckGitRemote("git.fetch", remote);
    }

    [Fact]
    public void A_git_remote_that_is_not_listed_is_refused()
    {
        var audit = new Audit();
        var gate = new OutboundGate(List("ssh://github.com"), "workspace", audit);
        gate.CheckGitRemote("git.fetch", "git@github.com:org/repo.git");
        Assert.Throws<OutboundRefusedException>(() => gate.CheckGitRemote("git.fetch", "https://github.com/org/repo.git"));
        Assert.Equal("git.fetch", Assert.Single(audit.Entries).Operation);
    }

    /// <summary>A server on a free port that answers every request with <paramref name="response"/> and counts the connections it accepted.</summary>
    private sealed class Server : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public int Accepted;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Server(string response)
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token);
                        Interlocked.Increment(ref Accepted);
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        _ = await stream.ReadAsync(buffer);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                    }
                    catch (Exception) when (stop.IsCancellationRequested) { }
                    catch (IOException) { }
                }
            });
        }

        public void Dispose() { stop.Cancel(); listener.Stop(); }
    }

    private static string Ok(string body = "ok") => $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";

    [Fact]
    public async Task An_http_client_reaches_a_listed_destination_and_is_refused_an_unlisted_one_before_it_connects()
    {
        using var listed = new Server(Ok());
        using var other = new Server(Ok());
        var audit = new Audit();
        var gate = new OutboundGate(List($"http://127.0.0.1:{listed.Port}"), "workspace", audit);
        using var client = new HttpClient(gate.CreateHttpHandler("package.fetch"));

        Assert.Equal("ok", await client.GetStringAsync($"http://127.0.0.1:{listed.Port}/index.json"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync($"http://127.0.0.1:{other.Port}/index.json"));
        var refused = Assert.IsType<OutboundRefusedException>(ex.InnerException);
        Assert.Equal(other.Port, refused.Destination.Port);
        Assert.Equal(0, other.Accepted);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(("package.fetch", other.Port), (entry.Operation, entry.Port));
    }

    [Fact]
    public async Task A_redirect_to_an_unlisted_destination_is_refused()
    {
        using var other = new Server(Ok());
        using var listed = new Server($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{other.Port}/x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var gate = new OutboundGate(List($"http://127.0.0.1:{listed.Port}"), "workspace");
        using var client = new HttpClient(gate.CreateHttpHandler("package.fetch"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync($"http://127.0.0.1:{listed.Port}/start"));
        Assert.IsType<OutboundRefusedException>(ex.InnerException);
        Assert.Equal(0, other.Accepted);
    }

    [Fact]
    public async Task A_connect_by_hand_checks_first()
    {
        using var listed = new Server(Ok());
        var gate = new OutboundGate(List($"tcp://127.0.0.1:{listed.Port}"), "workspace");
        await using (await gate.ConnectAsync("db.connect", "127.0.0.1", listed.Port)) { }
        await Assert.ThrowsAsync<OutboundRefusedException>(async () => await gate.ConnectAsync("db.connect", "127.0.0.1", listed.Port + 1));
    }
}
