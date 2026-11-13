using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Tests.Support;
using DbDataKit.Views.Export;

namespace DbDataGateway.Tests.Hosting;

/// <summary>
/// <c>dbdatagateway export install|status</c> (plan 32 DBR16): a person (or an installer script) installs the component from a release folder and the command sets <c>views.export</c>; the output document follows the schema. A tiny fake
/// component stands in for the real one.
/// </summary>
public sealed class ExportCommandTests : IDisposable
{
    private readonly string data = Directory.CreateTempSubdirectory("ddk-export-cli").FullName;

    public void Dispose() { try { Directory.Delete(data, true); } catch (IOException) { /* temporary */ } }

    private static async Task<(int Exit, string Out, string Error, JsonNode? Json)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.RunAsync([.. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString(), output.ToString().TrimStart().StartsWith('{') ? JsonNode.Parse(output.ToString()) : null);
    }

    [Fact]
    public async Task Install_downloads_from_a_release_folder_sets_views_export_in_server_yaml_and_status_then_says_it_is_installed()
    {
        using var release = new FakeExportRelease();
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(data));
        File.WriteAllText(GatewayConfigFile.PathIn(data), "schemaVersion: 2\n# kept\nlogSql: false\n");

        var before = await Run("export", "status", "--repo", data);
        Assert.Equal(0, before.Exit);
        Assert.False((bool)before.Json!["data"]!["installed"]!);

        var install = await Run("export", "install", "--source", release.Folder, "--version", FakeExportRelease.Version, "--repo", data);
        Assert.Equal(0, install.Exit);
        var result = install.Json!["data"]!;
        Assert.Equal(FakeExportRelease.Version, (string)result["release"]!);
        Assert.Equal(FakeExportRelease.Chromium, (string)result["chromium"]!);
        Assert.False((bool)result["already_installed"]!);
        Assert.True((bool)result["restart_needed"]!);
        // another Chromium than the one this program was tested with: installed all the same, a warning in words, exit 0
        Assert.False((bool)result["pins_verified"]!);
        Assert.Contains("this component carries Chromium " + FakeExportRelease.Chromium, (string)result["pins_note"]!);
        Assert.Contains("views.export.mode", result["settings_changed"]!.ToJsonString());
        var yaml = File.ReadAllText(GatewayConfigFile.PathIn(data));
        Assert.Contains("# kept", yaml);
        Assert.Contains("logSql: false", yaml);
        Assert.Contains("mode: pinned", yaml);
        // the file is still a valid configuration and says what the component says
        var options = GatewayConfiguration.LoadOptions(data);
        Assert.Equal("pinned", options.Views.Export.Mode);
        Assert.Equal(Path.Combine(data, "export-component", "chromium.pin"), options.Views.Export.Pin);
        Assert.Equal(Path.Combine(data, "export-component", "print"), options.Views.Export.PageAssets);

        var after = await Run("export", "status", "--repo", data);
        Assert.Equal(0, after.Exit);
        Assert.True((bool)after.Json!["data"]!["installed"]!);
        Assert.True((bool)after.Json["data"]!["whole"]!);
        Assert.True((bool)after.Json["data"]!["configured"]!);
        Assert.False((bool)after.Json["data"]!["pins_verified"]!);
        Assert.Contains("this component carries Chromium " + FakeExportRelease.Chromium, (string)after.Json["data"]!["pins_note"]!);

        // a second run finds it there and changes nothing
        var again = await Run("export", "install", "--source", release.Folder, "--version", FakeExportRelease.Version, "--repo", data);
        Assert.Equal(0, again.Exit);
        Assert.True((bool)again.Json!["data"]!["already_installed"]!);
        Assert.Empty(again.Json["data"]!["settings_changed"]!.AsArray());
    }

    [Fact]
    public async Task A_component_of_the_tested_browser_says_verified_in_the_install_and_in_status()
    {
        using var release = new FakeExportRelease(testedPins: true);
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(data));
        File.WriteAllText(GatewayConfigFile.PathIn(data), "schemaVersion: 2\n");
        var install = await Run("export", "install", "--source", release.Folder, "--version", FakeExportRelease.Version, "--repo", data);
        Assert.Equal(0, install.Exit);
        Assert.True((bool)install.Json!["data"]!["pins_verified"]!);
        var status = await Run("export", "status", "--repo", data);
        Assert.True((bool)status.Json!["data"]!["pins_verified"]!);
        Assert.StartsWith("verified: the browser and fonts this program was tested with", (string)status.Json["data"]!["pins_note"]!);
    }

    [Fact]
    public async Task A_refused_hash_fails_the_command_naming_the_file_and_leaves_server_yaml_alone()
    {
        using var release = new FakeExportRelease(changeArchiveAfterChecksums: true);
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(data));
        File.WriteAllText(GatewayConfigFile.PathIn(data), "schemaVersion: 2\n");
        var run = await Run("export", "install", "--source", release.Folder, "--version", FakeExportRelease.Version, "--repo", data);
        Assert.Equal(1, run.Exit);
        Assert.Contains($"dbdatagateway-export-v{FakeExportRelease.Version}-{release.Rid}.zip", run.Json!["errors"]!.ToJsonString());
        Assert.Equal("schemaVersion: 2\n", File.ReadAllText(GatewayConfigFile.PathIn(data)));
        Assert.False(Directory.Exists(Path.Combine(data, "export-component")));
    }

    [Fact]
    public async Task Status_of_a_component_whose_file_changed_says_so_and_fails()
    {
        using var release = new FakeExportRelease();
        Assert.Equal(0, (await Run("export", "install", "--source", release.Folder, "--version", FakeExportRelease.Version, "--repo", data)).Exit);
        File.AppendAllText(Path.Combine(data, "export-component", "print", "print.css"), "x");
        var status = await Run("export", "status", "--repo", data);
        Assert.Equal(1, status.Exit);
        Assert.False((bool)status.Json!["data"]!["whole"]!);
        Assert.Contains("print.css", status.Json["data"]!["problems"]!.ToJsonString());
    }

    [Fact]
    public async Task A_wrong_use_is_a_usage_error()
    {
        Assert.Equal(2, (await Run("export", "install", "extra", "--repo", data)).Exit);
        Assert.Equal(2, (await Run("export", "status", "--nope", "--repo", data)).Exit);
    }
}
