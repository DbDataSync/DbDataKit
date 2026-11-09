using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.Apps.Bundling;

namespace DbDataKit.Apps.Bundling.Tests;

/// <summary>
/// Plan 22a GP33 (QG12): offline builds from a vendored package store and the administrator's allow-list. The store is a small one made here (the library <c>lib</c>, the allowable <c>foo</c> and <c>@scope/bar</c>, and <c>hidden</c>,
/// which is in the store only as something <c>lib</c> needs), so the tests need no network and no Node. The ones that run Bun need it on the path (they skip without it, or fail with <c>DBDATA_REQUIRE_BUN=true</c>);
/// the ones that need a network namespace skip where the machine cannot make one (or fail with <c>DBDATA_REQUIRE_NETNS=true</c>).
/// </summary>
public sealed class VendoredStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vendored-" + Guid.NewGuid().ToString("N")[..8]);
    private string Store => Path.Combine(root, "store");
    private string Source => Path.Combine(root, "src");
    private string Output => Path.Combine(root, "out");

    public VendoredStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "src"));
        Package("lib", "1.0.0", "export const greeting = (name) => `Hello, ${name}`;\n");
        Package("foo", "2.0.0", "export const answer = 42;\n");
        Package("@scope/bar", "3.0.0", "export const bar = 'bar';\n");
        Package("hidden", "4.0.0", "export const secret = 's';\n");
        WriteManifest(["lib"]);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private readonly Dictionary<string, string> packages = new();

    private void Package(string name, string version, string code)
    {
        var directory = Path.Combine(Store, "node_modules", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), $"{{\"name\":\"{name}\",\"version\":\"{version}\",\"type\":\"module\",\"main\":\"index.js\"}}\n");
        File.WriteAllText(Path.Combine(directory, "index.js"), code);
        packages[name] = version;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void WriteManifest(string[] library, int schemaVersion = 1)
    {
        var files = Directory.EnumerateFiles(Path.Combine(Store, "node_modules"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Store, f).Replace('\\', '/'), Sha);
        var manifest = new
        {
            schemaVersion,
            library,
            packages = packages.ToDictionary(p => "node_modules/" + p.Key, p => new { name = p.Key, version = p.Value }),
            files,
        };
        File.WriteAllText(Path.Combine(Store, "store.json"), JsonSerializer.Serialize(manifest));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static bool BunAvailable()
    {
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bun", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            probe!.WaitForExit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static void NeedBun()
    {
        if (BunAvailable()) return;
        Assert.False(string.Equals(Environment.GetEnvironmentVariable("DBDATA_REQUIRE_BUN"), "true", StringComparison.OrdinalIgnoreCase), "bun is required but not on the path");
        Assert.Skip("bun is not on the path");
    }

    private static void NeedNamespace()
    {
        if (NetworkIsolator.IsAvailable) return;
        Assert.False(string.Equals(Environment.GetEnvironmentVariable("DBDATA_REQUIRE_NETNS"), "true", StringComparison.OrdinalIgnoreCase), "a network namespace is required but " + NetworkIsolator.WhyNot);
        Assert.Skip(NetworkIsolator.WhyNot);
    }

    private VendoredPackageStore Open() => VendoredPackageStore.Load(Store);

    // ---- the store ----

    [Fact]
    public void A_store_loads_with_its_packages_and_library_and_agrees_with_its_files()
    {
        var store = Open();
        Assert.Equal(["lib"], store.Library);
        Assert.Equal(["@scope/bar", "foo", "hidden", "lib"], store.Packages.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(8, store.FileCount);
        Assert.Empty(store.Verify());
    }

    [Fact]
    public void A_changed_vendored_file_is_refused_by_hash_naming_the_file()
    {
        var store = Open();
        File.WriteAllText(Path.Combine(Store, "node_modules", "lib", "index.js"), "export const greeting = () => fetch('https://evil.example');\n");

        var problems = store.Verify();

        var problem = Assert.Single(problems);
        Assert.Contains("node_modules/lib/index.js", problem);
        Assert.Contains("changed", problem);
    }

    [Fact]
    public void An_added_a_missing_and_a_linked_file_are_refused_too()
    {
        var store = Open();
        File.WriteAllText(Path.Combine(Store, "node_modules", "foo", "extra.js"), "x");
        File.Delete(Path.Combine(Store, "node_modules", "hidden", "index.js"));
        try { File.CreateSymbolicLink(Path.Combine(Store, "node_modules", "foo", "link.js"), Path.Combine(Store, "node_modules", "foo", "index.js")); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        var problems = store.Verify();

        Assert.Contains(problems, p => p.Contains("node_modules/foo/extra.js") && p.Contains("not in store.json"));
        Assert.Contains(problems, p => p.Contains("node_modules/hidden/index.js") && p.Contains("missing"));
        if (File.Exists(Path.Combine(Store, "node_modules", "foo", "link.js")) && new FileInfo(Path.Combine(Store, "node_modules", "foo", "link.js")).LinkTarget is not null)
            Assert.Contains(problems, p => p.Contains("node_modules/foo/link.js") && p.Contains("link"));
    }

    [Fact]
    public void A_manifest_that_does_not_read_is_refused_naming_the_file()
    {
        var path = Path.Combine(Store, "store.json");
        Assert.Contains("store.json", Assert.Throws<VendoredStoreException>(() => VendoredPackageStore.Load(Path.Combine(root, "nowhere"))).Message + "store.json");
        Assert.Contains(Path.Combine(root, "src", "store.json"), Assert.Throws<VendoredStoreException>(() => VendoredPackageStore.Load(Path.Combine(root, "src"))).Message);

        File.WriteAllText(path, "{ not json");
        Assert.Contains(path, Assert.Throws<VendoredStoreException>(() => VendoredPackageStore.Load(Store)).Message);

        WriteManifest(["lib"], schemaVersion: 2);
        var newer = Assert.Throws<VendoredStoreException>(() => VendoredPackageStore.Load(Store));
        Assert.Contains(path, newer.Message);
        Assert.Contains("schemaVersion is 2", newer.Message);

        WriteManifest(["nope"]);
        Assert.Contains("nope", Assert.Throws<VendoredStoreException>(() => VendoredPackageStore.Load(Store)).Message);

        WriteManifest(["lib"]);
        var text = File.ReadAllText(path).Replace("node_modules/lib/index.js", "node_modules/../escape.js");
        File.WriteAllText(path, text);
        Assert.Contains("not a path under node_modules", Assert.Throws<VendoredStoreException>(() => VendoredPackageStore.Load(Store)).Message);
    }

    [Fact]
    public void The_digest_of_the_manifest_can_be_pinned_by_the_administrator()
    {
        var digest = Open().Digest;
        Assert.Equal(digest, VendoredPackageStore.Load(Store, digest.ToUpperInvariant()).Digest);
        var wrong = Assert.Throws<VendoredStoreException>(() => VendoredPackageStore.Load(Store, new string('0', 64)));
        Assert.Contains("apps.packageStoreSha256", wrong.Message);
        Assert.Contains(digest, wrong.Message);
    }

    // ---- the allow-list ----

    [Fact]
    public void A_source_that_imports_a_package_that_is_not_listed_is_refused_naming_the_package_and_the_line()
    {
        Write("src/main.ts", "import { greeting } from 'lib';\n\nimport _ from 'lodash';\nconsole.log(_, greeting);\n");

        var violations = AppPackagePolicy.Check(Source, Open(), null);

        var violation = Assert.Single(violations);
        Assert.Equal("lodash", violation.Package);
        Assert.Equal("src/main.ts", violation.File);
        Assert.Equal(3, violation.Line);
        Assert.Contains("imports 'lodash'", violation.Message);
        Assert.Contains("apps.allowedPackages", violation.Message);
    }

    [Fact]
    public void A_package_the_store_holds_only_for_another_is_not_importable_until_it_is_allowed()
    {
        Write("src/main.ts", "import { secret } from 'hidden';\nimport { answer } from 'foo/index.js';\nimport { bar } from '@scope/bar';\nconsole.log(secret, answer, bar);\n");

        Assert.Equal(["hidden", "foo", "@scope/bar"], AppPackagePolicy.Check(Source, Open(), []).Select(v => v.Package));
        Assert.Equal(["hidden"], AppPackagePolicy.Check(Source, Open(), ["foo", "@scope/bar"]).Select(v => v.Package));
        Assert.Empty(AppPackagePolicy.Check(Source, Open(), ["foo", "@scope/bar", "hidden"]));
    }

    [Fact]
    public void A_package_on_the_allow_list_that_the_store_does_not_hold_is_refused_and_says_so()
    {
        Write("src/main.ts", "import ghost from 'ghost';\n");

        var violation = Assert.Single(AppPackagePolicy.Check(Source, Open(), ["ghost"]));

        Assert.Contains("on the allow-list", violation.Message);
        Assert.Contains("not in the vendored package store", violation.Message);
    }

    [Fact]
    public void Every_way_to_import_is_read_and_comments_and_relative_imports_are_not_packages()
    {
        Write("src/main.ts", string.Join('\n',
            "// import a from 'commented';", " * import b from 'starred';",
            "import './local';", "import type { T } from './local';", "export { x } from '@scope/other/sub/path';",
            "const lazy = () => import('dynamic-package');", "const old = require('required-package');", "import side from \"side-effect\";"));
        Write("src/local.ts", "export type T = string; export const x = 1;\n");
        Write("src/style.css", "@import 'css-package/theme.css';\n@import url(\"./local.css\");\n");

        var names = AppPackagePolicy.Check(Source, Open(), null).Select(v => v.Package).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["@scope/other", "css-package", "dynamic-package", "required-package", "side-effect"], names);
    }

    [Fact]
    public void Paths_out_of_the_source_addresses_and_node_modules_by_path_are_refused()
    {
        Write("src/main.ts", string.Join('\n', "import a from '../../outside';", "import b from '/etc/passwd';", "import c from 'https://cdn.example.com/x.js';", "import d from '../node_modules/lib/index.js';", "import e from './inside';"));
        Write("src/inside.ts", "export default 1;\n");

        var messages = AppPackagePolicy.Check(Source, Open(), null).Select(v => v.Message).ToList();

        Assert.Equal(4, messages.Count);
        Assert.Contains(messages, m => m.Contains("'../../outside'") && m.Contains("outside the app's source folder"));
        Assert.Contains(messages, m => m.Contains("'/etc/passwd'") && m.Contains("outside"));
        Assert.Contains(messages, m => m.Contains("https://cdn.example.com/x.js") && m.Contains("network"));
        Assert.Contains(messages, m => m.Contains("node_modules by path"));
    }

    [Fact]
    public void The_dependencies_of_package_json_are_held_to_the_same_list_but_dev_dependencies_install_nothing()
    {
        Write("package.json", "{\"dependencies\":{\"lib\":\"*\",\"left-pad\":\"1\"},\"optionalDependencies\":{\"fsevents\":\"2\"},\"devDependencies\":{\"typescript\":\"6\"}}");

        var violations = AppPackagePolicy.Check(Source, Open(), null);

        Assert.Equal(["left-pad", "fsevents"], violations.Select(v => v.Package));
        Assert.All(violations, v => Assert.Equal("package.json", v.File));
    }

    // ---- the build ----

    [Fact]
    public async Task A_source_importing_a_package_that_is_not_listed_is_not_built_and_the_refusal_names_it()
    {
        Write("src/main.ts", "import _ from 'lodash';\nconsole.log(_);\n");

        // Bun is never started: the bundler is given a program that does not exist
        var result = await new BunAppBundler("no-such-bun").BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output, PackageStore: Open()));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("imports 'lodash'") && d.Message.Contains("src/main.ts:1"));
        Assert.Empty(Directory.EnumerateFiles(Output));
    }

    [Fact]
    public async Task A_store_with_a_changed_file_is_not_built_from_and_the_refusal_names_the_file()
    {
        Write("src/main.ts", "import { greeting } from 'lib';\nconsole.log(greeting('x'));\n");
        var store = Open();
        File.AppendAllText(Path.Combine(Store, "node_modules", "lib", "index.js"), "// changed\n");

        var result = await new BunAppBundler("no-such-bun").BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output, PackageStore: store));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("node_modules/lib/index.js") && d.Message.Contains("changed"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("nothing was built"));
    }

    [Fact]
    public async Task The_sources_own_node_modules_and_registry_settings_are_ignored_so_nothing_installs()
    {
        Write("node_modules/evil/index.js", "export const evil = 1;\n");
        Write(".npmrc", "registry=https://registry.example.invalid/\n");
        Write("bunfig.toml", "[install]\nregistry = \"https://registry.example.invalid/\"\n");
        Write("src/main.ts", "import { evil } from 'evil';\nconsole.log(evil);\n");

        // `evil` is not in the store: refused although the source has a folder of that name, and also when the allow-list names it
        foreach (var allowed in new string[]?[] { null, ["evil"] })
        {
            var result = await new BunAppBundler("no-such-bun").BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output, PackageStore: Open(), AllowedPackages: allowed));
            Assert.False(result.Succeeded);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("imports 'evil'"));
        }
    }

    [Fact]
    public async Task An_allowed_package_builds_from_the_store_with_the_network_blocked()
    {
        NeedBun();
        NeedNamespace();
        Write("package.json", "{\"dependencies\":{\"lib\":\"*\",\"foo\":\"*\"}}");
        Write("src/main.ts", "import { greeting } from 'lib';\nimport { answer } from 'foo';\ndocument.title = greeting('apps') + answer;\n");

        var result = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output, PackageStore: Open(), AllowedPackages: ["foo"], Isolation: NetworkIsolation.Required));

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var script = File.ReadAllText(Path.Combine(Output, "main.js"));
        Assert.Contains("Hello, ", script);
        Assert.Contains("42", script);
        Assert.Contains(result.Notes!, n => n.Contains("network namespace with no route"));
        Assert.Contains(result.Notes!, n => n.Contains("nothing was installed"));
        // the store and the source are as they were
        Assert.Empty(Open().Verify());
        Assert.False(Directory.Exists(Path.Combine(Source, "node_modules")));
    }

    [Fact]
    public async Task Without_isolation_the_build_still_works_and_says_it_was_not_isolated()
    {
        NeedBun();
        Write("src/main.ts", "import { greeting } from 'lib';\ndocument.title = greeting('apps');\n");

        var result = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output, PackageStore: Open(), Isolation: NetworkIsolation.None));

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(result.Notes!, n => n.Contains("without network isolation"));
    }

    [Fact]
    public async Task A_generated_file_is_in_the_bundle_and_never_written_to_the_source_folder_with_or_without_a_store()
    {
        NeedBun();
        Write("src/main.ts", "import views from './views.generated.json';\nimport { greeting } from 'lib';\ndocument.title = greeting(views.title);\n");
        var generated = new Dictionary<string, byte[]> { ["src/views.generated.json"] = Encoding.UTF8.GetBytes("{\"title\":\"Generated title\"}") };

        var withStore = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output, PackageStore: Open(), GeneratedFiles: generated));
        Assert.True(withStore.Succeeded, string.Join("; ", withStore.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Generated title", File.ReadAllText(Path.Combine(Output, "main.js")));

        // without a store the source's own node_modules is what resolves `lib` (a copy of it is in the build's working folder)
        Directory.CreateSymbolicLink(Path.Combine(Source, "node_modules"), Path.Combine(Store, "node_modules"));
        Write("package.json", "{\"dependencies\":{\"lib\":\"*\"}}");
        var legacy = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Path.Combine(root, "out2"), Offline: true, GeneratedFiles: generated));
        Assert.True(legacy.Succeeded, string.Join("; ", legacy.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Generated title", File.ReadAllText(Path.Combine(root, "out2", "main.js")));

        Assert.False(File.Exists(Path.Combine(Source, "src", "views.generated.json")));
        // a generated path that leaves the source folder is refused, not written
        var outside = await Assert.ThrowsAsync<ArgumentException>(() => new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Path.Combine(root, "out3"), PackageStore: Open(), GeneratedFiles: new Dictionary<string, byte[]> { ["../escape.json"] = [1] })));
        Assert.Contains("outside the app's source folder", outside.Message);
        Assert.False(File.Exists(Path.Combine(root, "escape.json")));
    }

    // ---- the namespace ----

    [Fact]
    public async Task A_process_in_the_namespace_has_no_route_and_cannot_reach_a_service_on_the_machine()
    {
        NeedNamespace();
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/bash")) Assert.Skip("needs bash on Linux");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // outside the namespace the service answers (this is what the check measures against) ...
        var outside = await Run("/bin/bash", ["-c", $"exec 3<>/dev/tcp/127.0.0.1/{port}"]);
        Assert.Equal(0, outside.Exit);
        // ... inside it the same connection is refused, and the routing table holds no route at all
        var (file, args) = NetworkIsolator.Wrap("/bin/bash", ["-c", $"exec 3<>/dev/tcp/127.0.0.1/{port}"])!.Value;
        Assert.NotEqual(0, (await Run(file, args)).Exit);
        var (routeFile, routeArgs) = NetworkIsolator.Wrap("/bin/cat", ["/proc/net/route"])!.Value;
        var routes = await Run(routeFile, routeArgs);
        Assert.Equal(0, routes.Exit);
        Assert.True(routes.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 1, routes.Output);   // at most the header: no route, not even a default one
    }

    private static async Task<(int Exit, string Output)> Run(string file, IReadOnlyList<string> arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = new StringBuilder(await process.StandardOutput.ReadToEndAsync());
        output.Append(await process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        return (process.ExitCode, output.ToString());
    }
}
