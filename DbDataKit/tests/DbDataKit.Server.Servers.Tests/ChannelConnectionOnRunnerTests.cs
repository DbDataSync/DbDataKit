using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Deploy;
using DbDataKit.Runner;
using DbDataKit.Scheduling;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// A connection put on a Runner for a channel through the console, with the real programs: a runner process serving <c>prod</c>, <c>dev</c> and <c>test</c> holds the package <c>crm-to-dw</c> in <c>test</c>, whose task writes through the
/// slot <c>target</c>; a console in this process. As the signed-in administrator, the console stores the login's password in the runner's secret store, puts the connection's copy (the user as plain text, the password a reference to that
/// secret) and binds <c>target</c> in <c>test</c>. The runner's Bindings (the package's description) show the bind, and its copy names the person who sent it, says the reference resolves there and that <c>test</c>'s slot uses it.
/// A viewer of the console is refused by the runner, which decides by its own roles.
/// </summary>
public sealed class ChannelConnectionOnRunnerTests
{
    private const string Package = "crm-to-dw";
    private const string Channel = "test";
    private const string Copy = "crm-to-dw-target-test";
    private const string Password = "pw-channel-login-4471";
    private const string Secret = "dbdatakit:runner:dev-login:crm-to-dw.test.target:password";

    /// <summary>The package as the runner holds it: one task that reads <c>source</c> and writes <c>target</c> (its files are never run here).</summary>
    private static string WritePackage(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        File.WriteAllText(Path.Combine(folder, "files", "Load.dll"), "not run here");
        var task = new PackageTask("load", new TaskEntry("Load"), new Schedule(), [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], [])
        {
            Connections = [new ConnectionSlot("source", "database", Access: SlotAccess.Read), new ConnectionSlot("target", "database")],
        };
        var manifest = new PackageManifest(Package, null, new PackageLaunch(LaunchKind.Assembly, Assembly: "Load.dll"), new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), [task]);
        File.WriteAllText(Path.Combine(folder, "package.yaml"), PackageManifestWriter.Write(manifest));
        return folder;
    }

    [Fact]
    public async Task The_console_puts_a_copy_on_a_runner_and_binds_the_write_slot_of_test_as_the_person_and_the_runner_s_bindings_show_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-channelconn-" + Guid.NewGuid().ToString("N")[..8]);
        RealProcess? runnerProcess = null;
        WebApplication? console = null;
        try
        {
            var runner = Programs.Runner;
            var runnerDirectory = Path.Combine(root, "runner");
            Directory.CreateDirectory(runnerDirectory);
            var package = WritePackage(Path.Combine(root, "package"));
            await RealProcess.RunAsync(runner, "package", "install", Path.Combine(package, "package.yaml"), Path.Combine(package, "files"), "--channel", Channel, "--repo", runnerDirectory);
            var code = Programs.CodeIn(await RealProcess.RunAsync(runner, "server", "pair", "--repo", runnerDirectory, "--max-role", "admin"));
            var runnerUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
            runnerProcess = RealProcess.Start(runner, ["server", "run", "--repo", runnerDirectory, "--listen", runnerUrl.AbsoluteUri.TrimEnd('/'), "--channels", "prod,dev,test"]);
            await runnerProcess.WaitUntilAsync(new Uri(runnerUrl, "/health"), TimeSpan.FromSeconds(90));

            var consoleDirectory = Path.Combine(root, "console");
            console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
            await console.StartAsync();
            var runnerId = (await console.Services.GetRequiredService<ConsoleInstances>().PairAsync(runnerUrl, code, "runners-eu", default)).InstanceId;
            var accounts = console.Services.GetRequiredService<IUserAccounts>();
            var ada = new RelayPerson(accounts.CreateUser("Ada Admin", null, UserRole.Admin).Id, "Ada Admin", UserRole.Admin);
            var vic = new RelayPerson(accounts.CreateUser("Vic Viewer", null, UserRole.Viewer).Id, "Vic Viewer", UserRole.Viewer);
            var channels = console.Services.GetRequiredService<ConsoleChannelConnections>();

            var definition = $"schema: connection/1\nname: {Copy}\nengine: postgres\nconnection_string: 'Host=dw.example;Database=dw_test'\nauth: {{ mode: sql, user: ana_test, password_secret: 'secret:{Secret}' }}\n";
            var request = new ChannelConnectionSetUp(Package, Channel, "target", Copy, definition, new ChannelLogin(Secret, Password));

            // a viewer of the console: the runner refuses the login, and nothing is put
            var refused = await channels.SetUpAsync(runnerId, request, vic, default);
            Assert.True(refused.Status == 403, $"{refused.Status} {refused.Body}");
            Assert.DoesNotContain(Password, refused.Body.ToJsonString());

            // the administrator: the login, the copy, the bind of the write slot in test
            var answer = await channels.SetUpAsync(runnerId, request, ada, default);
            Assert.True(answer.Status == 200, $"{answer.Status} {answer.Body}\n{runnerProcess.Output}");
            Assert.True((bool?)answer.Body["bound"]);
            Assert.Equal(Secret, (string?)answer.Body["login_secret"]);
            Assert.Empty(answer.Body["unresolved"]!.AsArray());
            Assert.Equal(ConnectionCopyVersion.Of(definition), (string?)answer.Body["version"]);
            Assert.DoesNotContain(Password, answer.Body.ToJsonString());

            // the runner's Bindings: the package's description binds target in test to the copy
            var relay = console.Services.GetRequiredService<ConsoleRelay>();
            var instance = console.Services.GetRequiredService<ConsoleInstances>().Registry.Find(runnerId)!;
            var (described, body) = await relay.CallAsAsync(instance, ada.Id, ada.Name, ada.Role, HttpMethod.Get, $"/api/v1/definitions/integration/{Package}", null, default);
            Assert.True(described == 200, $"{described} {body}");
            var binding = Assert.Single(body!["bindings"]!.AsArray().OfType<JsonObject>(), b => (string?)b["channel"] == Channel);
            Assert.Equal(("target", Copy), ((string?)binding["slot"], (string?)binding["bound_to"]));

            // its copy: the user as written, the password a reference that resolves on the runner, used by test's slot
            var (_, copies) = await relay.CallAsAsync(instance, ada.Id, ada.Name, ada.Role, HttpMethod.Get, "/api/v1/connection-copies", null, default);
            var copy = Assert.Single(copies!["copies"]!.AsArray().OfType<JsonObject>());
            Assert.Equal(Copy, (string?)copy["name"]);
            Assert.Contains("user: ana_test", (string?)copy["definition"]);
            Assert.True((bool?)copy["secrets"]![0]!["resolves"], copy.ToJsonString());
            Assert.Contains(copy["used_by"]!.AsArray().OfType<JsonObject>(), u => (string?)u["package"] == Package && (string?)u["channel"] == Channel && (string?)u["slot"] == "target");
            Assert.DoesNotContain(Password, copies.ToJsonString());

            // the runner holds the password, and the copy names the person who sent it
            Assert.Contains(ada.Id, (string?)copy["sent_by"] ?? "");
            var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(runnerDirectory), SecretKeys.Open(runnerDirectory, new SecretKeyOptions()));
            using (var held = await store.TryResolveAsync(new SecretRef(SecretRefKind.Secret, Secret)))
                Assert.Equal(Password, held!.RevealString());
        }
        finally
        {
            if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
            if (runnerProcess is not null) await runnerProcess.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
