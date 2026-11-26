using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DbDataKit.Authoring.Server;
using DbDataKit.Cli;
using DbDataKit.Secrets;
using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>
/// The Workspace server calls no Runner or Gateway and holds no publisher key: the console deploys a build, runs its tasks, tests its bindings and reads a
/// Gateway's catalog, as the person. Each route that did this here answers 410 with the sentence naming the console's call; the command <c>publisher</c>
/// answers the same way; the keys an earlier version paired are deleted when the server starts; and no assembly of the server refers to a client of a
/// Runner or a Gateway.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class WorkspaceIsolationTests
{
    [Fact]
    public async Task Each_removed_route_answers_410_naming_the_consoles_call_whatever_the_role()
    {
        await using var server = await WorkspaceHarness.StartAsync();
        Assert.Equal(12, WorkspaceManagement.RemovedRoutes.Count);
        foreach (var (method, route, answer) in WorkspaceManagement.RemovedRoutes)
        {
            var path = route.Replace("{project}", "orders").Replace("{id}", "ada-draft").Replace("{build}", Guid.NewGuid().ToString("D")).Replace("{run}", "r1").Replace("{name}", "edge");
            var body = method is "POST" or "PUT" ? """{"target":"edge"}""" : null;
            foreach (var (response, json) in new[] { await server.AsAdmin(new HttpMethod(method), path, body), await server.AsAda(new HttpMethod(method), path, body) })
            {
                Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
                var error = (string)json!["error"]!;
                Assert.Equal(answer, error);
                Assert.StartsWith(WorkspaceManagement.ConsoleDoesIt, error, StringComparison.Ordinal);
                Assert.Contains("console", error[WorkspaceManagement.ConsoleDoesIt.Length..], StringComparison.Ordinal);
            }
        }
        Assert.DoesNotContain(server.Audit(), a => a.Command.StartsWith("publisher-key", StringComparison.Ordinal) || a.Command.StartsWith("build.publish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_publisher_command_is_gone_and_says_the_console_deploys()
    {
        var error = new StringWriter();
        var exit = await WorkspaceCli.RunAsync(["publisher", "pair", "edge", "--url", "https://gateway.example", "--code", "x"], new CliContext(TextWriter.Null, error, TextReader.Null));

        Assert.Equal(CliExit.Usage, exit);
        Assert.StartsWith("'dbdataworkspace publisher' is gone: ", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("the console deploys", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_publisher_keys_an_earlier_version_paired_are_deleted_and_the_state_login_stays()
    {
        var directory = Directory.CreateTempSubdirectory("ddw-keys-").FullName;
        try
        {
            var store = WorkspaceSecrets.Open(directory);
            await store.SetAsync(new SecretName("workspace", "publisher-key", "edge"), SecretValue.From("workspace-1a2b3c:c2VlZA"));
            await store.SetAsync(new SecretName("workspace", "publisher-key", "jobs"), SecretValue.From("workspace-4d5e6f:c2VlZA"));
            await store.SetAsync(new SecretName("server", "state", "main"), SecretValue.From("hunter2"));
            await File.WriteAllTextAsync(Path.Combine(directory, WorkspaceSecrets.PublishTargetsFile), "[]");
            var log = new List<string>();

            Assert.Equal(2, await WorkspaceSecrets.ForgetPublisherKeysAsync(directory, log.Add));

            var left = new List<string>();
            await foreach (var name in WorkspaceSecrets.Open(directory).ListAsync("")) left.Add(name.ToString());
            Assert.Equal(["dbdatakit:server:state:main"], left);
            Assert.False(File.Exists(Path.Combine(directory, WorkspaceSecrets.PublishTargetsFile)));
            Assert.Contains(log, l => l.Contains("dbdatakit:workspace:publisher-key:edge", StringComparison.Ordinal));
            Assert.Equal(0, await WorkspaceSecrets.ForgetPublisherKeysAsync(directory, log.Add));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task A_server_with_no_secret_store_gets_none_made_when_it_forgets_its_keys()
    {
        var directory = Directory.CreateTempSubdirectory("ddw-keys-").FullName;
        try
        {
            Assert.Equal(0, await WorkspaceSecrets.ForgetPublisherKeysAsync(directory, _ => { }));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // what a build hands the console is described with these; every other type of the deploy library calls a Runner or a Gateway
    private static readonly HashSet<string> DeployTypesAllowed = ["BuildContents", "BuildItem", "BuildFile", "BuildRecord"];

    [Theory]
    [InlineData(typeof(WorkspaceManagement))]                     // DbDataKit.Authoring.Server
    [InlineData(typeof(DbDataKit.Authoring.WorkspaceApi))]         // DbDataKit.Authoring
    [InlineData(typeof(WorkspaceServer))]                          // dbdataworkspace
    public void No_assembly_of_the_server_refers_to_a_client_of_a_runner_or_a_gateway(Type from)
    {
        using var stream = File.OpenRead(from.Assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var found = new List<string>();
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var ns = metadata.GetString(type.Namespace);
            var name = metadata.GetString(type.Name);
            if (ns.StartsWith("DbDataKit.Runner.Client", StringComparison.Ordinal)
                || (ns == "DbDataKit.Deploy" && !DeployTypesAllowed.Contains(name))
                || name is "DefinitionsClient" or "IDefinitionsLink" or "PinnedLink")
                found.Add($"{ns}.{name}");
        }
        Assert.True(found.Count == 0, $"{from.Assembly.GetName().Name} refers to {string.Join(", ", found)}");
        Assert.DoesNotContain(from.Assembly.GetReferencedAssemblies(), a => a.Name == "DbDataKit.Runner.Client");
    }
}
