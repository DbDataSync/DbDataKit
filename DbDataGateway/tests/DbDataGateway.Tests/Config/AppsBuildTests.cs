using System.Text;
using DbDataKit.Apps;
using DbDataKit.Apps.Bundling;
using DbDataKit.Cli;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 26, AP23: <c>apps init</c> and <c>apps build</c>. The build tests give the command a bundler of their own (it writes what Bun would, a script named after the entry), so no result depends on Bun being installed; one test
/// runs the real Bun when it is there and skips (or fails with <c>DBDATA_REQUIRE_BUN=true</c>) when it is not.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsBuildTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dbgateway-appsbuild-" + Guid.NewGuid().ToString("N")[..8]);

    public AppsBuildTests()
    {
        Directory.CreateDirectory(root);
        var config = GatewayConfigFile.DirectoryIn(DataDirectory);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(root);
    }

    private string DataDirectory => Path.Combine(root, "data");

    private string Folder(string name) => Path.Combine(root, name);

    private static async Task<(int Exit, string Out, string Error)> RunAsync(Func<string[], CliContext, Task<int>> verb, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await verb(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<(int Exit, string Out, string Error)> CommandAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.Router().RunAsync([.. args, "--repo", DataDirectory], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Writes what Bun writes for the entry: one script named after it, with the source's text (so a test can put an address in it).</summary>
    private sealed class WritingBundler : IAppBundler
    {
        public AppBundleRequest? Seen { get; private set; }

        public Task<AppBundleResult> BundleAsync(AppBundleRequest request, CancellationToken cancellationToken = default)
        {
            Seen = request;
            Directory.CreateDirectory(request.OutputDirectory);
            var name = Path.GetFileNameWithoutExtension(request.EntryPoint) + ".js";
            File.WriteAllText(Path.Combine(request.OutputDirectory, name), File.ReadAllText(Path.Combine(request.SourceDirectory, request.EntryPoint)));
            return Task.FromResult(new AppBundleResult(true, [name], 1, [], TimeSpan.Zero));
        }
    }

    [Theory]
    [InlineData("territory-notes")]
    [InlineData("front-portal")]
    [InlineData("territory-views")]
    public async Task A_folder_made_by_init_validates_and_publishes_with_no_build(string template)
    {
        var app = Folder("init-" + template);
        var made = await RunAsync(AppsBuild.InitAsync, "--template", template, "--dir", app);
        Assert.Equal(0, made.Exit);
        Assert.Contains("app.yaml", made.Out);

        var validated = await CommandAsync("apps", "validate", "--dir", app);
        Assert.Equal(0, validated.Exit);
        Assert.Contains("the files reach no other host", validated.Out);
        Assert.Equal(0, (await CommandAsync("apps", "publish", "--dir", app)).Exit);
        Assert.Contains(template, (await CommandAsync("apps", "list")).Out);
    }

    [Fact]
    public async Task Init_names_the_app_as_asked_refuses_a_folder_with_files_a_bad_name_and_an_unknown_template()
    {
        var app = Folder("named");
        var made = await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes", "--dir", app, "--name", "sales-notes");
        Assert.Equal(0, made.Exit);
        var manifest = File.ReadAllText(Path.Combine(app, "app.yaml"));
        Assert.Contains("name: sales-notes", manifest);
        Assert.Contains("title: Sales notes", manifest);
        Assert.Equal(0, (await CommandAsync("apps", "validate", "--dir", app)).Exit);

        var again = await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes", "--dir", app);
        Assert.Equal(1, again.Exit);
        Assert.Contains("is not empty", again.Error);

        var badName = await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes", "--dir", Folder("bad"), "--name", "Bad Name");
        Assert.Equal(2, badName.Exit);
        Assert.False(Directory.Exists(Folder("bad")));

        var unknown = await RunAsync(AppsBuild.InitAsync, "--template", "nope", "--dir", Folder("unknown"));
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("territory-notes-src", unknown.Error);
        Assert.Contains("territory-views", unknown.Error);
        Assert.Contains("territory-views", (await RunAsync(AppsBuild.InitAsync)).Error);   // `apps init` alone lists every template
    }

    [Fact]
    public async Task A_sample_source_tree_builds_into_a_folder_that_validates_and_publishes()
    {
        var source = Folder("tree");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes-src", "--dir", source)).Exit);
        File.WriteAllText(Path.Combine(source, "package.json"), "{}");
        File.WriteAllText(Path.Combine(source, "tsconfig.json"), "{}");
        var bundler = new WritingBundler();
        var built = Folder("built");

        var result = await RunBuild(bundler, "--src", source, "--out", built);

        Assert.Equal(0, result.Exit);
        Assert.Equal("src/main.ts", bundler.Seen!.EntryPoint);
        Assert.Contains("Built territory-notes-src 1.0.0", result.Out);
        // the app's files that are not source, the manifest, and the bundle; no source, no tooling
        var files = Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(built, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["app.css", "app.yaml", "index.html", "main.js"], files);

        Assert.Equal(0, (await CommandAsync("apps", "validate", "--dir", built)).Exit);
        Assert.Equal(0, (await CommandAsync("apps", "publish", "--dir", built)).Exit);
        Assert.Contains("territory-notes-src", (await CommandAsync("apps", "list")).Out);

        // building again over its own output is allowed; a folder that is not a build is not touched
        Assert.Equal(0, (await RunBuild(new WritingBundler(), "--src", source, "--out", built)).Exit);
        Directory.CreateDirectory(Folder("precious"));
        File.WriteAllText(Path.Combine(Folder("precious"), "notes.txt"), "keep");
        var refused = await RunBuild(new WritingBundler(), "--src", source, "--out", Folder("precious"));
        Assert.Equal(1, refused.Exit);
        Assert.Contains("does not look like an earlier build", refused.Error);
        Assert.True(File.Exists(Path.Combine(Folder("precious"), "notes.txt")));
        var inside = await RunBuild(new WritingBundler(), "--src", source, "--out", source);
        Assert.Equal(1, inside.Exit);
    }

    [Fact]
    public async Task An_app_with_no_script_entry_is_copied_as_it_is_and_needs_no_bundler()
    {
        var source = Folder("portal");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "front-portal", "--dir", source)).Exit);
        var built = Folder("portal-built");

        // no bundler is given and Bun is not looked for: there is nothing to bundle
        var result = await RunAsync((a, c) => AppsBuild.BuildAsync(a, c, null, _ => throw new InvalidOperationException("Bun must not be looked for")), "--src", source, "--out", built);

        Assert.Equal(0, result.Exit);
        Assert.Contains("nothing is bundled", result.Out);
        Assert.True(File.Exists(Path.Combine(built, "views", "home.yaml")));
        Assert.Equal(0, (await CommandAsync("apps", "validate", "--dir", built)).Exit);
    }

    [Fact]
    public async Task A_bad_tree_gives_a_diagnostic_with_the_file_and_the_line()
    {
        // 1. a manifest the loader refuses: file, line and column of the key
        var source = Folder("bad-manifest");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes-src", "--dir", source)).Exit);
        File.AppendAllText(Path.Combine(source, "app.yaml"), "colour: red\n");
        var manifest = await RunBuild(new WritingBundler(), "--src", source, "--out", Folder("bad-manifest-out"));
        Assert.Equal(1, manifest.Exit);
        Assert.Matches(@"app\.yaml:\d+:\d+", manifest.Error);
        Assert.False(Directory.Exists(Folder("bad-manifest-out")));

        // 2. a script that reaches another host: the built-in check on the result names the built file and the line
        var reaching = Folder("reaching");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes-src", "--dir", reaching)).Exit);
        File.WriteAllText(Path.Combine(reaching, "src", "main.ts"), "const a = 1\nfetch('https://tracker.example.net/ping')\n");
        var outside = await RunBuild(new WritingBundler(), "--src", reaching, "--out", Folder("reaching-out"));
        Assert.Equal(0, outside.Exit);                                    // advice, as `apps validate` gives it: the host's policy is what enforces
        Assert.Contains("main.js:2", outside.Out);
        Assert.Contains("tracker.example.net", outside.Out);

        // 3. a file the manifest names that the build does not have: an error naming it
        var missing = Folder("missing");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes-src", "--dir", missing)).Exit);
        File.Delete(Path.Combine(missing, "index.html"));
        var gone = await RunBuild(new WritingBundler(), "--src", missing, "--out", Folder("missing-out"));
        Assert.Equal(1, gone.Exit);
        Assert.Contains("index.html", gone.Error);
        Assert.Matches(@"app\.yaml:\d+", gone.Error);

        // 4. an entry that is not there: the bundler's own message, with Bun never started
        var noEntry = await RunAsync((a, c) => AppsBuild.BuildAsync(a, c, new BunAppBundler(Path.Combine(root, "no-such-bun")), null), "--src", reaching, "--out", Folder("noentry-out"), "--entry", "src/missing.ts");
        Assert.Equal(1, noEntry.Exit);
        Assert.Contains("src/missing.ts does not exist", noEntry.Error);
    }

    [Fact]
    public async Task When_bun_is_missing_the_message_names_the_pin_and_the_script_and_nothing_is_built()
    {
        var source = Folder("needs-bun");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes-src", "--dir", source)).Exit);
        // the finder is given empty inputs, so the result does not depend on this machine's Bun
        var empty = Folder("empty-path");
        Directory.CreateDirectory(empty);
        BunLocation None(string? explicitPath) => BunLocator.Locate(explicitPath, _ => null, [Folder("nowhere")], empty, "linux-x64");

        var result = await RunAsync((a, c) => AppsBuild.BuildAsync(a, c, null, None), "--src", source, "--out", Folder("needs-bun-out"));

        Assert.Equal(1, result.Exit);
        Assert.Contains("bun.pin", result.Error);
        Assert.Contains("scripts/fetch-bun.sh", result.Error);
        Assert.Contains("--bun", result.Error);
        Assert.False(Directory.Exists(Folder("needs-bun-out")));
    }

    [Fact]
    public async Task An_offline_build_is_passed_to_the_bundler_and_refuses_packages_that_are_not_installed()
    {
        var source = Folder("offline");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes-src", "--dir", source)).Exit);
        var bundler = new WritingBundler();
        Assert.Equal(0, (await RunBuild(bundler, "--src", source, "--out", Folder("offline-out"), "--offline", "--no-minify")).Exit);
        Assert.True(bundler.Seen!.Offline);
        Assert.False(bundler.Seen.Minify);

        File.WriteAllText(Path.Combine(source, "package.json"), "{\"dependencies\":{\"left-pad\":\"1.3.0\"}}");
        var refused = await RunAsync((a, c) => AppsBuild.BuildAsync(a, c, new BunAppBundler(Path.Combine(root, "no-such-bun")), null), "--src", source, "--out", Folder("offline-out2"), "--offline");
        Assert.Equal(1, refused.Exit);
        Assert.Contains("offline", refused.Error);
        Assert.Contains("node_modules", refused.Error);
    }

    [Fact]
    public async Task With_the_real_bun_the_sample_source_tree_builds_validates_and_publishes()
    {
        var bun = BunLocator.Locate();
        if (!bun.Found)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("DBDATA_REQUIRE_BUN"), "true", StringComparison.OrdinalIgnoreCase), "bun is required but was not found: " + bun.Problem);
            Assert.Skip("bun is not installed");
        }
        var source = Folder("real");
        Assert.Equal(0, (await RunAsync(AppsBuild.InitAsync, "--template", "territory-notes-src", "--dir", source)).Exit);
        var built = Folder("real-built");

        var result = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", built, "--bun", bun.Path!);

        Assert.True(result.Exit == 0, result.Error + result.Out);
        Assert.True(File.Exists(Path.Combine(built, "main.js")));
        // the page loads it with a plain script tag, so the bundle must not carry module syntax
        Assert.DoesNotContain("import ", File.ReadAllText(Path.Combine(built, "main.js")).Replace("import(", ""));
        Assert.Equal(0, (await CommandAsync("apps", "validate", "--dir", built)).Exit);
        Assert.Equal(0, (await CommandAsync("apps", "publish", "--dir", built)).Exit);

        // a TypeScript syntax error: Bun's own message, with the file and line
        File.WriteAllText(Path.Combine(source, "src", "main.ts"), "const a = 1\nconst b = (\n");
        var broken = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", Folder("real-broken"), "--bun", bun.Path!);
        Assert.Equal(1, broken.Exit);
        Assert.Contains("main.ts", broken.Error);
    }

    private static BunLocation NeedBun()
    {
        var bun = BunLocator.Locate();
        if (bun.Found) return bun;
        Assert.False(string.Equals(Environment.GetEnvironmentVariable("DBDATA_REQUIRE_BUN"), "true", StringComparison.OrdinalIgnoreCase), "bun is required but was not found: " + bun.Problem);
        Assert.Skip("bun is not installed");
        return bun;
    }

    [Fact]
    public async Task The_app_template_builds_through_the_bundler_from_the_vendored_store_with_the_default_library_set_and_validates()
    {
        var bun = NeedBun();
        var store = AppStoreSupport.Require();
        var source = Folder("template");
        var made = await RunAsync(AppsBuild.NewAsync, "--dir", source, "--name", "sales-app");
        Assert.True(made.Exit == 0, made.Error);
        Assert.Contains("name: sales-app", File.ReadAllText(Path.Combine(source, "app.yaml")));

        // 1. through IAppBundler, the way the apps host and the Workspace server build: the packages are the store's, nothing is installed
        var bundled = await new BunAppBundler(bun.Path!).BundleAsync(new AppBundleRequest(source, "src/main.tsx", Folder("template-bundle"), PackageStore: VendoredPackageStore.Load(store)));
        Assert.True(bundled.Succeeded, string.Join("; ", bundled.Diagnostics.Select(d => d.Message)));
        Assert.Contains("main.js", bundled.Files);
        Assert.Contains("main.css", bundled.Files);
        // a plain script in a sandboxed document: no module syntax
        Assert.DoesNotContain("\nexport ", File.ReadAllText(Path.Combine(Folder("template-bundle"), "main.js")));
        Assert.False(Directory.Exists(Path.Combine(source, "node_modules")));

        // 2. through `apps build`, which then runs the checks `apps validate` and `apps publish` make (the network namespace where the machine can make one)
        var built = Folder("template-built");
        var result = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", built, "--store", store, "--bun", bun.Path!, "--isolation", NetworkIsolator.IsAvailable ? "required" : "auto");
        Assert.True(result.Exit == 0, result.Error + result.Out);
        Assert.Contains("nothing was installed", result.Out);
        var files = Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(built, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["app.yaml", "index.html", "main.css", "main.js"], files);
        Assert.Equal(0, (await CommandAsync("apps", "validate", "--dir", built)).Exit);
    }

    [Fact]
    public async Task A_build_from_the_store_refuses_a_package_that_is_not_listed_naming_it_and_builds_it_once_the_allow_list_has_it()
    {
        var bun = NeedBun();
        var store = AppStoreSupport.Require();
        var source = Folder("allow");
        Assert.Equal(0, (await RunAsync(AppsBuild.NewAsync, "--dir", source)).Exit);
        // `yaml` is in the store (the visuals need it) but is not in the default library set
        File.WriteAllText(Path.Combine(source, "src", "main.tsx"), File.ReadAllText(Path.Combine(source, "src", "main.tsx")) + "\nimport { parse } from 'yaml'\nconsole.log(parse('a: 1'))\n");

        var refused = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", Folder("allow-out"), "--store", store, "--bun", bun.Path!);
        Assert.Equal(1, refused.Exit);
        Assert.Contains("imports 'yaml'", refused.Error);
        Assert.Contains("src/main.tsx", refused.Error);
        Assert.Contains("apps.allowedPackages", refused.Error);
        Assert.False(Directory.Exists(Folder("allow-out")));

        var allowed = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", Folder("allow-out"), "--store", store, "--allow", "yaml", "--bun", bun.Path!);
        Assert.True(allowed.Exit == 0, allowed.Error + allowed.Out);
        Assert.Contains("a: 1".Length > 0 ? "main.js" : "", string.Join(' ', Directory.EnumerateFiles(Folder("allow-out")).Select(Path.GetFileName)));

        // the same list from the Gateway's settings (`apps.allowedPackages`, `apps.packageStore`), read from the data directory
        File.WriteAllText(Path.Combine(GatewayConfigFile.DirectoryIn(DataDirectory), "server.yaml"), $"schemaVersion: 2\ntls: {{ mode: selfSigned }}\napps:\n  packageStore: '{store.Replace("'", "''")}'\n  allowedPackages: [yaml]\n");
        var fromSettings = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", Folder("settings-out"), "--repo", DataDirectory, "--bun", bun.Path!);
        Assert.True(fromSettings.Exit == 0, fromSettings.Error + fromSettings.Out);
        File.WriteAllText(Path.Combine(GatewayConfigFile.DirectoryIn(DataDirectory), "server.yaml"), $"schemaVersion: 2\ntls: {{ mode: selfSigned }}\napps:\n  packageStore: '{store.Replace("'", "''")}'\n");
        var notListed = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", Folder("settings-out2"), "--repo", DataDirectory, "--bun", bun.Path!);
        Assert.Equal(1, notListed.Exit);
        Assert.Contains("imports 'yaml'", notListed.Error);

        // a store manifest that is not the one the administrator pinned (`apps.packageStoreSha256`) is refused before anything is read from the store
        File.WriteAllText(Path.Combine(GatewayConfigFile.DirectoryIn(DataDirectory), "server.yaml"), $"schemaVersion: 2\ntls: {{ mode: selfSigned }}\napps:\n  packageStore: '{store.Replace("'", "''")}'\n  packageStoreSha256: '{new string('0', 64)}'\n");
        var pinned = await RunAsync(AppsBuild.BuildAsync, "--src", source, "--out", Folder("settings-out3"), "--repo", DataDirectory, "--bun", bun.Path!);
        Assert.Equal(1, pinned.Exit);
        Assert.Contains("apps.packageStoreSha256", pinned.Error);
    }

    [Fact]
    public async Task A_changed_vendored_file_stops_the_build_and_store_verify_names_it()
    {
        var store = AppStoreSupport.Require();
        // a copy of the store, so the shared one stays whole
        var copy = Folder("store-copy");
        foreach (var file in Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(store, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var ok = await RunAsync(AppsBuild.StoreVerifyAsync, "--store", copy);
        Assert.True(ok.Exit == 0, ok.Error);
        Assert.Contains("An app may import: @dbdatakit/semantic-client", ok.Out);
        Assert.Matches("Digest of store.json .*: [0-9a-f]{64}", ok.Out);

        var changed = Path.Combine(copy, "node_modules", "react", "index.js");
        File.AppendAllText(changed, "\n// changed\n");
        var source = Folder("changed");
        Assert.Equal(0, (await RunAsync(AppsBuild.NewAsync, "--dir", source)).Exit);
        var verify = await RunAsync(AppsBuild.StoreVerifyAsync, "--store", copy);
        Assert.Equal(1, verify.Exit);
        Assert.Contains("node_modules/react/index.js", verify.Error);
        var build = await RunAsync((a, c) => AppsBuild.BuildAsync(a, c, new BunAppBundler("no-such-bun"), null), "--src", source, "--out", Folder("changed-out"), "--store", copy);
        Assert.Equal(1, build.Exit);
        Assert.Contains("node_modules/react/index.js", build.Error);
        Assert.Contains("nothing was built", build.Error);

        // a store folder that is not there is refused naming it
        var wrong = await RunAsync((a, c) => AppsBuild.BuildAsync(a, c, new BunAppBundler("no-such-bun"), null), "--src", source, "--out", Folder("pinned-out"), "--store", Folder("nowhere"));
        Assert.Equal(1, wrong.Exit);
        Assert.Contains("does not exist", wrong.Error);
    }

    [Fact]
    public async Task New_makes_the_app_template_and_refuses_what_init_refuses()
    {
        var app = Folder("new-app");
        var made = await RunAsync(AppsBuild.NewAsync, "--dir", app);
        Assert.Equal(0, made.Exit);
        Assert.True(File.Exists(Path.Combine(app, "src", "main.tsx")));
        Assert.Contains("apps build --src", made.Out);
        Assert.Contains("--dir <new folder>", (await RunAsync(AppsBuild.NewAsync)).Error);
        Assert.Equal(2, (await RunAsync(AppsBuild.NewAsync, "--dir", Folder("x"), "--name", "Bad Name")).Exit);
        Assert.Equal(1, (await RunAsync(AppsBuild.NewAsync, "--dir", app)).Exit);   // not empty
        Assert.Equal(2, (await RunAsync(AppsBuild.NewAsync, "--dir", Folder("y"), "--template", "territory-notes")).Exit);   // `new` has no template option
        Assert.Equal(0, (await CommandAsync("apps", "validate", "--dir", app)).Exit);
    }

    private static Task<(int Exit, string Out, string Error)> RunBuild(IAppBundler bundler, params string[] args) =>
        RunAsync((a, c) => AppsBuild.BuildAsync(a, c, bundler, _ => throw new InvalidOperationException("Bun must not be looked for")), args);
}
