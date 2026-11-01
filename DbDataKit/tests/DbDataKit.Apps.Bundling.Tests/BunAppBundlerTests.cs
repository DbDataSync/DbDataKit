using System.Diagnostics;
using DbDataKit.Apps.Bundling;

namespace DbDataKit.Apps.Bundling.Tests;

/// <summary>The Bun bundler on small real sources. Needs <c>bun</c> on the path; without it the tests that build skip (set <c>DBDATA_REQUIRE_BUN=true</c> to fail them instead).</summary>
public sealed class BunAppBundlerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bundling-" + Guid.NewGuid().ToString("N")[..8]);
    private string Source => Path.Combine(root, "src");
    private string Output => Path.Combine(root, "out");

    public BunAppBundlerTests() => Directory.CreateDirectory(Path.Combine(Source, "src"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static void NeedBun()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("bun", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            probe!.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("DBDATA_REQUIRE_BUN"), "true", StringComparison.OrdinalIgnoreCase), "bun is required but not on the path");
            Assert.Skip("bun is not on the path");
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task TypeScript_with_an_import_becomes_one_browser_bundle()
    {
        NeedBun();
        Write("src/greeting.ts", "export const greeting = (name: string): string => `Hello, ${name}`;\n");
        Write("src/main.ts", "import { greeting } from './greeting';\ndocument.title = greeting('apps');\n");

        var result = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output));

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("main.js", result.Files);
        Assert.Contains("Hello, ", File.ReadAllText(Path.Combine(Output, "main.js")));
        Assert.True(result.TotalBytes > 0);
    }

    [Fact]
    public async Task A_source_the_bundler_refuses_fails_with_its_words_and_leaves_no_bundle()
    {
        NeedBun();
        Write("src/main.ts", "import { nothing } from './missing';\nconsole.log(nothing);\n");

        var result = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task A_bundle_over_the_size_limit_is_a_failure()
    {
        NeedBun();
        Write("src/main.ts", "console.log('a'.repeat(100));\n");

        var result = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output, MaxBundleBytes: 10));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("allowed"));
    }

    [Fact]
    public async Task An_entry_point_outside_the_source_a_missing_one_and_a_missing_bundler_are_refused_before_anything_runs()
    {
        Write("src/main.ts", "console.log(1);\n");

        var outside = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "../outside.ts", Output));
        Assert.Contains(outside.Diagnostics, d => d.Message.Contains("outside"));

        var missing = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/none.ts", Output));
        Assert.Contains(missing.Diagnostics, d => d.Message.Contains("does not exist"));

        var inside = await new BunAppBundler().BundleAsync(new AppBundleRequest(Source, "src/main.ts", Source));
        Assert.Contains(inside.Diagnostics, d => d.Message.Contains("output folder"));

        var absent = await new BunAppBundler("definitely-not-a-bundler").BundleAsync(new AppBundleRequest(Source, "src/main.ts", Output));
        Assert.Contains(absent.Diagnostics, d => d.Message.Contains("could not be started"));
    }
}
