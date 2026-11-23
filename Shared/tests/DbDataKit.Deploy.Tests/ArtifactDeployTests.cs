using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Web.Trust;

namespace DbDataKit.Deploy.Tests;

public sealed class ArtifactDeployTests : IDisposable
{
    private const string Manifest = """
        schema: package/1
        name: sales
        launch: { kind: assembly, assembly: bin/Sales.dll }
        built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
        tasks:
          - name: setup
            entry: { type: Sales.Setup }
            triggers:
              - manual: true
        """;

    private static readonly DeployTarget Runner = new("jobs", "https://runner:8495", "r1", DeployProducts.Runner);
    private static readonly DeployTarget Gateway = new("edge", "https://edge:8443", "g1", DeployProducts.Gateway);
    private static readonly ConsoleIdentity Publisher = new("console-1", ConsoleSigningKey.Generate());

    private readonly string folder = Directory.CreateTempSubdirectory("deploy-artifact-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public async Task A_package_is_bound_then_staged_and_only_the_files_the_runner_lacks_are_sent()
    {
        File.WriteAllText(Path.Combine(folder, "package.yaml"), Manifest);
        Directory.CreateDirectory(Path.Combine(folder, "files", "bin"));
        File.WriteAllText(Path.Combine(folder, "files", "bin", "Sales.dll"), "not really an assembly");
        using var link = new FakeLink(Runner.Url)
        {
            Answer = (method, path, _) => (method, FakeLink.PathOf(path)) switch
            {
                ("PUT", "bindings/dev/integration/sales") => (200, new JsonObject { ["bindings"] = new JsonArray(new JsonObject { ["slot"] = "source", ["bound_to"] = "erp" }) }),
                ("HEAD", _) => null,
                ("PUT", var p) when p.StartsWith("artifacts/", StringComparison.Ordinal) => (201, new JsonObject()),
                ("PUT", "definitions/integration/sales") => (200, new JsonObject { ["version"] = 3, ["changed"] = true, ["mode"] = "stage", ["signature"] = "sig", ["tasks"] = new JsonArray("setup") }),
                _ => null,
            },
        };
        var steps = new List<string>();
        var options = new DeployOptions(Channel: "dev", Bind: new Dictionary<string, string> { ["source"] = "erp" }, Note: "build 3 [workspace:w/sales/main]");

        var answer = await ArtifactDeploy.DeployAsync("sync", new ProjectPublishRoute(DeployProducts.Runner, "package", "a package"), link, Publisher, Runner, folder, options, "Ada", steps.Add);

        // the runner is asked whether it trusts the publisher's key for packages; this one does not say, so the version goes unsigned
        Assert.Equal(["PUT bindings/dev/integration/sales", "HEAD artifacts", "PUT artifacts", "GET trust/integration", "PUT definitions/integration/sales"],
            link.Calls.Select(c => c.Method + " " + (c.Path.StartsWith("artifacts/", StringComparison.Ordinal) ? "artifacts" : c.Path)));
        var publish = JsonNode.Parse(link.Calls[^1].Body!)!;
        Assert.Equal(("dev", "stage", "build 3 [workspace:w/sales/main]"), ((string?)publish["channel"], (string?)publish["mode"], (string?)publish["note"]));
        Assert.Equal(("sales", 3, "stage", 1, "jobs", "https://runner:8495"), ((string?)answer["package"], (int)answer["version"]!, (string?)answer["mode"], (int)answer["uploaded"]!, (string?)answer["target"], (string?)answer["runner"]));
        Assert.Contains("Bound source in dev.", steps);
        Assert.Equal("Staged sales on dev: version 3.", steps.Last());
    }

    [Fact]
    public async Task A_package_without_a_channel_or_a_manifest_is_refused_before_a_call()
    {
        using var link = new FakeLink(Runner.Url);

        var noChannel = await Assert.ThrowsAsync<DeployException>(() => ArtifactDeploy.PublishPackageAsync(link, Runner, folder, new DeployOptions(), "Ada", _ => { }));
        var noManifest = await Assert.ThrowsAsync<DeployException>(() => ArtifactDeploy.PublishPackageAsync(link, Runner, folder, new DeployOptions(Channel: "dev"), "Ada", _ => { }));

        Assert.Equal((400, "Name the channel of the Runner the package goes to (\"channel\")."), (noChannel.Status, noChannel.Message));
        Assert.Equal((409, "This build has no package to publish (package.yaml is not in it)."), (noManifest.Status, noManifest.Message));
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task A_route_the_library_does_not_know_and_a_build_with_no_model_are_refused_in_words()
    {
        using var link = new FakeLink(Gateway.Url);

        var unknown = await Assert.ThrowsAsync<DeployException>(() => ArtifactDeploy.DeployAsync("notes", new ProjectPublishRoute(DeployProducts.Gateway, "zip", "a zip"), link, Publisher, Gateway, folder, new DeployOptions(), "Ada", _ => { }));
        var noModel = await Assert.ThrowsAsync<DeployException>(() => ArtifactDeploy.DeployAsync("store", new ProjectPublishRoute(DeployProducts.Gateway, "model", "models"), link, Publisher, Gateway, folder, new DeployOptions(), "Ada", _ => { }));

        Assert.Equal("A notes project publishes 'zip' to a gateway, which this server does not know how to send.", unknown.Message);
        Assert.Equal((409, "This build has no model to publish."), (noModel.Status, noModel.Message));
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task A_gateway_that_cannot_be_reached_while_models_are_sent_is_a_502()
    {
        Directory.CreateDirectory(Path.Combine(folder, "models"));
        File.WriteAllText(Path.Combine(folder, "models", "territories.yaml"), GatewayDefinitionsPublishTests.Territories("by-hand"));
        using var link = new FakeLink(Gateway.Url) { Unreachable = true };

        var refused = await Assert.ThrowsAsync<DeployException>(() => ArtifactDeploy.PublishModelsAsync(link, Publisher, Gateway, folder, new DeployOptions(), _ => { }));

        Assert.Equal((502, "https://edge:8443 could not be reached (Connection refused)."), (refused.Status, refused.Message));
    }
}
