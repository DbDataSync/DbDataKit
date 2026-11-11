using DbDataKit.Views.Export;

namespace DbDataKit.Views.Export.Tests;

/// <summary>
/// Where the tests find a browser. In the job that fetched the pin, <c>DBDATAKIT_TEST_CHROMIUM</c> names <c>.chromium/&lt;rid&gt;</c> and <c>DBDATAKIT_TEST_REQUIRE_CHROMIUM=true</c> makes a missing browser a failure; anywhere else
/// (a runner whose kernel forbids user namespaces sets <c>DBDATAKIT_TEST_NOSANDBOX=true</c>), the tests skip when no browser is found, and in a cloud session a Playwright build (<c>/opt/pw-browsers/chromium-*</c>) stands in through <c>mode: system</c> (it is not the pinned build, and nothing here compares with goldens made on it).
/// </summary>
internal static class TestBrowser
{
    public static bool Required => string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_CHROMIUM"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The executable and whether it is the pinned build.</summary>
    public static (string Path, bool Pinned)? Find()
    {
        if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_CHROMIUM") is { Length: > 0 } dir)
        {
            var pin = ChromiumPin.Find(AppContext.BaseDirectory);
            var rid = ExportSettings.PinRid();
            if (pin is not null && rid is not null && ChromiumPin.Read(pin).Executable is { } exe && File.Exists(exe)) return (exe, true);
            foreach (var name in new[] { "chrome-linux64/chrome", "chrome-win64/chrome.exe", "chrome", "chrome.exe" })
                if (File.Exists(Path.Combine(dir, name))) return (Path.Combine(dir, name), false);
        }
        if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_CHROME_SYSTEM") is { Length: > 0 } system && File.Exists(system)) return (system, false);
        if (Directory.Exists("/opt/pw-browsers"))
            foreach (var d in Directory.EnumerateDirectories("/opt/pw-browsers", "chromium-*").Order(StringComparer.Ordinal))
                if (File.Exists(Path.Combine(d, "chrome-linux64", "chrome"))) return (Path.Combine(d, "chrome-linux64", "chrome"), false);
        return null;
    }

    /// <summary>Settings for the browser found, or a skip (a failure in the pinned job).</summary>
    public static ExportSettings Settings(Func<ExportSettings, ExportSettings>? change = null)
    {
        var found = Find();
        if (found is null)
        {
            if (Required) Assert.Fail("DBDATAKIT_TEST_REQUIRE_CHROMIUM is set and no browser was found (run scripts/fetch-chromium and set DBDATAKIT_TEST_CHROMIUM).");
            Assert.Skip("No Chromium found (set DBDATAKIT_TEST_CHROMIUM, or DBDATAKIT_TEST_CHROME_SYSTEM for a system build); the exporter tests were not run.");
        }
        var settings = new ExportSettings
        {
            Mode = found!.Value.Pinned ? BrowserMode.Pinned : BrowserMode.System,
            Path = found.Value.Path,
            NoSandbox = Environment.IsPrivilegedProcess || string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_NOSANDBOX"), "true", StringComparison.OrdinalIgnoreCase),
            Deadline = TimeSpan.FromSeconds(60),
            FontsDirectory = FontsDirectory(),
            FontsPin = FontsPin(),
        };
        return change?.Invoke(settings) ?? settings;
    }

    public static string? FontsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".fonts", "noto-sans"))) return Path.Combine(dir.FullName, ".fonts");
        return null;
    }

    public static string? FontsPin()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "fonts.pin"))) return Path.Combine(dir.FullName, "fonts.pin");
        return null;
    }
}
