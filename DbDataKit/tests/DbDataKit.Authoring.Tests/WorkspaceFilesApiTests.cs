using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Tests;

/// <summary>The file commands of a workspace (plan 22a, GP35a) on the fake app project: list, read, write, move and delete in a draft, with the roles they need and a path that cannot leave the draft.</summary>
public sealed partial class WorkspaceApiTests
{
    private static string Files(string id, string? path = null) => $"/api/v1/projects/orders/workspaces/{id}/files" + (path is null ? "" : "/" + path);

    private static string Put(string content) => new JsonObject { ["content"] = content }.ToJsonString();

    [Fact]
    public async Task The_files_of_a_draft_are_listed_and_read_by_a_viewer_and_git_is_not_among_them()
    {
        var id = await Draft();

        var listed = await Json(await Send("vic", ServerRole.Viewer, HttpMethod.Get, Files(id)));
        Assert.Equal(["app.yaml", "views/orders.yaml"], listed["files"]!.AsArray().Select(f => (string)f!["path"]!));
        Assert.False((bool)listed["truncated"]!);
        Assert.Equal(WorkspaceFiles.MaxFileBytes, (int)listed["max_file_bytes"]!);

        var read = await Json(await Send("vic", ServerRole.Viewer, HttpMethod.Get, Files(id, "views/orders.yaml")));
        Assert.Equal("views/orders.yaml", (string)read["path"]!);
        Assert.Contains("kind: grid", (string)read["content"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await AsAda(HttpMethod.Get, Files(id, "nope.yaml"))).StatusCode);
    }

