using System.Diagnostics;

namespace DbDataGateway.Tests.Support;

/// <summary>
/// The vendored package store of the app build (plan 22a GP32, <c>DbDataKit/web/app-store/build.mjs</c>) for tests that build the app template with the suite's real libraries. It is made once per test run from the web workspace
/// (<c>npm ci</c> in <c>DbDataKit/web</c> first) with Node, or taken from <c>DBDATAKIT_TEST_APP_STORE</c> when that names a built one. Without Node or the workspace's packages the tests skip; with
/// <c>DBDATAKIT_TEST_REQUIRE_APP_STORE=true</c> (CI) they fail instead.
/// </summary>
internal static class AppStoreSupport
{
    private static readonly Lazy<(string? Folder, string? Problem)> Store = new(Make);

    /// <summary>The repository's root: the folder above this assembly that holds <c>bun.pin</c>.</summary>
    public static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        // above this source file (a build with --artifacts-path puts the assembly outside the repository), the test assembly or the working directory
        foreach (var start in new[] { Path.GetDirectoryName(thisFile) ?? "", AppContext.BaseDirectory, Directory.GetCurrentDirectory() }.Where(Directory.Exists))
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "bun.pin"))) return directory.FullName;
        throw new InvalidOperationException("bun.pin was not found above " + AppContext.BaseDirectory + " or the working directory");
    }

    /// <summary>The built store's folder (holding <c>store.json</c> and <c>node_modules</c>); skips the test when it cannot be had.</summary>
    public static string Require()
    {
        var (folder, problem) = Store.Value;
        if (folder is not null) return folder;
        Assert.False(string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_APP_STORE"), "true", StringComparison.OrdinalIgnoreCase), "the app store is required but could not be made: " + problem);
        Assert.Skip("no app store: " + problem);
        return "";
    }

    private static (string?, string?) Make()
    {
        if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_APP_STORE") is { Length: > 0 } given)
            return File.Exists(Path.Combine(given, "store.json")) ? (given, null) : (null, $"DBDATAKIT_TEST_APP_STORE={given} holds no store.json");
        var web = Path.Combine(RepoRoot(), "DbDataKit", "web");
        if (!Directory.Exists(Path.Combine(web, "node_modules", "react"))) return (null, $"{web} has no node_modules (run npm ci there)");
        var folder = Path.Combine(Path.GetTempPath(), "dbdata-app-store-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var node = Process.Start(new ProcessStartInfo("node", ["app-store/build.mjs", "--out", folder]) { WorkingDirectory = web, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = node.StandardOutput.ReadToEnd() + node.StandardError.ReadToEnd();
            node.WaitForExit();
            return node.ExitCode == 0 ? (folder, null) : (null, "app-store/build.mjs failed: " + output);
        }
        catch (System.ComponentModel.Win32Exception) { return (null, "node is not on the path"); }
    }
}
