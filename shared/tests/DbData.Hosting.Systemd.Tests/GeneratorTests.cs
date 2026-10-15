using System.Runtime.CompilerServices;

namespace DbData.Hosting.Systemd.Tests;

public class GeneratorTests
{
    private static string SourceDir([CallerFilePath] string? path = null) => Path.GetDirectoryName(path)!;

    /// <summary>Compare to Golden/name. Set UPDATE_GOLDEN=1 to rewrite the expected file from the source tree.</summary>
    private static void Golden(string name, string actual)
    {
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(SourceDir(), "Golden", name), actual);
        }

        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name)).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }

    private static SystemdUnitOptions Dynamic() => new()
    {
        Name = "dbdataproxy",
        Description = "DbDataProxy",
        ExecStart = "/opt/dbdataproxy/current/DbDataProxy.Server --urls https://0.0.0.0:8443",
        WorkingDirectory = "/opt/dbdataproxy/current",
        StateDirectories = ["dbdataproxy"],
        ConfigurationDirectories = ["dbdataproxy"],
        LogsDirectories = ["dbdataproxy"],
        LoadCredentials = new Dictionary<string, string> { ["pfx-password"] = "/etc/dbdataproxy/secrets/pfx-password", ["api-key"] = "/etc/dbdataproxy/secrets/api-key" },
        Environment = new Dictionary<string, string> { ["DOTNET_ENVIRONMENT"] = "Production" },
        WatchdogSec = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public void Unit_dynamic_user_golden() => Golden("unit-dynamic.service", SystemdUnitGenerator.Generate(Dynamic()));

    [Fact]
    public void Unit_dedicated_user_golden() =>
        Golden("unit-dedicated.service", SystemdUnitGenerator.Generate(new SystemdUnitOptions
        {
            Name = "dbdatasync",
            Description = "DbDataSync",
            ExecStart = "/opt/dbdatasync/current/dbdatasync serve",
            DynamicUser = false,
            User = "dbdatasync",
            Type = "simple",
            ReadWritePaths = ["/var/lib/dbdatasync", "/srv/sync-data"],
            Capabilities = ["CAP_NET_BIND_SERVICE"],
        }));

    [Fact]
    public void Unit_always_carries_the_hardening_baseline()
    {
        var text = SystemdUnitGenerator.Generate(Dynamic());
        foreach (var line in new[] { "NoNewPrivileges=yes", "ProtectSystem=strict", "ProtectHome=yes", "PrivateTmp=yes", "Restart=on-failure", "DynamicUser=yes", "WatchdogSec=30", "LoadCredential=api-key:/etc/dbdataproxy/secrets/api-key" })
        {
            Assert.Contains(line + "\n", text);
        }

        Assert.DoesNotContain("User=root", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void Unit_refuses_injection_and_relative_paths()
    {
        Assert.Throws<ArgumentException>(() => SystemdUnitGenerator.Generate(Dynamic().WithDescription("x\nExecStartPre=/bin/evil")));
        Assert.Throws<ArgumentException>(() => SystemdUnitGenerator.Generate(new SystemdUnitOptions { Name = "a", ExecStart = "relative/bin" }));
        Assert.Throws<ArgumentException>(() => SystemdUnitGenerator.Generate(new SystemdUnitOptions { Name = "a", ExecStart = "/bin/a", DynamicUser = false }));
        Assert.Throws<ArgumentException>(() => SystemdUnitGenerator.Generate(new SystemdUnitOptions { Name = "a", ExecStart = "/bin/a", ReadWritePaths = ["data"] }));
        Assert.Throws<ArgumentException>(() => SystemdUnitGenerator.Generate(new SystemdUnitOptions { Name = "a", ExecStart = "/bin/a", StateDirectories = ["../etc"] }));
    }

    [Fact]
    public void Dockerfile_runtime_golden() =>
        Golden("runtime.Dockerfile", DockerfileGenerator.Generate(new DockerfileOptions { EntryAssembly = "DbDataProxy.Server.dll" }));

    [Fact]
    public void Dockerfile_dev_pinned_chiseled_golden() =>
        Golden("dev.Dockerfile", DockerfileGenerator.Generate(new DockerfileOptions
        {
            EntryAssembly = "DbDataSync.Api.dll",
            Flavor = ImageFlavor.Dev,
            BaseImage = "mcr.microsoft.com/dotnet/sdk:10.0@sha256:" + new string('a', 64),
            Port = 5080,
            HasShell = false,
        }));

    [Fact]
    public void Dockerfile_is_never_root_and_has_healthcheck()
    {
        var text = DockerfileGenerator.Generate(new DockerfileOptions { EntryAssembly = "A.dll" });
        Assert.Contains("USER 1654:1654", text);
        Assert.Contains("HEALTHCHECK", text);
        Assert.DoesNotContain("sdk", text);
        Assert.Throws<ArgumentException>(() => DockerfileGenerator.Generate(new DockerfileOptions { EntryAssembly = "A.dll", Uid = 0 }));
        Assert.Throws<ArgumentException>(() => DockerfileGenerator.Generate(new DockerfileOptions { EntryAssembly = "A.dll", BaseImage = "evil image" }));
        Assert.Throws<ArgumentException>(() => DockerfileGenerator.Generate(new DockerfileOptions { EntryAssembly = "A\".dll" }));
    }
}

internal static class OptionsExtensions
{
    public static SystemdUnitOptions WithDescription(this SystemdUnitOptions o, string description) => new()
    {
        Name = o.Name,
        ExecStart = o.ExecStart,
        Description = description,
    };
}
