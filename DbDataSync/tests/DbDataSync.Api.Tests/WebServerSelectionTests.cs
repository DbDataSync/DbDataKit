using DbDataSync.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// <c>DbDataSync:App:Server</c> — Kestrel by default, HTTP.sys opt-in and Windows only. See
/// <see cref="WebServerSelection"/> for why an unknown value is refused rather than ignored.
/// </summary>
public sealed class WebServerSelectionTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-webserver-tests-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { [WebServerSelection.ConfigKey] = value })
            .Build();

    [Fact]
    public void Unset_IsKestrel() =>
        Assert.Equal(WebServer.Kestrel, WebServerSelection.Resolve(Config(null)));

    [Theory]
    [InlineData("httpsys", WebServer.HttpSys)]
    [InlineData("HttpSys", WebServer.HttpSys)]
    [InlineData(" HTTPSYS ", WebServer.HttpSys)]
    [InlineData("kestrel", WebServer.Kestrel)]
    [InlineData("", WebServer.Kestrel)]
    public void KnownValues_AreCaseInsensitive(string value, WebServer expected) =>
        Assert.Equal(expected, WebServerSelection.Resolve(Config(value)));

    [Theory]
    [InlineData("http.sys")]
    [InlineData("iis")]
    [InlineData("7")]
    public void UnknownValues_AreRefusedNamingTheKey(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WebServerSelection.Resolve(Config(value)));
        Assert.Contains(WebServerSelection.ConfigKey, ex.Message);
    }

    private string[] BaseArgs(params string[] extra) =>
    [
        "--DbDataSync:App:RepoRoot", _repoRoot,
        "--DbDataSync:State:DbPath", Path.Combine(_repoRoot, "state.db"),
        "--DbDataSync:App:TaskRunnerDllPath", Path.Combine(_repoRoot, "DbDataSync.TaskRunner.dll"),
        .. extra,
    ];

    [NonWindowsFact]
    public void HttpSysOffWindows_FailsAtStartupSayingSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DbDataSyncHost.Build(BaseArgs("--DbDataSync:App:Server", "httpsys")));

        Assert.Contains("only exists on Windows", ex.Message);
    }

    [Fact]
    public void KestrelIsTheDefaultHost()
    {
        using var app = DbDataSyncHost.Build(BaseArgs());

        Assert.Equal(WebServer.Kestrel, app.Services.GetRequiredService<ApiOptions>().Server);
    }
}
