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
/// A development login set up from a Workspace screen, with the real programs: a runner process, a Workspace server and a console in this process. The screen's call goes to the console, which asks the Workspace server for the
/// project's package and puts the login's password in the runner's secret store as the signed-in person, for the channel and the connection's slot (<c>dev</c>, then <c>prod</c>); the user is not stored as a secret. The runner
/// holds the value; the Workspace server fails any request that carries it, and no file of its directory and no line of its log holds it.
/// </summary>
public sealed class DevelopmentLoginOnRunnerTests
{
    private const string Package = "crm-to-dw";
    private const string Password = "pw-dev-login-real-5583";
    private const string User = "ana_dev_real_2291";

    [Fact]
    public async Task A_login_set_up_for_dev_or_prod_is_held_by_the_runner_as_its_password_only_and_reaches_no_workspace_request_file_or_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-devlogin-" + Guid.NewGuid().ToString("N")[..8]);
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
            runnerProcess = RealProcess.Start(runner, ["server", "run", "--repo", runnerDirectory, "--listen", runnerUrl.AbsoluteUri.TrimEnd('/')]);
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

            var (made, madeBody) = await As(HttpMethod.Post, $"/relay/{workspaceId}/api/v1/projects", new { id = Package, kind = "sync" });
            Assert.True(made == HttpStatusCode.Created, $"{made} {madeBody}");

            var (status, answer) = await As(HttpMethod.Post, $"/api/workspace-dev-logins/{workspaceId}/{Package}", new { runner = runnerId, channel = "dev", connection = "crm", user = User, password = Password });
            Assert.True(status == HttpStatusCode.OK, $"{status} {answer}\n{runnerProcess.Output}");
            Assert.DoesNotContain(Password, answer.ToJsonString());
            const string passwordName = "dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password";
            Assert.Equal(passwordName, (string?)answer["secrets"]?["password"]);

            // the runner holds it, under the name of the package, dev and the slot, and lists it by name only
            var (listed, names) = await As(HttpMethod.Get, $"/relay/{runnerId}/api/v1/secrets/overview");
            Assert.True(listed == HttpStatusCode.OK, $"{listed} {names}");
            Assert.Contains(passwordName, names.ToJsonString());
            Assert.DoesNotContain(Password, names.ToJsonString());
            var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(runnerDirectory), SecretKeys.Open(runnerDirectory, new SecretKeyOptions()));
            using (var held = await store.TryResolveAsync(new SecretRef(SecretRefKind.Secret, passwordName)))
                Assert.Equal(Password, held!.RevealString());
            using (var held = await store.TryResolveAsync(new SecretRef(SecretRefKind.Secret, "dbdatakit:runner:dev-login:crm-to-dw.dev.crm:user")))
                Assert.Null(held);
            Assert.DoesNotContain("dev-login:crm-to-dw.dev.crm:user", names.ToJsonString());
            Assert.Equal(User, (string?)answer["user"]);

            // the Workspace server never received it: no request carried it, and no file of its directory or line of its log holds it
            Assert.Empty(carried);
            Assert.DoesNotContain(workspaceLog, l => l.Contains(Password, StringComparison.Ordinal) || l.Contains(User, StringComparison.Ordinal));
            foreach (var file in Directory.EnumerateFiles(workspaceDirectory, "*", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                var bytes = memory.ToArray();
                foreach (var text in new[] { Encoding.Latin1.GetString(bytes), Encoding.Unicode.GetString(bytes) })
                {
                    Assert.DoesNotContain(Password, text);
                    Assert.DoesNotContain(User, text);
                }
            }

            // a login for prod is stored the same way, its password only, under the name of prod
            var (prod, prodAnswer) = await As(HttpMethod.Post, $"/api/workspace-dev-logins/{workspaceId}/{Package}", new { runner = runnerId, channel = "prod", connection = "crm", user = User, password = Password });
            Assert.True(prod == HttpStatusCode.OK, $"{prod} {prodAnswer}");
            Assert.True((bool?)prodAnswer["production"]);
            using (var stored = await store.TryResolveAsync(new SecretRef(SecretRefKind.Secret, "dbdatakit:runner:dev-login:crm-to-dw.prod.crm:password")))
                Assert.Equal(Password, stored!.RevealString());
            using (var none = await store.TryResolveAsync(new SecretRef(SecretRefKind.Secret, "dbdatakit:runner:dev-login:crm-to-dw.prod.crm:user")))
                Assert.Null(none);
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
