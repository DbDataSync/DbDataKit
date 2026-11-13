using DbDataGateway.Server.Export;
using DbDataGateway.Tests.Support;
using DbDataKit.Views.Export;

namespace DbDataGateway.Tests.Hosting;

/// <summary>
/// The export component's install (plan 32 DBR16): the download, the checksum, the extraction and the verification against a release folder (<c>file://</c> style) holding a tiny fake component; a refused hash or file names it, and nothing
/// is installed. The real Chromium is never downloaded here.
/// </summary>
public sealed class ExportInstallerTests : IDisposable
{
    private readonly string data = Path.Combine(Path.GetTempPath(), "export-data-" + Guid.NewGuid().ToString("N")[..8]);

    public ExportInstallerTests() => Directory.CreateDirectory(data);

    public void Dispose() { try { Directory.Delete(data, true); } catch (IOException) { /* temporary */ } }

    private ExportInstaller Installer(string version = FakeExportRelease.Version) => new(ExportInstaller.DefaultFetcher(), data, version);

    [Fact]
    public async Task A_release_folder_installs_a_whole_component_beside_the_data_and_a_second_run_downloads_nothing()
    {
        using var release = new FakeExportRelease();
        var steps = new List<string>();
        var result = await Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false), new ExportProgressSink(p => steps.Add(p.Step)));
        Assert.Equal(Path.Combine(data, "export-component"), result.Directory);
        Assert.Equal(FakeExportRelease.Chromium, result.Chromium);
        Assert.False(result.AlreadyInstalled);
        Assert.True(result.DownloadedBytes > 0);
        Assert.Empty(ExportComponent.Verify(result.Directory, release.Rid));
        Assert.Equal(FakeExportRelease.Version, ExportInstaller.ReadVersion(result.Directory));
        Assert.Contains("verifying the component", steps);
        Assert.Empty(Directory.GetDirectories(data, ".export-install-*"));   // the working folder is gone

        var again = await Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false));
        Assert.True(again.AlreadyInstalled);
        Assert.Equal(0, again.DownloadedBytes);
        var forced = await Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, true));
        Assert.False(forced.AlreadyInstalled);
    }

    [Fact]
    public async Task A_mirror_layout_is_read_too_and_the_settings_point_at_the_component()
    {
        using var release = new FakeExportRelease();
        var mirror = Path.Combine(data, "mirror");
        var inside = Path.Combine(mirror, "dbdatagateway", "v" + FakeExportRelease.Version);
        Directory.CreateDirectory(inside);
        foreach (var file in Directory.GetFiles(release.Folder)) File.Copy(file, Path.Combine(inside, Path.GetFileName(file)));
        var result = await Installer().InstallAsync(new ExportInstallRequest(null, new Uri(mirror).AbsoluteUri, null, release.Rid, false));
        var settings = ExportComponent.SettingsFor(result.Directory);
        Assert.Equal("pinned", settings["views.export.mode"]);
        Assert.Equal(Path.Combine(result.Directory, "print"), settings["views.export.pageAssets"]);
        Assert.True(File.Exists(settings["views.export.pin"]));
    }

    [Fact]
    public async Task An_archive_that_is_not_the_one_the_checksums_name_is_refused_naming_the_file_and_nothing_is_installed()
    {
        using var release = new FakeExportRelease(changeArchiveAfterChecksums: true);
        var ex = await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false)));
        Assert.Contains($"dbdatagateway-export-v{FakeExportRelease.Version}-{release.Rid}.zip", ex.Message);
        Assert.Contains("SHA256SUMS", ex.Message);
        Assert.Contains("Nothing was installed", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(data, "export-component")));
        Assert.Empty(Directory.GetDirectories(data, ".export-install-*"));
    }

    [Fact]
    public async Task A_component_with_a_changed_browser_is_refused_by_the_pin_inside_it_even_when_the_checksum_matches()
    {
        using var release = new FakeExportRelease(tamperWithComponent: folder => File.AppendAllText(Directory.GetFiles(Path.Combine(folder, ".chromium"), "chrome*", SearchOption.AllDirectories)[0], "x"));
        var ex = await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false)));
        Assert.Contains("does not match chromium.pin", ex.Message);
        Assert.Contains("chrome", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(data, "export-component")));
    }

    [Fact]
    public async Task A_changed_font_a_missing_licence_and_a_changed_print_file_are_each_refused_by_name()
    {
        using var release = new FakeExportRelease(tamperWithComponent: folder =>
        {
            File.AppendAllText(Path.Combine(folder, ".fonts", "noto-sans", "noto-sans-latin-400-normal.woff2"), "x");
            File.Delete(Path.Combine(folder, ".fonts", "noto-serif", "OFL.txt"));
            File.AppendAllText(Path.Combine(folder, "print", "print.js"), ";");
        });
        var ex = await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false)));
        Assert.Contains("noto-sans-latin-400-normal.woff2", ex.Message);
        Assert.Contains("licence of noto-serif is missing", ex.Message);
        Assert.Contains("print.js", ex.Message);
    }

    [Fact]
    public async Task A_release_without_a_component_a_folder_without_checksums_a_missing_folder_and_a_source_that_is_not_https_are_each_refused_in_words()
    {
        using var unlisted = new FakeExportRelease(listInSums: false);
        var ex = await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, unlisted.Folder, null, unlisted.Rid, false)));
        Assert.Contains("does not list", ex.Message);
        Assert.Contains("SHA256SUMS", ex.Message);

        var empty = Directory.CreateDirectory(Path.Combine(data, "empty")).FullName;
        Assert.Contains("SHA256SUMS", (await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, empty, null, null, false)))).Message);
        Assert.Contains("does not exist", (await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, Path.Combine(data, "nowhere"), null, null, false)))).Message);
        Assert.Contains("https", (await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, "http://example.test/x", null, null, false)))).Message);
    }

    [Fact]
    public async Task A_build_from_source_names_no_release_and_a_platform_without_a_component_is_refused()
    {
        using var release = new FakeExportRelease();
        Assert.Contains("--version", (await Assert.ThrowsAsync<ExportInstallException>(() => Installer("0.0.0").InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false)))).Message);
        Assert.Contains("osx-arm64", (await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, "osx-arm64", false)))).Message);
        Assert.Contains("not a release version", (await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest("../x", release.Folder, null, release.Rid, false)))).Message);
    }

    [Fact]
    public async Task An_archive_that_reaches_outside_its_folder_is_refused()
    {
        using var release = new FakeExportRelease(tamperWithZip: zip =>
        {
            using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update);
            using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open());
            writer.Write("x");
        });
        // FakeExportRelease takes the checksum after the tampering, so the checksum matches and the extraction is what refuses
        var ex = await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false)));
        Assert.Contains("outside the folder", ex.Message);
        Assert.False(File.Exists(Path.Combine(data, "escape.txt")));
    }

    [Fact]
    public async Task A_newer_release_replaces_the_installed_one()
    {
        using var first = new FakeExportRelease(version: "2026.11.12.1000");
        using var second = new FakeExportRelease(version: "2026.11.13.0900");
        await Installer("2026.11.12.1000").InstallAsync(new ExportInstallRequest(null, first.Folder, null, first.Rid, false));
        var result = await Installer("2026.11.13.0900").InstallAsync(new ExportInstallRequest(null, second.Folder, null, second.Rid, false));
        Assert.False(result.AlreadyInstalled);
        Assert.Equal("2026.11.13.0900", ExportInstaller.ReadVersion(result.Directory));
        Assert.Empty(ExportComponent.Verify(result.Directory, second.Rid));
    }

    [Fact]
    public void The_status_says_not_installed_then_installed_and_configured()
    {
        var options = new DbDataGateway.Server.Configuration.ViewsExportOptions();
        var before = ExportComponentStatus.Describe(options, data);
        Assert.False((bool)before["installed"]!);
        Assert.Contains("not installed", (string)before["summary"]!);
    }

    [Fact]
    public async Task A_component_of_the_browser_and_fonts_this_program_was_tested_with_says_verified()
    {
        using var release = new FakeExportRelease(testedPins: true);
        var result = await Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false));
        Assert.True(result.Pins.Verified);
        Assert.StartsWith("verified: the browser and fonts this program was tested with", result.Pins.Note);
        Assert.Equal(ExportPins.Chromium, result.Chromium);
        var status = ExportComponentStatus.Describe(StatusOptions(result.Directory), data);
        Assert.True((bool)status["pins_verified"]!);
        Assert.StartsWith("verified:", (string)status["pins_note"]!);
    }

    [Fact]
    public async Task A_component_with_another_chromium_is_installed_anyway_with_a_warning_in_words_and_status_says_so()
    {
        using var release = new FakeExportRelease();   // Chromium 1.2.3.4, other fonts
        var result = await Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false));
        Assert.False(result.AlreadyInstalled);
        Assert.Empty(ExportComponent.Verify(result.Directory, release.Rid));   // installed, whole
        Assert.False(result.Pins.Verified);
        Assert.Contains($"this component carries Chromium {FakeExportRelease.Chromium} (this program was tested with {ExportPins.Chromium})", result.Pins.Note);
        Assert.EndsWith(": installed as the release pins it.", result.Pins.Note);
        Assert.StartsWith("Warning: ", result.Pins.Sentence);
        var status = ExportComponentStatus.Describe(StatusOptions(result.Directory), data);
        Assert.True((bool)status["whole"]!);
        Assert.False((bool)status["pins_verified"]!);
        Assert.Contains($"this component carries Chromium {FakeExportRelease.Chromium}", (string)status["pins_note"]!);
        // a second run still reports it
        Assert.False((await Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false))).Pins.Verified);
    }

    [Fact]
    public async Task The_pins_decide_nothing_a_tampered_archive_is_still_refused_when_the_pins_match()
    {
        using var release = new FakeExportRelease(testedPins: true, changeArchiveAfterChecksums: true);
        var ex = await Assert.ThrowsAsync<ExportInstallException>(() => Installer().InstallAsync(new ExportInstallRequest(null, release.Folder, null, release.Rid, false)));
        Assert.Contains("Nothing was installed", ex.Message);
    }

    [Fact]
    public void The_embedded_pins_are_the_repository_files_so_they_cannot_drift()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "chromium.pin"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var name in new[] { "chromium.pin", "fonts.pin" })
            Assert.Equal(File.ReadAllText(Path.Combine(root.FullName, name)).Replace("\r\n", "\n").Split('\n').Select(l => l).ToArray(), ExportPins.Text(name).Split('\n'));
        Assert.Matches(@"^\d+(\.\d+){3}$", ExportPins.Chromium);
        Assert.Equal(3, ExportPins.Families.Count);
    }

    private static DbDataGateway.Server.Configuration.ViewsExportOptions StatusOptions(string folder)
    {
        var settings = ExportComponent.SettingsFor(folder);
        return new DbDataGateway.Server.Configuration.ViewsExportOptions { Mode = "pinned", Pin = settings["views.export.pin"], PageAssets = settings["views.export.pageAssets"] };
    }
}