    [Fact]
    public async Task A_file_is_written_moved_and_deleted_in_the_draft_and_never_in_the_projects_checkout()
    {
        var id = await Draft();

        var made = await AsAda(HttpMethod.Put, Files(id, "views/new/customers.yaml"), Put("kind: grid\n"));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.True((bool)(await Json(made))["created"]!);
        Assert.Equal(HttpStatusCode.OK, (await AsAda(HttpMethod.Put, Files(id, "views/new/customers.yaml"), Put("kind: grid\ntitle: Customers\n"))).StatusCode);
        Assert.Equal("kind: grid\ntitle: Customers\n", File.ReadAllText(Path.Combine(FolderOf(id), "views", "new", "customers.yaml")));
        Assert.False(File.Exists(Path.Combine(app1, "views", "new", "customers.yaml")));

        Assert.Equal(HttpStatusCode.OK, (await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/move", """{"from":"views/new/customers.yaml","to":"views/customers.yaml"}""")).StatusCode);
        Assert.True(File.Exists(Path.Combine(FolderOf(id), "views", "customers.yaml")));
        Assert.False(Directory.Exists(Path.Combine(FolderOf(id), "views", "new")));      // the folder it left empty goes
        Assert.Equal(HttpStatusCode.Conflict, (await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/move", """{"from":"views/customers.yaml","to":"views/orders.yaml"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/move", """{"from":"nope","to":"x"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/move", """{"from":"app.yaml"}""")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await AsAda(HttpMethod.Delete, Files(id, "views/customers.yaml"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsAda(HttpMethod.Delete, Files(id, "views/customers.yaml"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AsAda(HttpMethod.Put, Files(id, "x.yaml"), "{}")).StatusCode);

        // a commit takes what the commands left: the new file is not in it, the changed one is
        await AsAda(HttpMethod.Put, Files(id, "views/orders.yaml"), Put("kind: form\n"));
        var committed = await Json(await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/commit", """{"message":"Edit the grid"}"""));
        Assert.True((bool)committed["committed"]!);
        var changes = (await Json(await AsAda(HttpMethod.Get, $"/api/v1/projects/orders/workspaces/{id}/changes")))["changes"]!.AsArray();
        Assert.Equal(["views/orders.yaml"], changes.Select(c => (string)c!["path"]!));
    }

    [Fact]
    public async Task Writing_needs_the_author_role_and_is_the_owners_and_the_host_is_told_of_each_command()
    {
        var id = await Draft();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send("vic", ServerRole.Viewer, HttpMethod.Put, Files(id, "a.yaml"), Put("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("bob", ServerRole.Author, HttpMethod.Put, Files(id, "a.yaml"), Put("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("bob", ServerRole.Author, HttpMethod.Delete, Files(id, "app.yaml"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("bob", ServerRole.Author, HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/move", """{"from":"app.yaml","to":"b.yaml"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("vic", ServerRole.Viewer, HttpMethod.Delete, Files(id, "app.yaml"))).StatusCode);
        Assert.True(File.Exists(Path.Combine(FolderOf(id), "app.yaml")));
        Assert.False(File.Exists(Path.Combine(FolderOf(id), "a.yaml")));
        Assert.DoesNotContain(changes, c => c.Kind.StartsWith("file-", StringComparison.Ordinal));

        await AsAda(HttpMethod.Put, Files(id, "a.yaml"), Put("x"));
        await AsAda(HttpMethod.Post, $"/api/v1/projects/orders/workspaces/{id}/move", """{"from":"a.yaml","to":"b.yaml"}""");
        await AsAda(HttpMethod.Delete, Files(id, "b.yaml"));
        Assert.Equal([("file-written", "a.yaml"), ("file-moved", "a.yaml -> b.yaml"), ("file-deleted", "b.yaml")],
            changes.Where(c => c.Kind.StartsWith("file-", StringComparison.Ordinal)).Select(c => (c.Kind, c.Detail!)));
        Assert.All(changes.Where(c => c.Kind.StartsWith("file-", StringComparison.Ordinal)), c => Assert.Equal("ada", c.ActorId));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("views/.GIT/x")]
    [InlineData(".git")]
    [InlineData("a%5Cb")]                 // a backslash
    [InlineData("C%3A/x")]                // a drive
    [InlineData("views//x")]
    [InlineData("views/x.")]
    public async Task A_path_that_could_leave_the_draft_or_touch_git_is_refused_for_every_command(string path)
    {
        var id = await Draft();

        foreach (var response in new[]
        {
            await AsAda(HttpMethod.Put, Files(id, path), Put("x")),
            await AsAda(HttpMethod.Get, Files(id, path)),
            await AsAda(HttpMethod.Delete, Files(id, path)),
        })
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{path}: {response.StatusCode}");
        var written = await AsAda(HttpMethod.Put, Files(id, path), Put("x"));
        Assert.NotEqual(HttpStatusCode.Created, written.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, written.StatusCode);
    }

    [Fact]
    public async Task A_move_into_or_out_of_places_it_must_not_reach_is_refused_in_the_body_where_the_client_cannot_tidy_it()
    {
        var id = await Draft();
        var url = $"/api/v1/projects/orders/workspaces/{id}/move";

        foreach (var to in new[] { "../escaped.yaml", "views/../../escaped.yaml", "/etc/passwd", ".git/hooks/post-commit", "C:/x", "..\\x" })
        {
            var refused = await AsAda(HttpMethod.Post, url, new JsonObject { ["from"] = "app.yaml", ["to"] = to }.ToJsonString());
            Assert.True(refused.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, $"{to}: {refused.StatusCode}");
        }
        var from = await AsAda(HttpMethod.Post, url, """{"from":".git/HEAD","to":"head.txt"}""");
        Assert.Equal(HttpStatusCode.Forbidden, from.StatusCode);
        Assert.True(File.Exists(Path.Combine(FolderOf(id), "app.yaml")));
        Assert.False(File.Exists(Path.Combine(root, "server", "workspaces", "escaped.yaml")));
        Assert.False(File.Exists(Path.Combine(FolderOf(id), "head.txt")));
    }

    [Fact]
    public async Task A_file_too_big_or_not_text_is_refused_and_a_file_that_is_a_folder_is_named()
    {
        var id = await Draft();

        var tooBig = await AsAda(HttpMethod.Put, Files(id, "big.txt"), Put(new string('a', WorkspaceFiles.MaxFileBytes + 1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.False(File.Exists(Path.Combine(FolderOf(id), "big.txt")));
        Assert.Equal(HttpStatusCode.Created, (await AsAda(HttpMethod.Put, Files(id, "limit.txt"), Put(new string('a', WorkspaceFiles.MaxFileBytes)))).StatusCode);

        File.WriteAllBytes(Path.Combine(FolderOf(id), "logo.bin"), [1, 2, 0, 3]);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await AsAda(HttpMethod.Get, Files(id, "logo.bin"))).StatusCode);
        File.WriteAllBytes(Path.Combine(FolderOf(id), "latin.txt"), [0xE9, 0x41]);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await AsAda(HttpMethod.Get, Files(id, "latin.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await AsAda(HttpMethod.Put, Files(id, "nul.txt"), Put("a\0b"))).StatusCode);

        var folder = await AsAda(HttpMethod.Put, Files(id, "views"), Put("x"));
        Assert.Equal(HttpStatusCode.Conflict, folder.StatusCode);
        var inside = await AsAda(HttpMethod.Put, Files(id, "app.yaml/inner.txt"), Put("x"));
        Assert.Equal(HttpStatusCode.Conflict, inside.StatusCode);
        Assert.Contains("app.yaml", (string)(await Json(inside))["error"]!);
    }

    [Fact]
    public async Task A_symbolic_link_in_the_draft_is_not_followed_listed_or_written_through()
    {
        if (OperatingSystem.IsWindows()) return;     // creating a link needs a privilege there
        var id = await Draft();
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(FolderOf(id), "link"), outside);
        File.CreateSymbolicLink(Path.Combine(FolderOf(id), "file-link.txt"), Path.Combine(outside, "secret.txt"));

        var listed = (await Json(await AsAda(HttpMethod.Get, Files(id))))["files"]!.AsArray().Select(f => (string)f!["path"]!);
        Assert.DoesNotContain(listed, p => p.StartsWith("link", StringComparison.Ordinal) || p == "file-link.txt");
        Assert.Equal(HttpStatusCode.Forbidden, (await AsAda(HttpMethod.Get, Files(id, "link/secret.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AsAda(HttpMethod.Get, Files(id, "file-link.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AsAda(HttpMethod.Put, Files(id, "link/new.txt"), Put("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AsAda(HttpMethod.Put, Files(id, "file-link.txt"), Put("overwritten"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AsAda(HttpMethod.Delete, Files(id, "link/secret.txt"))).StatusCode);
        Assert.Equal("secret", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
    }

    [Fact]
    public void The_path_rules_in_one_place()
    {
        foreach (var ok in new[] { "a", "a/b.yaml", ".gitignore", ".gitattributes", "views/orders.yaml", "dir.with.dots/x" })
            Assert.Equal(ok, WorkspaceFiles.Normalize(ok));
        foreach (var bad in new[] { "", "/a", "a/", "a//b", ".", "..", "a/../b", "a/./b", "a\\b", "c:x", ".git", ".git/x", "a/.GIT", ".git ", ".git.", "a\tb", "a\nb", new string('a', 241), string.Join('/', Enumerable.Repeat("a", 25)) })
            Assert.Throws<WorkspaceFileException>(() => WorkspaceFiles.Normalize(bad));
        Assert.Equal(403, Assert.Throws<WorkspaceFileException>(() => WorkspaceFiles.Normalize(".git/config")).Status);
        Assert.Equal(400, Assert.Throws<WorkspaceFileException>(() => WorkspaceFiles.Normalize("../x")).Status);
    }

    [Fact]
    public void A_workspace_holds_a_limited_number_of_files()
    {
        var folder = Directory.CreateTempSubdirectory("files-limit-").FullName;
        try
        {
            for (var i = 0; i < WorkspaceFiles.MaxFiles; i++) File.WriteAllText(Path.Combine(folder, $"f{i}.txt"), "");
            Assert.Equal(WorkspaceFiles.MaxFiles, WorkspaceFiles.List(folder).Count);
            Assert.Equal(413, Assert.Throws<WorkspaceFileException>(() => WorkspaceFiles.Write(folder, "one-more.txt", "x")).Status);
            Assert.False(WorkspaceFiles.Write(folder, "f0.txt", "replaced"));      // a file that exists is replaced, not counted again
        }
        finally { Directory.Delete(folder, true); }
    }
}
