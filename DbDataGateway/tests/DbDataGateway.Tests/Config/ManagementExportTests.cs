using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Export;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests.Config;

/// <summary>Plan 32 DBR16: the console's "PDF export is not installed: Install" card on the management API. It runs the installer of <c>dbdatagateway export install</c> against a release folder holding a tiny fake component (the real Chromium is never downloaded).</summary>
public sealed partial class ManagementTests
{
    private async Task<(HttpStatusCode Status, JsonNode Body)> ExportAsync(PairedServer admin, HttpMethod method, string suffix = "", object? body = null, ServerRole roles = ServerRole.Admin)
    {
        var response = await CallAsync(admin, method, "/api/v1/views/export" + suffix, roles, body is null ? null : System.Text.Json.JsonSerializer.Serialize(body));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? ParseBody(text) : new JsonObject());
    }

    private async Task<JsonNode> WaitForInstallAsync(PairedServer admin)
    {
        for (var i = 0; i < 300; i++)
        {
            var body = (await ExportAsync(admin, HttpMethod.Get)).Body;
            if ((string)body["install"]!["state"]! is "done" or "failed") return body;
            await Task.Delay(100);
        }
        throw new TimeoutException("the install did not finish");
    }

    [Fact]
    public async Task Only_an_administrator_reads_the_export_component_or_installs_it_and_nothing_changes_for_the_rest()
    {
        await StartAsync();
        var admin = await PairAsync(ServerRole.Admin | ServerRole.Author | ServerRole.Approver);
        var before = File.ReadAllText(ServerFile);
        foreach (var held in new[] { ServerRole.Viewer, ServerRole.Operator, ServerRole.Author, ServerRole.Approver })
        {
            var roles = ServerRoles.Close(held);
            Assert.Equal(HttpStatusCode.Forbidden, (await ExportAsync(admin, HttpMethod.Get, "", null, roles)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await ExportAsync(admin, HttpMethod.Post, "/install", new { }, roles)).Status);
        }
        Assert.Equal(before, File.ReadAllText(ServerFile));
        Assert.False(Directory.Exists(Path.Combine(dataDirectory, "export-component")));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await ExportAsync(admin, HttpMethod.Delete)).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await ExportAsync(admin, HttpMethod.Get, "/install")).Status);
    }

    [Fact]
    public async Task The_install_button_installs_the_component_from_the_release_sets_views_export_as_a_commit_by_the_caller_audits_it_and_says_a_restart_is_needed()
    {
        using var release = new FakeExportRelease();
        var was = Environment.GetEnvironmentVariable(ExportInstaller.SourceVariable);
        Environment.SetEnvironmentVariable(ExportInstaller.SourceVariable, release.Folder);
        try
        {
            await StartAsync();
            var admin = await PairAsync();
            var state = (await ExportAsync(admin, HttpMethod.Get)).Body;
            Assert.False((bool)state["installed"]!);
            Assert.Contains("not installed", (string)state["summary"]!);
            Assert.Equal("idle", (string)state["install"]!["state"]!);

            var started = await ExportAsync(admin, HttpMethod.Post, "/install", new { version = FakeExportRelease.Version });
            Assert.Equal(HttpStatusCode.Accepted, started.Status);
            var done = await WaitForInstallAsync(admin);
            Assert.Equal("done", (string)done["install"]!["state"]!);
            Assert.True((bool)done["install"]!["restart_needed"]!);
            Assert.Contains("Restart", (string)done["install"]!["message"]!);
            Assert.True((bool)done["installed"]!);
            Assert.True((bool)done["whole"]!);
            Assert.True((bool)done["configured"]!);
            Assert.Equal(FakeExportRelease.Chromium, (string)done["chromium"]!);

            var onDisk = File.ReadAllText(ServerFile);
            Assert.Contains("mode: pinned", onDisk);
            Assert.Contains("pageAssets:", onDisk);
            Assert.Contains($"certificatePassword: {TlsSecret}", onDisk);   // what was there is kept
            var commit = new GitCommitService(ConfigDirectory).GetHistory("", 1)[0];
            Assert.Equal("Ada", commit.AuthorName);
            Assert.Contains($"PDF export component {FakeExportRelease.Version} installed", commit.Message);
            var audit = ConfigTestSupport.AuditEntries(dataDirectory).Select(e => e.ToJsonString()).ToList();
            Assert.Contains(audit, e => e.Contains("export.install", StringComparison.Ordinal) && e.Contains("started", StringComparison.Ordinal));
            Assert.Contains(audit, e => e.Contains("export.install", StringComparison.Ordinal) && e.Contains("installed " + FakeExportRelease.Version, StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(ExportInstaller.SourceVariable, was); }
    }

    [Fact]
    public async Task An_install_whose_archive_does_not_match_the_checksums_fails_in_words_naming_the_file_and_changes_nothing()
    {
        using var release = new FakeExportRelease(changeArchiveAfterChecksums: true);
        var was = Environment.GetEnvironmentVariable(ExportInstaller.SourceVariable);
        Environment.SetEnvironmentVariable(ExportInstaller.SourceVariable, release.Folder);
        try
        {
            await StartAsync();
            var admin = await PairAsync();
            var before = File.ReadAllText(ServerFile);
            Assert.Equal(HttpStatusCode.Accepted, (await ExportAsync(admin, HttpMethod.Post, "/install", new { version = FakeExportRelease.Version })).Status);
            var done = await WaitForInstallAsync(admin);
            Assert.Equal("failed", (string)done["install"]!["state"]!);
            Assert.Contains($"dbdatagateway-export-v{FakeExportRelease.Version}-{release.Rid}.zip", (string)done["install"]!["message"]!);
            Assert.False((bool)done["installed"]!);
            Assert.Equal(before, File.ReadAllText(ServerFile));
            Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory).Select(e => e.ToJsonString()), e => e.Contains("export.install", StringComparison.Ordinal) && e.Contains("failed", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(ExportInstaller.SourceVariable, was); }
    }
}
