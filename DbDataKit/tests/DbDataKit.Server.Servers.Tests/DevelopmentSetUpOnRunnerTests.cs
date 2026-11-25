using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Set up for development from a draft, with the real programs: a runner process serving <c>prod</c> and <c>dev</c> that holds no version of the package yet, a Workspace server and a console in this process. The screen's one call
/// goes to the console, which reads the draft's connection file from the Workspace server as the person, stores the password in the runner's secret store, puts the connection's copy (the user as plain text, the password a reference to
/// that secret) and binds the slot <c>crm</c> of <c>crm-to-dw</c> in <c>dev</c>. The runner lists the copy with the user, used by that slot, its reference resolving there; no request to the Workspace server, file of its directory or
/// line of its log holds the password or the user.
/// </summary>
public sealed class DevelopmentSetUpOnRunnerTests
{
    private const string Package = "crm-to-dw";
    private const string Password = "pw-dev-setup-real-6619";
    private const string User = "ana_dev_setup_3307";

    [Fact]
    public async Task Set_up_for_dev_from_a_draft_puts_the_copy_with_the_user_on_the_runner_and_binds_the_slot_and_the_login_reaches_no_workspace_request_file_or_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-devsetup-" + Guid.NewGuid().ToString("N")[..8]);
        RealProcess? runnerProcess = null;
        WebApplication? workspace = null, console = null;
        var carried = new ConcurrentQueue<string>();
        var workspaceLog = new ConcurrentQueue<string>();
        try
        {
            var runner = Programs.Runner;
            var runnerDirectory = Path.Combine(root, "runner");
            Directory.CreateDirectory(runnerDirectory);
            var code = Programs.CodeIn(await RealProcess.RunAsync(runner, "server", "pair", "--repo", runnerDirectory, "--max-role", "admin"));
            var runnerUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
            runnerProcess = RealProcess.Start(runner, ["server", "run", "--repo", runnerDirectory, "--listen", runnerUrl.AbsoluteUri.TrimEnd('/'), "--channels", "prod,dev"]);
            await runnerProcess.WaitUntilAsync(new Uri(runnerUrl, "/health"), TimeSpan.FromSeconds(90));

            // the Workspace server, paired with the console only; any request that carries the login fails
            var workspaceDirectory = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceDirectory);
            workspace = WorkspaceServer.Build(new WorkspaceSettings(workspaceDirectory, new Uri("https://127.0.0.1:0")), WorkspaceServer.Certificate(workspaceDirectory), workspaceLog.Enqueue);
            workspace.Use(async (context, next) =>
            {
                var body = "";
                // the pairing route sets its own size limit before it reads, so only the management calls' bodies are read here
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    var bytes = new MemoryStream();
                    await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
                    body = Encoding.UTF8.GetString(bytes.ToArray());
                    bytes.Position = 0;
                    context.Request.Body = bytes;
                }
                var seen = new StringBuilder().Append(context.Request.Path).Append(context.Request.QueryString).Append('\n').Append(body);
                foreach (var (name, values) in context.Request.Headers) seen.Append('\n').Append(name).Append(": ").Append(values.ToString());
                if (seen.ToString() is var text && (text.Contains(Password, StringComparison.Ordinal) || text.Contains(User, StringComparison.Ordinal)))
                {
                    carried.Enqueue($"{context.Request.Method} {context.Request.Path}");
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { error = "This Workspace server received a login." });
                    return;
                }
                await next(context);
            });
            await workspace.StartAsync();

            var consoleDirectory = Path.Combine(root, "console");
            console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
            await console.StartAsync();
            var instances = console.Services.GetRequiredService<ConsoleInstances>();
            var workspaceCode = WorkspaceState.Pairing(workspaceDirectory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
            var workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), workspaceCode.Code, "Workspace", default)).InstanceId;
            var runnerId = (await instances.PairAsync(runnerUrl, code, "runners-eu", default)).InstanceId;
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
            var ada = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Author", null, UserRole.Admin).Id;
            async Task<(HttpStatusCode Status, JsonObject Body)> As(HttpMethod method, string path, object? body = null)
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(ada).Id}");
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request);
                return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
            }

            // a sync project and a draft with the connection crm, as a person saves it: its own user and a reference, never a password
            var (made, madeBody) = await As(HttpMethod.Post, $"/relay/{workspaceId}/api/v1/projects", new { id = Package, kind = "sync" });
            Assert.True(made == HttpStatusCode.Created, $"{made} {madeBody}");
            var (opened, draftBody) = await As(HttpMethod.Post, $"/relay/{workspaceId}/api/v1/projects/{Package}/workspaces", new { name = "draft" });
            Assert.True(opened is HttpStatusCode.OK or HttpStatusCode.Created, $"{opened} {draftBody}");
            var draft = (string)draftBody["id"]!;
            const string file = "schema: connection/1\nname: crm\nengine: postgres\nconnection_string: 'Host=crm.example;Database=crm'\nauth:\n  mode: sql\n  user: sync\n  password_secret: secret:dbdatakit:sync:connection:crm\n";
            var (saved, savedBody) = await As(HttpMethod.Put, $"/relay/{workspaceId}/api/v1/projects/{Package}/workspaces/{draft}/files/config/connections/crm.yaml", new { content = file });
            Assert.True(saved is HttpStatusCode.OK or HttpStatusCode.Created, $"{saved} {savedBody}");

            var (status, answer) = await As(HttpMethod.Post, $"/api/workspace-dev-logins/{workspaceId}/{Package}", new { runner = runnerId, channel = "dev", connection = "crm", user = User, password = Password, draft });
            Assert.True(status == HttpStatusCode.OK, $"{status} {answer}\n{runnerProcess.Output}");
            Assert.True((bool?)answer["bound"], answer.ToJsonString());
            Assert.DoesNotContain(Password, answer.ToJsonString());
            const string passwordName = "dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password";
            const string copyName = "crm-to-dw-crm-dev";
            Assert.Equal((passwordName, copyName, User), ((string?)answer["secrets"]?["password"], (string?)answer["copy"], (string?)answer["user"]));

            // the runner's copy: the user as plain text, the password a reference to the secret it holds, used by the slot crm of the package in dev
            var (listed, copies) = await As(HttpMethod.Get, $"/relay/{runnerId}/api/v1/connection-copies");
            Assert.True(listed == HttpStatusCode.OK, $"{listed} {copies}");
            var copy = Assert.Single(copies["copies"]!.AsArray().OfType<JsonObject>());
            Assert.Equal(copyName, (string?)copy["name"]);
            Assert.Contains($"user: '{User}'", (string?)copy["definition"]);
            Assert.Contains($"secret:{passwordName}", (string?)copy["definition"]);
            Assert.True((bool?)copy["secrets"]![0]!["resolves"], copy.ToJsonString());
            Assert.Contains(copy["used_by"]!.AsArray().OfType<JsonObject>(), u => (string?)u["package"] == Package && (string?)u["channel"] == "dev" && (string?)u["slot"] == "crm");
            Assert.DoesNotContain(Password, copies.ToJsonString());
            var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(runnerDirectory), SecretKeys.Open(runnerDirectory, new SecretKeyOptions()));
            using (var held = await store.TryResolveAsync(new SecretRef(SecretRefKind.Secret, passwordName)))
                Assert.Equal(Password, held!.RevealString());

            // the same set-up again changes nothing and still binds
            var (again, againBody) = await As(HttpMethod.Post, $"/api/workspace-dev-logins/{workspaceId}/{Package}", new { runner = runnerId, channel = "dev", connection = "crm", user = User, password = Password, draft });
            Assert.True(again == HttpStatusCode.OK && (bool?)againBody["bound"] == true, $"{again} {againBody}");
            Assert.Equal(answer["version"]!.GetValue<string>(), againBody["version"]!.GetValue<string>());

            // the Workspace server never received the login: no request carried it, and no file of its directory or line of its log holds it
            Assert.Empty(carried);
            Assert.DoesNotContain(workspaceLog, l => l.Contains(Password, StringComparison.Ordinal) || l.Contains(User, StringComparison.Ordinal));
            foreach (var path in Directory.EnumerateFiles(workspaceDirectory, "*", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                var bytes = memory.ToArray();
                foreach (var text in new[] { Encoding.Latin1.GetString(bytes), Encoding.Unicode.GetString(bytes) })
                {
                    Assert.DoesNotContain(Password, text);
                    Assert.DoesNotContain(User, text);
                }
            }
        }
        finally
        {
            if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
            if (workspace is not null) { await workspace.StopAsync(); await workspace.DisposeAsync(); }
            if (runnerProcess is not null) await runnerProcess.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
