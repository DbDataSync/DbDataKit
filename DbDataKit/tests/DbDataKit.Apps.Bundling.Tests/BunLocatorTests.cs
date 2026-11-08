using System.Security.Cryptography;
using DbDataKit.Apps.Bundling;

namespace DbDataKit.Apps.Bundling.Tests;

/// <summary>Finding the pinned Bun (plan 26, AP23): nothing here runs Bun, so the tests do not depend on it being installed, and every rule is given its inputs instead of reading the machine.</summary>
public sealed class BunLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bunloc-" + Guid.NewGuid().ToString("N")[..8]);

    public BunLocatorTests() => Directory.CreateDirectory(root);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private string Fake(string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void When_bun_is_missing_the_message_names_the_pin_its_version_and_the_script_that_fetches_it()
    {
        Fake("bun.pin", "VERSION=9.9.9\n");
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        var found = BunLocator.Locate(environment: _ => null, startFolders: [Path.Combine(root, "work")], path: empty, rid: "linux-x64");

        Assert.False(found.Found);
        Assert.Contains("bun.pin", found.Problem);
        Assert.Contains("9.9.9", found.Problem);
        Assert.Contains("scripts/fetch-bun.sh", found.Problem);
        Assert.Contains(BunLocator.EnvironmentVariable, found.Problem);
    }

    [Fact]
    public void A_bun_named_by_the_caller_or_the_environment_comes_first_and_a_missing_one_is_said_so()
    {
        var named = Fake("mine/bun", "x");
        Assert.Equal(named, BunLocator.Locate(named, _ => null, [root], "", "linux-x64").Path);
        Assert.Contains("--bun", BunLocator.Locate(Path.Combine(root, "nope"), _ => null, [root], "", "linux-x64").Problem);
        var viaEnvironment = BunLocator.Locate(null, name => name == BunLocator.EnvironmentVariable ? named : null, [root], "", "linux-x64");
        Assert.Equal(named, viaEnvironment.Path);
        Assert.Equal(BunLocator.EnvironmentVariable, viaEnvironment.Source);
        Assert.Contains(BunLocator.EnvironmentVariable, BunLocator.Locate(null, _ => Path.Combine(root, "nope"), [root], "", "linux-x64").Problem);
    }

    [Fact]
    public void The_fetched_bun_is_found_above_the_working_folder_and_checked_against_the_pin_and_a_changed_byte_is_refused()
    {
        Fake(".bun/linux-x64/bun", "the program");
        Fake("bun.pin", $"VERSION=1.0.0\nBIN_SHA256_linux_x64={Sha("the program")}\n");
        var work = Path.Combine(root, "deep", "work");
        Directory.CreateDirectory(work);

        var found = BunLocator.Locate(null, _ => null, [work], "", "linux-x64");
        Assert.True(found.Found);
        Assert.Contains("bun.pin", found.Source);

        Fake(".bun/linux-x64/bun", "the program, changed");
        var refused = BunLocator.Locate(null, _ => null, [work], "", "linux-x64");
        Assert.False(refused.Found);
        Assert.Contains("does not match", refused.Problem);
        Assert.Contains("scripts/fetch-bun.sh", refused.Problem);
    }

    [Fact]
    public void A_bun_on_the_path_is_the_last_resort_and_is_marked_as_not_checked()
    {
        var onPath = Fake("tools/" + (OperatingSystem.IsWindows() ? "bun.exe" : "bun"), "x");
        var found = BunLocator.Locate(null, _ => null, [Path.Combine(root, "work")], Path.GetDirectoryName(onPath), OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");
        Assert.Equal(onPath, found.Path);
        Assert.Contains("not checked", found.Source);
    }

    [Fact]
    public async Task An_offline_build_installs_nothing_and_refuses_a_source_whose_packages_are_not_already_there()
    {
        var source = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(source, "src"));
        File.WriteAllText(Path.Combine(source, "src", "main.ts"), "export {}");
        File.WriteAllText(Path.Combine(source, "package.json"), "{\"dependencies\":{\"left-pad\":\"1.3.0\"}}");

        // the bundler program does not exist: the refusal comes before anything is started
        var result = await new BunAppBundler(Path.Combine(root, "no-such-bun")).BundleAsync(new AppBundleRequest(source, "src/main.ts", Path.Combine(root, "out"), Offline: true));

        Assert.False(result.Succeeded);
        Assert.Contains("offline", result.Diagnostics[0].Message);
        Assert.Contains("node_modules", result.Diagnostics[0].Message);
    }
}
