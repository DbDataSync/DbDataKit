using DbDataKit.Authoring.Server;
using System.Net;
using System.Text.Json.Nodes;
using DbDataGateway.Tests.Support;
using DbDataKit.Apps.Bundling;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>
/// Builds and previews on the Workspace server (plan 22a GP35b1 and the preview of GP35b2): an app project starts as the template, a build of a draft runs from the vendored package store with the allow-list and the isolation the settings say,
/// is a row of the <c>build</c> table, keeps its result (with the file and line of a refused import) and is previewed read only. Bun and the store are the real ones (the tests skip without them; CI requires them).
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceBuildTests
{
    private const string Orders = """{"id":"orders","title":"Orders"}""";

    private static WorkspaceAppSettings Real(NetworkIsolation isolation = NetworkIsolation.IfAvailable, int seconds = 120, string? bun = null, IReadOnlyList<string>? allowed = null) =>
        new(AppStoreSupport.Require(), allowed, null, isolation, seconds, bun ?? NeedBun());

    private static string NeedBun()
    {
        var found = BunLocator.Locate();
        Assert.False(!found.Found && string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_BUN"), "true", StringComparison.OrdinalIgnoreCase), "Bun is required but was not found: " + found.Problem);
        if (!found.Found) Assert.Skip("bun is not installed");
        return found.Path!;
    }

    private static async Task<(string Project, string Workspace)> DraftAsync(WorkspaceHarness server)
    {
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", Orders)).Response.StatusCode);
        var id = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/orders/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        return ("orders", id);
    }

    private static async Task<JsonNode> FinishedAsync(WorkspaceHarness server, string workspace, string build)
    {
        for (var i = 0; i < 600; i++)
        {
            var read = await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{workspace}/builds/{build}");
            if ((string)read.Body!["build"]!["status"]! is "succeeded" or "failed" or "canceled") return read.Body!;
            await Task.Delay(200);
        }
        throw new TimeoutException("the build did not finish");
    }

    [Fact]
    public async Task An_app_project_starts_as_the_template_named_for_the_project()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var (_, workspace) = await DraftAsync(server);

        var files = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{workspace}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.Contains("app.yaml", files);
        Assert.Contains("package.json", files);
        Assert.Contains("src/main.tsx", files);
        Assert.Contains("views/dashboard.yaml", files);
        var manifest = (string)(await server.AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{workspace}/files/app.yaml")).Body!["content"]!;
        Assert.Contains("name: orders", manifest);
        Assert.Contains("title: \"Orders\"", manifest);
        Assert.True(DbDataKit.Apps.AppManifestLoader.Load(manifest).Ok);
    }

    [Fact]
    public async Task A_server_without_a_package_store_says_so_in_the_screens_words_and_refuses_to_build()
    {
        await using var server = await WorkspaceHarness.StartAsync(apps: new WorkspaceAppSettings());
        var (_, workspace) = await DraftAsync(server);

        var status = (await server.AsAda(HttpMethod.Get, "/api/v1/app-build")).Body!;
        Assert.False((bool)status["ready"]!);
        Assert.Contains("apps.packageStore", string.Join(" ", status["problems"]!.AsArray().Select(p => (string)p!)));
        Assert.Contains("app-store/build.mjs", string.Join(" ", status["problems"]!.AsArray().Select(p => (string)p!)));

        var refused = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{workspace}/builds");
        Assert.Equal(HttpStatusCode.Conflict, refused.Response.StatusCode);
        Assert.Contains("No package store is set", (string)refused.Body!["error"]!);
        Assert.Empty(server.Store().Builds("orders"));                       // nothing was asked for, so no row
        Assert.Contains(server.Audit(), a => a.Command == "build.request" && a.Outcome.StartsWith("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_store_folder_that_does_not_exist_on_this_machine_is_named()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-store-" + Guid.NewGuid().ToString("N")[..6]);
        await using var server = await WorkspaceHarness.StartAsync(apps: new WorkspaceAppSettings(missing));

        var status = (await server.AsAda(HttpMethod.Get, "/api/v1/app-build")).Body!;
        Assert.False((bool)status["ready"]!);
        Assert.Contains(missing, string.Join(" ", status["problems"]!.AsArray().Select(p => (string)p!)));
        Assert.Contains("does not exist on this machine", string.Join(" ", status["problems"]!.AsArray().Select(p => (string)p!)));
    }

    [Fact]
    public async Task The_settings_come_from_server_yaml_and_a_key_that_is_not_one_is_refused_naming_the_line()
    {
        var directory = Directory.CreateTempSubdirectory("ddw-yaml-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "server.yaml"), "apps:\n  packageStore: /srv/store\n  allowedPackages: [left-pad, '@scope/x']\n  isolation: auto\n  buildTimeoutSeconds: 90\n");
            var read = WorkspaceAppSettings.Load(directory);
            Assert.Equal(("/srv/store", NetworkIsolation.IfAvailable, 90), (read.PackageStore, read.Isolation, read.BuildTimeoutSeconds));
            Assert.Equal(["left-pad", "@scope/x"], read.Allowed);
            Assert.Equal(NetworkIsolation.Required, WorkspaceAppSettings.Load(Path.Combine(directory, "none")).Isolation);       // the default is the strict one

            File.WriteAllText(Path.Combine(directory, "server.yaml"), "apps:\n  packageStore: /srv/store\n  isolaton: auto\n");
            var wrong = Assert.Throws<InvalidOperationException>(() => WorkspaceAppSettings.Load(directory));
            Assert.Contains("line 3", wrong.Message);
            Assert.Contains("apps.isolaton is not a setting", wrong.Message);
            File.WriteAllText(Path.Combine(directory, "server.yaml"), "apps:\n  isolation: sometimes\n");
            Assert.Contains("required or auto", Assert.Throws<InvalidOperationException>(() => WorkspaceAppSettings.Load(directory)).Message);
        }
        finally { Directory.Delete(directory, true); }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_build_of_the_template_runs_from_the_store_is_recorded_and_its_preview_is_read_only()
    {
        var settings = Real(NetworkIsolator.IsAvailable ? NetworkIsolation.Required : NetworkIsolation.IfAvailable);
        await using var server = await WorkspaceHarness.StartAsync(apps: settings);
        var (_, workspace) = await DraftAsync(server);
        var root = $"/api/v1/projects/orders/workspaces/{workspace}";

        Assert.True((bool)(await server.AsAda(HttpMethod.Get, "/api/v1/app-build")).Body!["ready"]!);
        var asked = await server.AsAda(HttpMethod.Post, root + "/builds");
        Assert.Equal(HttpStatusCode.Accepted, asked.Response.StatusCode);
        Assert.Equal("queued", (string)asked.Body!["status"]!);
        var id = (string)asked.Body["id"]!;

        var done = await FinishedAsync(server, workspace, id);
        var result = done["result"]!;
        Assert.True("succeeded" == (string)done["build"]!["status"]!, result.ToJsonString());
        Assert.Equal("orders", (string)result["app"]!);
        Assert.Contains("main.js", result["bundled"]!.AsArray().Select(f => (string)f!));
        Assert.True((int)result["files"]! > 3);
        Assert.False((bool)result["uncommitted_changes"]!);                       // a draft nobody has edited
        Assert.NotEqual("", (string)done["build"]!["commit"]!);
        Assert.Contains(result["notes"]!.AsArray(), n => ((string)n!).Contains("nothing was installed", StringComparison.Ordinal));
        // the isolation is said either way: in a network namespace, or (where the machine cannot make one and the setting allows it) that it was not
        Assert.Equal(NetworkIsolator.IsAvailable, (bool)result["isolated"]!);

        // the table has it, from queued through running to succeeded, with who asked
        var row = Assert.Single(server.Store().Builds("orders", workspace));
        Assert.Equal(("succeeded", "ada"), (row.Status, row.RequestedBy));
        Assert.NotNull(row.StartedAt);
        Assert.NotNull(row.FinishedAt);
        Assert.Contains("Built orders", row.Message);
        var listed = (await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, root + "/builds")).Body!["builds"]!.AsArray();
        Assert.Equal([id], listed.Select(b => (string)b!["id"]!));

        // the preview: a page with its script and style in it, read only, with no network for it
        var page = await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, $"{root}/builds/{id}/preview/");
        Assert.Equal(HttpStatusCode.OK, page.Response.StatusCode);
        var html = await server.Http.SendAsync(WithToken(server, "vic", ServerRole.Viewer, HttpMethod.Get, $"{root}/builds/{id}/preview"));
        var text = await html.Content.ReadAsStringAsync();
        Assert.Contains("<script>", text);
        Assert.DoesNotContain("src=\"main.js\"", text);
        Assert.DoesNotContain("href=\"main.css\"", text);
        var policy = string.Join(' ', html.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("connect-src 'none'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("sandbox allow-scripts", policy);
        Assert.DoesNotContain("allow-same-origin", policy);
        Assert.Equal("nosniff", html.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await server.Http.SendAsync(WithToken(server, "vic", ServerRole.Viewer, HttpMethod.Get, $"{root}/builds/{id}/preview/main.js"))).StatusCode);

        // it cannot write: only GET is a preview route, and a path cannot leave the build
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete, HttpMethod.Patch })
            Assert.True((await server.Send("root", ServerRole.Admin, method, $"{root}/builds/{id}/preview/main.js", "{}")).Response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, $"{root}/builds/{id}/preview/nothing.js")).Response.StatusCode);
        Assert.True((await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, $"{root}/builds/{id}/preview/%2e%2e/result.json")).Response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, $"{root}/builds/{Guid.NewGuid()}/preview")).Response.StatusCode);

        // the roles: a viewer reads builds and previews but does not ask for one
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Post, root + "/builds")).Response.StatusCode);
        Assert.Contains(server.Audit(), a => a.Command == "build.request" && a.Outcome == "ok" && a.Target!.StartsWith("orders/" + workspace, StringComparison.Ordinal));
    }

    private static HttpRequestMessage WithToken(WorkspaceHarness server, string user, ServerRole role, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, server.Token(user, role));
        return request;
    }

    [Fact]
    public async Task A_refused_import_is_reported_with_its_file_and_line_and_the_build_is_failed()
    {
        await using var server = await WorkspaceHarness.StartAsync(apps: Real(NetworkIsolator.IsAvailable ? NetworkIsolation.Required : NetworkIsolation.IfAvailable));
        var (_, workspace) = await DraftAsync(server);
        var root = $"/api/v1/projects/orders/workspaces/{workspace}";
        var main = (string)(await server.AsAda(HttpMethod.Get, root + "/files/src/main.tsx")).Body!["content"]!;
        await server.AsAda(HttpMethod.Put, root + "/files/src/main.tsx", new JsonObject { ["content"] = "import leftPad from 'left-pad'\n" + main }.ToJsonString());

        var id = (string)(await server.AsAda(HttpMethod.Post, root + "/builds")).Body!["id"]!;
        var done = await FinishedAsync(server, workspace, id);

        Assert.Equal("failed", (string)done["build"]!["status"]!);
        var found = done["result"]!["diagnostics"]!.AsArray().Select(d => d!).FirstOrDefault(d => (string?)d["message"] is { } m && m.Contains("left-pad", StringComparison.Ordinal));
        Assert.NotNull(found);
        Assert.Equal(("src/main.tsx", 1), ((string)found["file"]!, (int)found["line"]!));
        Assert.Contains("not allowed", string.Join(" ", done["result"]!["diagnostics"]!.AsArray().Select(d => (string)d!["message"]!)) + (string)done["build"]!["message"]!, StringComparison.OrdinalIgnoreCase);
        Assert.True((bool)done["result"]!["uncommitted_changes"]!);
        var row = Assert.Single(server.Store().Builds("orders", workspace));
        Assert.Equal("failed", row.Status);
        // there is nothing to preview or publish
        Assert.Equal(HttpStatusCode.Conflict, (await server.Send("vic", ServerRole.Viewer, HttpMethod.Get, $"{root}/builds/{id}/preview")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await server.Send("grace", ServerRole.Approver, HttpMethod.Post, $"{root}/builds/{id}/publish", """{"target":"edge"}""")).Response.StatusCode);

        // the administrator's allow-list is what lets it build: the package must be in the store too, so a name the store lacks is named by the check
        await using var allowing = await WorkspaceHarness.StartAsync(apps: Real(NetworkIsolator.IsAvailable ? NetworkIsolation.Required : NetworkIsolation.IfAvailable, allowed: ["left-pad"]));
        var (_, again) = await DraftAsync(allowing);
        await allowing.AsAda(HttpMethod.Put, $"/api/v1/projects/orders/workspaces/{again}/files/src/main.tsx", new JsonObject { ["content"] = "import leftPad from 'left-pad'\n" + main }.ToJsonString());
        var second = (string)(await allowing.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{again}/builds")).Body!["id"]!;
        var secondDone = await FinishedAsync(allowing, again, second);
        Assert.Equal("failed", (string)secondDone["build"]!["status"]!);
        Assert.Contains("allow", string.Join(" ", secondDone["result"]!["diagnostics"]!.AsArray().Select(d => (string)d!["message"]!)));     // listed, and not in the store
    }

    [Fact]
    public async Task A_machine_that_cannot_isolate_refuses_a_required_build_and_with_auto_builds_and_says_it_was_not_isolated()
    {
        if (NetworkIsolator.IsAvailable) Assert.Skip("this machine can make a network namespace, so the fallback is not reached here (CI sets DBDATAKIT_TEST_REQUIRE_NETNS)");
        var strict = Real(NetworkIsolation.Required);
        await using (var server = await WorkspaceHarness.StartAsync(apps: strict))
        {
            var status = (await server.AsAda(HttpMethod.Get, "/api/v1/app-build")).Body!;
            Assert.False((bool)status["ready"]!);
            Assert.Contains("network namespace", string.Join(" ", status["problems"]!.AsArray().Select(p => (string)p!)));
        }
        await using var relaxed = await WorkspaceHarness.StartAsync(apps: Real(NetworkIsolation.IfAvailable));
        var (_, workspace) = await DraftAsync(relaxed);
        var id = (string)(await relaxed.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{workspace}/builds")).Body!["id"]!;
        var done = await FinishedAsync(relaxed, workspace, id);
        Assert.Equal("succeeded", (string)done["build"]!["status"]!);
        Assert.False((bool)done["result"]!["isolated"]!);
        Assert.Contains(done["result"]!["notes"]!.AsArray(), n => ((string)n!).Contains("without network isolation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_build_that_runs_past_its_time_is_stopped_and_recorded_as_failed()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("a shell script stands in for Bun");
        var fake = Path.Combine(Directory.CreateTempSubdirectory("ddw-bun-").FullName, "bun");
        File.WriteAllText(fake, "#!/bin/sh\nsleep 60\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var server = await WorkspaceHarness.StartAsync(apps: new WorkspaceAppSettings(AppStoreSupport.Require(), null, null, NetworkIsolation.IfAvailable, 1, fake));
        var (_, workspace) = await DraftAsync(server);

        var id = (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{workspace}/builds")).Body!["id"]!;
        var done = await FinishedAsync(server, workspace, id);

        Assert.Equal("failed", (string)done["build"]!["status"]!);
        Assert.Contains("longer than 1 seconds", (string)done["build"]!["message"]!);
        Assert.True((double)done["result"]!["seconds"]! < 30);
        Directory.Delete(Path.GetDirectoryName(fake)!, true);
    }

    [Fact]
    public async Task A_build_that_was_waiting_when_the_server_stopped_is_failed_when_it_starts_again()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        var (_, workspace) = await DraftAsync(server);
        var store = server.Store();
        var queued = store.RequestBuild("orders", workspace, "ada");
        var running = store.RequestBuild("orders", workspace, "ada");
        store.SetBuildStatus(running.Id, "running");

        using var restarted = new WorkspaceBuilds(server.Directory, store, new DbDataKit.Authoring.Workspaces(server.Directory, TimeProvider.System), () => new WorkspaceAppSettings(), _ => { });

        Assert.All(store.Builds("orders", workspace), b => Assert.Equal("failed", b.Status));
        Assert.Contains("server stopped", store.Builds("orders", workspace).First(b => b.Id == queued.Id).Message);
    }

    [Fact]
    public void A_diagnostic_line_is_split_into_file_line_and_reason_when_it_names_a_place()
    {
        var located = WorkspaceBuilds.Diagnostic("  src/main.tsx:12:5: imports 'lodash', which is not in the default library set");
        Assert.Equal(("src/main.tsx", 12, "imports 'lodash', which is not in the default library set"), ((string)located["file"]!, (int)located["line"]!, (string)located["message"]!));
        var plain = WorkspaceBuilds.Diagnostic("The package store does not match its store.json; nothing was built.");
        Assert.Null(plain["file"]);
        Assert.Contains("nothing was built", (string)plain["message"]!);
    }
}
