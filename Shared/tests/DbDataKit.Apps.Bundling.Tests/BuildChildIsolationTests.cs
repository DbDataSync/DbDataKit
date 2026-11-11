using DbDataKit.Apps.Bundling;

namespace DbDataKit.Apps.Bundling.Tests;

/// <summary>
/// The properties of the app build that the Workspace must keep wherever it runs, the console included (plan 33 SC04): the child process of a build holds an allow-listed environment and nothing of its host's (no key ring, no secret, no
/// <c>DBDATAKIT_*</c> variable) (the no-network property has its tests in <c>VendoredStoreTests</c>). Each test fails when the property is lost.
/// </summary>
public sealed class BuildChildIsolationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "child-" + Guid.NewGuid().ToString("N")[..8]);

    public BuildChildIsolationTests() => Directory.CreateDirectory(Path.Combine(root, "src", "src"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    [Fact]
    public void The_environment_of_a_build_child_is_the_allow_list_and_nothing_else_of_the_host()
    {
        var host = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin", ["HOME"] = "/home/service", ["TMPDIR"] = "/tmp",
            ["DBDATAKIT_DATA"] = "/var/lib/dbdatakit", ["DBDATAKIT_DATA_WORKSPACE"] = "/var/lib/dbdatakit/workspace",
            ["DBDATAKIT_STATE_ENGINE"] = "postgres", ["DBDATAKIT_STATE_CONNECTION"] = "Host=db;Password=secret:state",
            ["DBDATAKIT_KEYRING"] = "/var/lib/dbdatakit/server/secrets/keys", ["DOTNET_DataProtection_KeyRing"] = "/keys",
            ["ASPNETCORE_URLS"] = "https://+:8480", ["ASPNETCORE_Kestrel__Certificates__Default__Password"] = "pfx-password",
            ["SIGNING_KEY"] = "seed", ["SESSION_SECRET"] = "s3cret", ["GITHUB_TOKEN"] = "ghp_x", ["HTTPS_PROXY"] = "http://proxy:3128",
        };

        var child = BunAppBundler.ChildEnvironment(name => host.GetValueOrDefault(name));

        var allowed = BunAppBundler.AllowedVariables.Append("CI").Append("NO_COLOR").ToList();
        Assert.All(child.Keys, name => Assert.True(allowed.Contains(name), name));
        Assert.Equal("/usr/bin", child["PATH"]);
        Assert.DoesNotContain(child.Keys, name => name.StartsWith("DBDATAKIT_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(child.Values, v => v.Contains("secret", StringComparison.OrdinalIgnoreCase) || v.Contains("pfx", StringComparison.Ordinal) || v.Contains("keys", StringComparison.Ordinal) || v.Contains("proxy", StringComparison.Ordinal));
    }

    [Fact]
    public void The_allow_list_names_no_variable_a_suite_service_reads()
    {
        // a name added to the list by mistake would hand a service's configuration to every build
        Assert.DoesNotContain(BunAppBundler.AllowedVariables, name => name.StartsWith("DBDATAKIT_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_real_child_started_by_the_bundler_sees_none_of_the_hosts_variables()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Assert.Skip("the fake bundler is a shell script");
        const string secret = "DBDATAKIT_TEST_SC04_SECRET";
        const string keyRing = "DBDATAKIT_DATA_SC04_KEYRING";
        Environment.SetEnvironmentVariable(secret, "hunter2-sc04");
        Environment.SetEnvironmentVariable(keyRing, "/var/lib/sc04/keys");
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "src", "main.ts"), "console.log(1);\n");
            // a bundler that prints its environment and then fails, so the lines come back as the diagnostics
            var bun = Path.Combine(root, "fake-bun");
            File.WriteAllText(bun, "#!/bin/sh\nenv\nexit 1\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(bun, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await new BunAppBundler(bun).BundleAsync(new AppBundleRequest(Path.Combine(root, "src"), "src/main.ts", Path.Combine(root, "out")));

            Assert.False(result.Succeeded);
            var seen = string.Join('\n', result.Diagnostics.Select(d => d.Message));
            Assert.Contains("CI=1", seen);       // it did print its environment
            Assert.DoesNotContain("DBDATAKIT", seen);
            Assert.DoesNotContain("hunter2-sc04", seen);
            Assert.DoesNotContain("/var/lib/sc04", seen);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable(keyRing, null);
        }
    }
}
