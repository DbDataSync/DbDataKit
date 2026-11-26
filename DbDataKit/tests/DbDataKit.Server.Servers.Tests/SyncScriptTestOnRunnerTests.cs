using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// <b>Test</b> of a Sync draft's script, with every piece the program it is (Design: docs/authoring-metadata.md#a-scripts-test-runs-on-a-runner): a Workspace server in this process builds the draft with the real
/// <c>dbdatasync</c>, the console stages the build in <c>dev</c> of a real runner, and the set's task <c>script-test</c> is started through the console as the person; its answer is the cases of the script against
/// generated input. No database is needed: the task opens no connection. The Workspace server starts no script test: its <c>dbdatasync</c> is a wrapper that writes down every command it is asked for.
/// </summary>
public sealed class SyncScriptTestOnRunnerTests : IAsyncLifetime
{
    private const string Channel = "dev";
    private const string RuntimePackage = "dbdatasync-runtime";

    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-script-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private WebApplication console = null!;
    private WebApplication workspace = null!;
    private HttpClient http = null!;
    private string personId = null!;
    private string runnerId = null!;
    private string workspaceId = null!;
    private RealProcess runner = null!;

    private string Commands => Path.Combine(root, "dbdatasync-commands.txt");

    public async ValueTask InitializeAsync()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("the dbdatasync the Workspace server runs is a shell script here");
        var runnerDll = Programs.Runner;
        var runnerDirectory = Path.Combine(root, "runner");
        Directory.CreateDirectory(runnerDirectory);

