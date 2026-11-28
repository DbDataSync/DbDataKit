using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DbDataKit.Config.Git;
using DbDataKit.Outbound;
using DbDataWorkspace.Server;
using LibGit2Sharp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The Workspace server's outbound list (the <c>outbound:</c> setting of <c>server.yaml</c>): a package source and a project's git remote are listed, so a package source answers through the
/// server's gate and a git fetch works; a call to a Runner's address is refused before it connects and written to the management audit with the destination and the operation, never the path.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceOutboundTests
{
    private const string RunnerAddress = "https://runner.internal:7840/api/v1/runs?channel=prod";

    [Fact]
    public async Task A_call_to_a_Runner_is_refused_and_audited()
    {
        await using var server = await WorkspaceHarness.StartAsync(outbound: ["https://api.nuget.org", "ssh://github.com"]);
        using var http = new HttpClient(server.Outbound.CreateHttpHandler("runner.call", NoProxy()));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(RunnerAddress));
        var refused = Assert.IsType<OutboundRefusedException>(failure.InnerException);
        Assert.Equal("workspace", refused.Service);
        Assert.Equal(new OutboundDestination("runner.internal", 7840), refused.Destination);
        Assert.Throws<OutboundRefusedException>(() => server.Outbound.Check("runner.call", new Uri(RunnerAddress)));

        var audited = server.Audit().Where(e => e.Command == WorkspaceOutbound.AuditCommand).ToList();
        Assert.Equal(2, audited.Count);
        Assert.All(audited, e =>
        {
            Assert.Equal("runner.internal:7840", e.Target);
            Assert.Equal("refused: runner.call", e.Outcome);
            Assert.Equal("dbdataworkspace", e.UserId);
        });
        Assert.DoesNotContain("/api/v1/runs", File.ReadAllText(Path.Combine(server.Directory, "management-audit.log")));
    }

    [Fact]
    public async Task A_server_without_the_setting_connects_anywhere_and_a_wrong_entry_fails_the_start_naming_the_file()
    {
        await using (var open = await WorkspaceHarness.StartAsync())
        {
            Assert.False(open.Outbound.List.Restricted);
            open.Outbound.Check("runner.call", new Uri(RunnerAddress));
        }

        var directory = Directory.CreateTempSubdirectory("ddw-outbound-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "server.yaml"), "outbound:\n  - https://api.nuget.org/v3/index.json\n");
            var failure = Assert.Throws<OutboundSettingsException>(() => WorkspaceServer.Build(new WorkspaceSettings(directory, new Uri("https://127.0.0.1:0")), WorkspaceServer.Certificate(directory), _ => { }));
            Assert.Contains("server.yaml", failure.Message);
            Assert.Contains("https://api.nuget.org/v3/index.json", failure.Message);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    /// <summary>A NuGet feed on this machine, listed by its address: its service index and a package's versions are read through the server's gate, the way a restore starts.</summary>
    [Fact]
    public async Task A_listed_package_source_answers_through_the_gate()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var feed = builder.Build();
        feed.MapGet("/v3/index.json", (HttpContext context) => Results.Json(new
        {
            version = "3.0.0",
            resources = new[] { new Dictionary<string, string> { ["@id"] = $"{context.Request.Scheme}://{context.Request.Host}/v3-flatcontainer/", ["@type"] = "PackageBaseAddress/3.0.0" } },
        }));
        feed.MapGet("/v3-flatcontainer/{id}/index.json", (string id) => Results.Json(new { versions = new[] { "1.0.0", "1.1.0" } }));
        await feed.StartAsync();
        var address = new Uri(feed.Urls.First());

        await using var server = await WorkspaceHarness.StartAsync(outbound: [$"http://127.0.0.1:{address.Port}", "ssh://github.com"]);
        using var http = new HttpClient(server.Outbound.CreateHttpHandler("package.fetch", NoProxy()));

        var index = await http.GetStringAsync(new Uri(address, "/v3/index.json"));
        Assert.Contains("PackageBaseAddress/3.0.0", index);
        Assert.Contains("1.1.0", await http.GetStringAsync(new Uri(address, "/v3-flatcontainer/newtonsoft.json/index.json")));
        Assert.DoesNotContain(server.Audit(), e => e.Command == WorkspaceOutbound.AuditCommand);
    }

    /// <summary>
    /// A project's remote served by <c>git daemon</c> on this machine and listed as <c>git://127.0.0.1:port</c>: the server's gate passes it, and a clone and then a fetch of a new commit work;
    /// the same repository at an address that is not listed is refused before anything connects. Linux only: the daemon is the fixture, and the gate's check is the same code on Windows.
    /// </summary>
    [Fact]
    public async Task A_git_fetch_from_a_listed_remote_works_and_an_unlisted_remote_is_refused()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("git daemon is the fixture of this test; it is run on Linux.");
        var root = Directory.CreateTempSubdirectory("ddw-outbound-git-").FullName;
        Process? daemon = null;
        try
        {
            var work = Path.Combine(root, "work");
            Repository.Init(work);
            File.WriteAllText(Path.Combine(work, "README.md"), "first\n");
            var who = new Signature("Ada", "ada@example.com", DateTimeOffset.UtcNow);
            using (var repo = new Repository(work))
            {
                Commands.Stage(repo, "*");
                repo.Commit("first", who, who);
            }
            var served = Path.Combine(root, "served");
            Directory.CreateDirectory(served);
            Repository.Clone(work, Path.Combine(served, "project.git"), new CloneOptions { IsBare = true });

            var port = FreePort();
            daemon = Process.Start(new ProcessStartInfo("git", ["daemon", "--reuseaddr", "--export-all", "--listen=127.0.0.1", $"--port={port}", $"--base-path={served}", served])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            await WaitForPortAsync(port);

            await using var server = await WorkspaceHarness.StartAsync(outbound: ["https://api.nuget.org", $"git://127.0.0.1:{port}"]);
            var remote = $"git://127.0.0.1:{port}/project.git";
            var clone = Path.Combine(root, "clone");

            server.Outbound.CheckGitRemote("git.fetch", remote);
            Assert.True(GitRemote.CloneOrUpdate(remote, null, clone).Cloned);

            File.WriteAllText(Path.Combine(work, "README.md"), "second\n");
            using (var repo = new Repository(work))
            {
                Commands.Stage(repo, "*");
                repo.Commit("second", who, who);
                repo.Network.Remotes.Add("served", Path.Combine(served, "project.git"));
                repo.Network.Push(repo.Network.Remotes["served"], $"{repo.Head.CanonicalName}:{repo.Head.CanonicalName}");
            }
            server.Outbound.CheckGitRemote("git.fetch", remote);
            var fetched = GitRemote.CloneOrUpdate(remote, null, clone);
            Assert.False(fetched.Cloned);
            Assert.Equal("second\n", File.ReadAllText(Path.Combine(clone, "README.md")).ReplaceLineEndings("\n"));

            var refused = Assert.Throws<OutboundRefusedException>(() => server.Outbound.CheckGitRemote("git.fetch", $"git://localhost:{port}/project.git"));
            Assert.Equal(new OutboundDestination("localhost", port), refused.Destination);
            Assert.Contains(server.Audit(), e => e.Command == WorkspaceOutbound.AuditCommand && e.Target == $"localhost:{port}" && e.Outcome == "refused: git.fetch");
        }
        finally
        {
            if (daemon is not null)
            {
                try { daemon.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await daemon.WaitForExitAsync();
                daemon.Dispose();
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>A handler that connects directly: behind a proxy (a machine's HTTPS_PROXY) the gate checks the proxy's address, which is not what these tests are about.</summary>
    private static SocketsHttpHandler NoProxy() => new() { UseProxy = false };

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < until) { await Task.Delay(100); }
        }
    }
}
