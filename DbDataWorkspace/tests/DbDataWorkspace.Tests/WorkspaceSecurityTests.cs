using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The properties of the Workspace that must not get weaker when it is hosted in the console (plan 33 section 3, SC04): the publisher's key is kept in the secret store under a name of its own and nowhere else, and no call a screen can make
/// returns it. (The build child's environment and network are tested where the child is started, <c>DbDataKit.Apps.Bundling.Tests.BuildChildIsolationTests</c> and <c>VendoredStoreTests</c>; git hooks and paths in <c>DbDataKit.Config.Git.Tests</c> and <c>DbDataKit.Authoring.Tests</c>.)
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceSecurityTests
{
    [Fact]
    public async Task The_publisher_key_is_in_the_secret_store_under_its_own_name_and_in_no_other_file_log_or_answer()
    {
        await using var gateway = await FakeGateway.StartAsync();
        await using var server = await WorkspaceHarness.StartAsync();
        var paired = await server.AsAdmin(HttpMethod.Post, "/api/v1/publisher-keys/edge/pair", new JsonObject { ["url"] = gateway.Url.ToString(), ["code"] = gateway.NewCode() }.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, paired.Response.StatusCode);

        // the key, as only the publish step reads it: through the secret store's resolver, from the reference
        using var value = await WorkspaceState.Secrets(server.Directory).ResolveAsync(SecretRef.Parse("secret:dbdata:workspace:publisher-key:edge"));
        var key = value.RevealString();
        var seed = key[(key.IndexOf(':') + 1)..];
        Assert.True(seed.Length >= 32);

        // no file of the server's folder holds it in the clear (the store encrypts it; the state, the targets, the audit and the answers hold references and names)
        foreach (var file in Directory.EnumerateFiles(server.Directory, "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var text = System.Text.Encoding.Latin1.GetString(copy.ToArray());
            Assert.False(text.Contains(seed, StringComparison.Ordinal), $"the publisher key is in {Path.GetRelativePath(server.Directory, file)}");
        }

        // the answers a person can read name it by reference or not at all, and the server's log never has it
        var answers = new[]
        {
            await server.AsAdmin(HttpMethod.Get, "/api/v1/publisher-keys"),
            await server.Send("grace", ServerRole.Approver, HttpMethod.Get, "/api/v1/publish-targets"),
            await server.AsAdmin(HttpMethod.Get, "/api/v1/app-build"),
            await server.AsAdmin(HttpMethod.Get, "/api/v1/state"),
        };
        Assert.All(answers, answer => Assert.DoesNotContain(seed, answer.Body?.ToJsonString() ?? "", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Log, line => line.Contains(seed, StringComparison.Ordinal));
        Assert.DoesNotContain(server.Audit(), a => (a.Target ?? "").Contains(seed, StringComparison.Ordinal) || a.Outcome.Contains(seed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_pasted_in_place_of_a_reference_is_refused_and_nothing_is_kept()
    {
        await using var server = await WorkspaceHarness.StartAsync();

        var pasted = await server.AsAdmin(HttpMethod.Put, "/api/v1/publisher-keys/edge", """{"secret_ref":"workspace-1a2b3c:c2VlZA"}""");

        Assert.Equal(HttpStatusCode.BadRequest, pasted.Response.StatusCode);
        Assert.Empty(server.Store().PublisherKeys());
    }
}
