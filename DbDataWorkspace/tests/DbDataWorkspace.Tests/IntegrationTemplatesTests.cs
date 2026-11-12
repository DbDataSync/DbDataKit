using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Authoring.Server;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Packaging;
using DbDataKit.Scripting;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The two integration project templates (plan 27a IX21): each generates a project that builds with the build code of <c>dbdatarunner package build</c> and passes its own tests, which run against recorded responses and a fake share, with no
/// network and no database, and nothing edited. The manifest the template writes agrees with the job it declares (slots, counters, progress).
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class IntegrationTemplatesTests
{
    private static readonly string[] Names = ["rest-api-to-staging", "csv-files-from-share"];

    public static IEnumerable<TheoryDataRow<string>> Templates() => Names.Select(n => new TheoryDataRow<string>(n));

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ddw-template-" + Guid.NewGuid().ToString("N")[..10]);

        public Scratch() => Directory.CreateDirectory(Root);

        public string Project(string template, string id = "my-integration", string? title = "My integration")
        {
            var folder = Path.Combine(Root, "project");
            foreach (var file in new IntegrationProjectKind(new RunnerTool(), new DotnetTool()).Template(id, title, template))
            {
                var target = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, file.Content);
            }

            return folder;
        }

        public PackageBuildOptions Options() => new()
        {
            CacheDirectory = Path.Combine(Root, "cache"),
            Sources = [new PackageSource(Path.Combine(Root, "no-feed"))],
            Offline = true,
            WriteLock = false,
        };

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a temp folder */ }
        }
    }

    [Fact]
    public void The_integration_kind_lists_its_templates_and_the_plain_one_is_still_the_default()
    {
        var kind = new IntegrationProjectKind(new RunnerTool(), new DotnetTool());
        Assert.Equal(Names, kind.Templates.Select(t => t.Name));
        Assert.All(kind.Templates, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Contains(kind.Template("x", null, null), f => f.Path == "src/hello.csx");
        Assert.Throws<ArgumentException>(() => kind.Template("x", null, "made-up"));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task A_template_generates_a_project_that_builds_and_passes_its_own_tests_with_no_edits(string template)
    {
        using var s = new Scratch();
        var project = s.Project(template);
        Assert.True(File.Exists(Path.Combine(project, "package.yaml")));
        Assert.Contains("# My integration", File.ReadAllText(Path.Combine(project, "README.md")));
        Assert.DoesNotContain("{{", string.Join("\n", Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));

        var build = await CsxPackageBuilder.BuildAsync(project, Path.Combine(s.Root, "out"), s.Options());
        Assert.True(build.Succeeded, string.Join("; ", build.Diagnostics));
        Assert.Contains(build.Files!, f => f == "bin/my-integration.dll");
        // the kit's libraries the job stands on travel beside it: the task host carries only the kit
        Assert.Contains(build.Files!, f => f == "bin/DbDataKit.Runner.Kit.Http.dll");
        Assert.Contains(build.Files!, f => f == "bin/DbDataKit.Runner.Kit.Landing.dll");

        var tested = await IntegrationTester.TestAsync(project, s.Options());
        Assert.True(tested.Succeeded, string.Join("; ", tested.Diagnostics.Select(d => d.ToString())) + " " + string.Join("; ", tested.Tests.Where(t => !t.Passed).Select(t => t.File + ": " + t.Message)));
        Assert.NotEmpty(tested.Tests);
        Assert.All(tested.Tests, t => Assert.True(t.Passed, $"{t.File}: {t.Message}"));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task The_manifest_a_template_writes_agrees_with_the_job_it_declares(string template)
    {
        using var s = new Scratch();
        var project = s.Project(template);
        var build = await CsxPackageBuilder.BuildAsync(project, Path.Combine(s.Root, "out"), s.Options());
        Assert.True(build.Succeeded, string.Join("; ", build.Diagnostics));

        var written = PackageManifestReader.Read(File.ReadAllText(Path.Combine(project, "package.yaml")));
        var context = new System.Runtime.Loader.AssemblyLoadContext(null, isCollectible: true);
        var declared = ManifestGenerator.ForAssembly(context.LoadFromAssemblyPath(build.AssemblyPath!), written.Name, written.Launch);
        var task = Assert.Single(written.Tasks);
        var job = Assert.Single(declared.Tasks);
        Assert.Equal(job.Name, task.Name);
        Assert.Equal(job.Entry.Type, task.Entry.Type);
        Assert.Equal(job.Connections.Select(c => (c.Name, c.Kind)), task.Connections.Select(c => (c.Name, c.Kind)));
        Assert.Equal(job.CounterList.Order(StringComparer.Ordinal), task.CounterList.Order(StringComparer.Ordinal));
        Assert.Equal(job.Progress.States.Select(p => p.Name), task.Progress.States.Select(p => p.Name));
        context.Unload();
    }

    [Fact]
    public async Task A_new_project_is_made_from_a_template_through_the_api_and_an_unknown_template_is_refused()
    {
        await using var server = await WorkspaceHarness.StartAsync();

        var kinds = (await server.AsAdmin(HttpMethod.Get, "/api/v1/project-kinds")).Body!["kinds"]!.AsArray();
        var templates = kinds.Single(k => (string)k!["name"]! == "integration")!["templates"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
        Assert.Equal(Names, templates);
        Assert.Empty(kinds.Single(k => (string)k!["name"]! == "app")!["templates"]!.AsArray());

        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = "pull", ["kind"] = "integration", ["title"] = "Pull", ["template"] = "csv-files-from-share" }.ToJsonString())).Response.StatusCode);
        var workspace = (string)(await server.AsAda(HttpMethod.Post, "/api/v1/projects/pull/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var files = (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/pull/workspaces/{workspace}/files")).Body!["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
        Assert.Contains("src/pickup.csx", files);
        Assert.Contains("tests/pickup.csx", files);
        Assert.DoesNotContain("src/hello.csx", files);

        var refused = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = "other", ["kind"] = "integration", ["template"] = "made-up" }.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, refused.Response.StatusCode);
        Assert.Contains("made-up", refused.Body!.ToJsonString());
        var wrongKind = await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = "third", ["kind"] = "app", ["template"] = "csv-files-from-share" }.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.Response.StatusCode);
    }
}
