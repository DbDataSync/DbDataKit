using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DbDataKit.Deploy;

namespace DbDataWorkspace.Tests;

/// <summary>
/// What a build holds to send, served to the control service that deploys it: the build's record (where and from which commit it was built, what the kind's validators found) and each item its kind's routes made, with its files by
/// SHA-256, and each file by its hash. Read only, for a viewer; the server makes no call of its own.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceBuildContentsTests
{
    private const string Project = "territories";

    [Fact]
    public async Task A_store_build_serves_its_record_its_models_and_one_package_per_model_and_each_file_by_hash()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await server.AsAdmin(HttpMethod.Post, "/api/v1/projects", new JsonObject { ["id"] = Project, ["kind"] = "store" }.ToJsonString())).Response.StatusCode);
        var workspace = (string)(await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces", """{"name":"draft"}""")).Body!["id"]!;
        var made = await server.AsAda(HttpMethod.Post, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds");
        var build = (string)made.Body!["id"]!;
        var builds = $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{build}";
        for (var i = 0; i < 300 && (string)(await server.AsAda(HttpMethod.Get, builds)).Body!["build"]!["status"]! is "queued" or "running"; i++) await Task.Delay(100);

        var contents = await server.AsAda(HttpMethod.Get, builds + "/contents");

        Assert.Equal(HttpStatusCode.OK, contents.Response.StatusCode);
        var record = contents.Body!["record"]!;
        Assert.Equal(("store", Project, workspace, build, "succeeded", true), ((string?)record["kind"], (string?)record["project"], (string?)record["workspace"], (string?)record["build"], (string?)record["status"], (bool)record["checks"]!["passed"]!));
        Assert.False(string.IsNullOrEmpty((string?)record["server"]));
        var items = contents.Body["items"]!.AsArray().Select(i => BuildItem.FromJson(i!.AsObject())).ToList();
        Assert.Equal(["model", "model-package/" + Project], items.Select(i => i.Id));
        Assert.Contains($"artifact/models/{Project}.yaml", items[0].Files.Select(f => f.Path));
        Assert.Equal(BuildContents.ManifestSha256(items[1].Root, items[1].Files), items[1].ManifestSha256);

        // each file by its hash, the bytes the build holds; a hash the build does not list is not served
        foreach (var file in items.SelectMany(i => i.Files))
        {
            var (response, _) = await server.Send("ada", DbDataKit.Web.Trust.ServerRole.Viewer, HttpMethod.Get, builds + "/files/" + file.Sha256);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAda(HttpMethod.Get, builds + "/files/" + new string('0', 64))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.AsAda(HttpMethod.Get, $"/api/v1/projects/{Project}/workspaces/{workspace}/builds/{Guid.NewGuid()}/contents")).Response.StatusCode);
        // a token with no role on the server reads nothing of it
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Send("nobody", DbDataKit.Web.Trust.ServerRole.None, HttpMethod.Get, builds + "/contents")).Response.StatusCode);
    }
}