        // the engine as the runtime package, installed in the channel before the runner starts
        var runtime = Path.Combine(root, "runtime");
        foreach (var file in Directory.EnumerateFiles(Programs.EngineBuildOutput, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(runtime, "files", "bin", Path.GetRelativePath(Programs.EngineBuildOutput, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        File.WriteAllText(Path.Combine(runtime, "package.yaml"), RuntimeManifest());
        await RealProcess.RunAsync(runnerDll, "package", "install", Path.Combine(runtime, "package.yaml"), Path.Combine(runtime, "files"), "--channel", Channel, "--repo", runnerDirectory);

        // the console, with an administrator
        var consoleDirectory = Path.Combine(root, "console");
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()), Timeout = TimeSpan.FromMinutes(8) };
        personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;
        var instances = console.Services.GetRequiredService<ConsoleInstances>();

        // the runner, paired with the console, which the runner trusts to sign what it deploys
        var code = Programs.CodeIn(await RealProcess.RunAsync(runnerDll, "server", "pair", "--repo", runnerDirectory, "--max-role", "admin"));
        var url = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
        runner = RealProcess.Start(runnerDll, ["server", "run", "--repo", runnerDirectory, "--listen", url.AbsoluteUri.TrimEnd('/')]);
        started.Add(runner);
        await runner.WaitUntilAsync(new Uri(url, "/health"), TimeSpan.FromSeconds(90));
        runnerId = (await instances.PairAsync(url, code, "Runner")).InstanceId;
        var trusted = await console.Services.GetRequiredService<ConsoleTrust>().EnsureAsync(instances.Registry.Find(runnerId)!);
        Assert.True(trusted.Done, trusted.Problem);

        // the Workspace server, whose dbdatasync writes down each command before it runs the real one
        var directory = Path.Combine(root, "workspace");
        Directory.CreateDirectory(directory);
        var tool = Path.Combine(root, "dbdatasync");
        File.WriteAllText(tool, $"#!/bin/bash\necho \"$@\" >> \"{Commands}\"\nexec dotnet \"{Programs.Sync}\" \"$@\"\n", new UTF8Encoding(false));
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        workspace = WorkspaceServer.Build(new WorkspaceSettings(directory, new Uri("https://127.0.0.1:0")) { SyncExecutable = tool }, WorkspaceServer.Certificate(directory), _ => { });
        await workspace.StartAsync();
        var pairing = WorkspaceState.Pairing(directory).Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Author | ServerRole.Approver));
        workspaceId = (await instances.PairAsync(new Uri(workspace.Urls.First()), pairing.Code, "Workspace", default)).InstanceId;
    }

    public async ValueTask DisposeAsync()
    {
        if (workspace is not null) { await workspace.StopAsync(); await workspace.DisposeAsync(); }
        if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
        http?.Dispose();
        foreach (var process in started) await process.DisposeAsync();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a process may still hold a file a moment */ }
    }

    /// <summary>A runtime version in the line of dbdatasync as built, so a set the Workspace builds with dbdatasync's own range runs on it.</summary>
    private static string RuntimeManifest()
    {
        var parts = (System.Diagnostics.FileVersionInfo.GetVersionInfo(Programs.Sync).ProductVersion ?? "").Split('-', '+')[0].Split('.');
        var version = parts.Length >= 2 && parts.Take(2).All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) ? $"{parts[0]}.{parts[1]}.0" : "1.0.0";
        return "schema: package/1\nname: " + RuntimePackage + "\ndescription: \"The dbdatasync engine for the test\"\nruntime: true\nversion: \"" + version + "\"\nplatform: " + (OperatingSystem.IsWindows() ? "win-x64" : "linux-x64") + "\n" +
            "launch: { kind: executable, command: dotnet, args: [\"{runtime}/bin/DbDataSync.TaskRunner.dll\", \"--runner\"] }\nbuilt_against: { dotnet: \"10.0\", kit: \"1\", protocol: dbdatakit.runner.v1 }\n";
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> ConsoleCall(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(personId).Id}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> Workspace(HttpMethod method, string path, JsonNode? body = null) => ConsoleCall(method, $"/relay/{workspaceId}/api/v1{path}", body);

    /// <summary>What the Scripts tab does for **Test**: build the draft, stage the build in <see cref="Channel"/> through the console, run <c>script-test</c> there and wait for its answer.</summary>
    private async Task<JsonNode> TestAsync(string project, string draft, string script)
    {
        var requested = await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces/{draft}/builds");
        Assert.True(requested.Status == HttpStatusCode.Accepted, requested.Body?.ToJsonString());
        var build = (string)requested.Body!["id"]!;
        for (var i = 0; ; i++)
        {
            var read = (await Workspace(HttpMethod.Get, $"/projects/{project}/workspaces/{draft}/builds/{build}")).Body!;
            var status = (string)read["build"]!["status"]!;
            if (status == "succeeded") break;
            Assert.True(status != "failed" && i < 1200, read.ToJsonString());
            await Task.Delay(250);
        }

        // staged, not served, in a channel that is not production; the slots of the set are bound there, as any version staged in a channel needs, although this task opens none of them
        var staged = await ConsoleCall(HttpMethod.Post, "/api/deploys", new JsonObject
        {
            ["workspaceServer"] = workspaceId, ["project"] = project, ["workspace"] = draft, ["build"] = build, ["target"] = runnerId, ["channel"] = Channel, ["serve"] = false,
            ["bind"] = new JsonObject { ["source_db"] = "source-db", ["target_db"] = "target-db" }, ["note"] = $"Test of the script {script}",
        });
        Assert.True(staged.Status == HttpStatusCode.OK, $"{staged.Status} {staged.Body}");
        var deploy = (string)staged.Body!["deploy"]!["id"]!;
        Assert.Contains("script-test", staged.Body["deploy"]!["tasks"]!.AsArray().Select(t => (string?)t));

        var (status, body) = await ConsoleCall(HttpMethod.Post, $"/api/deploys/{deploy}/runs", new JsonObject
        {
            ["task"] = "script-test", ["answer"] = true, ["inputs"] = new JsonObject { ["name"] = script, ["rows"] = "3" },
        });
        var until = DateTime.UtcNow.AddMinutes(4);
        while (status == HttpStatusCode.Accepted && DateTime.UtcNow < until)
        {
            await Task.Delay(500);
            (status, body) = await ConsoleCall(HttpMethod.Get, $"/api/deploys/{deploy}/runs/{(string)body!["run_id"]!}/answer");
        }
        Assert.True(status == HttpStatusCode.OK, $"{status} {body}\n{runner.Output}");
        Assert.Equal("done", (string?)body!["state"]);
        Assert.False((bool)body["production"]!);
        Assert.False(string.IsNullOrEmpty((string?)body["link"]));
        return body;
    }

    [Fact]
    public async Task Test_of_a_script_compiles_and_runs_it_on_the_Runner_and_answers_its_cases_and_the_Workspace_server_starts_no_script()
    {
        const string project = "scripts-sync";
        var made = await Workspace(HttpMethod.Post, "/projects", new JsonObject { ["id"] = project, ["kind"] = "sync" });
        Assert.True(made.Status == HttpStatusCode.Created, made.Body?.ToJsonString());
        var draft = (string)(await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces", new JsonObject { ["name"] = "draft" })).Body!["id"]!;

        // the template's script, as a new project has it
        var answer = (await TestAsync(project, draft, "upper-name"))["answer"]!;
        Assert.True((bool)answer["ok"]!, answer.ToJsonString());
        Assert.Equal(("upper-name", "sqlColumnExpression"), ((string)answer["name"]!, (string)answer["kind"]!));
        Assert.Contains("generated", (string)answer["source"]!);
        Assert.Contains(answer["cases"]!.AsArray(), c => ((string?)c!["output"])?.Contains("UPPER(", StringComparison.Ordinal) == true);

        // a script that does not compile: the compiler's words are the answer, and the run is not a failure
        var put = await Workspace(HttpMethod.Put, $"/projects/{project}/workspaces/{draft}/files/config/scripts/upper-name.csx", new JsonObject { ["content"] = "this is not C#;\nnew X()" });
        Assert.True(put.Status is HttpStatusCode.OK or HttpStatusCode.Created, put.Body?.ToJsonString());
        var broken = (await TestAsync(project, draft, "upper-name"))["answer"]!;
        Assert.False((bool)broken["ok"]!);
        Assert.Contains("did not compile", (string)broken["error"]!);

        // the Workspace server built the draft and ran no script; the route that did answers where a script is tested now
        Assert.Contains(File.ReadAllLines(Commands), c => c.StartsWith("package publish", StringComparison.Ordinal));
        Assert.DoesNotContain(File.ReadAllLines(Commands), c => c.StartsWith("script test", StringComparison.Ordinal));
        var old = await Workspace(HttpMethod.Post, $"/projects/{project}/workspaces/{draft}/script-test", new JsonObject { ["name"] = "upper-name" });
        Assert.Equal(HttpStatusCode.Gone, old.Status);
        Assert.Contains("on a Runner", (string)old.Body!["error"]!);
    }
}
