using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataKit.Deploy.Tests;

public sealed class BuildContentsTests : IDisposable
{
    private readonly string build = Directory.CreateTempSubdirectory("deploy-build-").FullName;
    private readonly string laidOut = Directory.CreateTempSubdirectory("deploy-laid-out-").FullName;

    public void Dispose()
    {
        Directory.Delete(build, true);
        Directory.Delete(laidOut, true);
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(build, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static readonly ProjectPublishRoute Package = new("runner", "package", "a package");
    private static readonly ProjectPublishRoute SemanticModel = new("gateway", "semantic-model", "models");
    private static readonly ProjectPublishRoute Model = new("gateway", "model", "models");
    private static readonly ProjectPublishRoute ModelPackage = new("runner", "model-package", "one package per model");

    [Fact]
    public void A_build_projects_build_holds_its_package_and_its_semantic_models_as_two_items_each_with_its_own_files()
    {
        Write("artifact/package.yaml", "schema: package/1\n");
        Write("artifact/files/rendered/plan.sql", "select 1");
        Write("artifact/gateway/gateway.json", "{}");
        Write("result.json", "{}");

        var items = BuildContents.Of([Package, SemanticModel], build);

        Assert.Equal(["package", "semantic-model"], items.Select(i => i.Id));
        Assert.Equal(["artifact/files/rendered/plan.sql", "artifact/package.yaml"], items[0].Files.Select(f => f.Path));
        Assert.Equal(["artifact/gateway/gateway.json"], items[1].Files.Select(f => f.Path));
        Assert.Equal(("artifact", "runner", "package"), (items[0].Root, items[0].Product, items[0].What));
        Assert.Equal(Sha("select 1"), items[0].Files[0].Sha256);
        // the hash of the list names the paths under the item's folder, so it is the same content whatever build made it
        Assert.Equal(Sha($"files/rendered/plan.sql\t{Sha("select 1")}\npackage.yaml\t{Sha("schema: package/1\n")}\n"), items[0].ManifestSha256);
    }

    [Fact]
    public void A_store_build_holds_its_models_and_one_package_per_model_and_a_route_that_made_nothing_has_no_item()
    {
        Write("artifact/models/sales.yaml", "name: sales");
        Write("artifact/runner/sales/package.yaml", "schema: package/1\nname: store-model-sales\n");
        Write("artifact/runner/sales/files/model.yaml", "name: sales");
        Write("artifact/runner/hr/package.yaml", "schema: package/1\nname: store-model-hr\n");

        var items = BuildContents.Of([Model, ModelPackage, SemanticModel], build);

        Assert.Equal(["model", "model-package/hr", "model-package/sales"], items.Select(i => i.Id));
        Assert.Equal(("sales", "artifact/runner/sales"), (items[2].Model, items[2].Root));
        Assert.Equal(["artifact/runner/sales/files/model.yaml", "artifact/runner/sales/package.yaml"], items[2].Files.Select(f => f.Path));
    }

    [Fact]
    public async Task An_item_is_laid_out_again_from_its_files_by_hash_and_a_file_that_is_not_the_one_listed_is_refused()
    {
        Write("artifact/package.yaml", "schema: package/1\n");
        Write("artifact/files/repo/config/task.yaml", "name: orders");
        var item = BuildItem.FromJson(BuildContents.Of([Package], build).Single().ToJson());
        var byHash = item.Files.ToDictionary(f => f.Sha256, f => File.ReadAllBytes(Path.Combine(build, f.Path)));

        var root = await BuildContents.FetchAsync(item, laidOut, (sha, _) => Task.FromResult(byHash[sha]));

        Assert.Equal(Path.Combine(laidOut, "artifact"), root);
        Assert.Equal("name: orders", File.ReadAllText(Path.Combine(root, "files", "repo", "config", "task.yaml")));

        var changed = await Assert.ThrowsAsync<DeployException>(() => BuildContents.FetchAsync(item, Path.Combine(laidOut, "again"), (_, _) => Task.FromResult(Encoding.UTF8.GetBytes("something else"))));
        Assert.Contains("is not the one the build lists", changed.Message);
        var relisted = item with { Files = [.. item.Files.Skip(1)] };
        Assert.Contains("does not match its hash", (await Assert.ThrowsAsync<DeployException>(() => BuildContents.FetchAsync(relisted, Path.Combine(laidOut, "third"), (_, _) => Task.FromResult(Array.Empty<byte>())))).Message);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("artifact/../../x")]
    [InlineData("C:/x")]
    public void A_path_that_would_leave_the_folder_is_refused(string path)
    {
        var json = new JsonObject { ["id"] = "package", ["product"] = "runner", ["what"] = "package", ["root"] = "artifact", ["manifest_sha256"] = "0", ["files"] = new JsonArray(new JsonObject { ["path"] = path, ["sha256"] = "00", ["size"] = 1 }) };
        Assert.Throws<DeployException>(() => BuildItem.FromJson(json));
    }

    [Fact]
    public void The_record_of_an_item_takes_only_the_fields_of_a_build_record_in_a_fixed_order()
    {
        var result = new JsonObject
        {
            ["status"] = "succeeded", ["commit"] = "abc123", ["uncommitted_changes"] = true,
            ["diagnostics"] = new JsonArray(new JsonObject { ["severity"] = "warning", ["code"] = "W1", ["message"] = "a slot is unused", ["file"] = "config/task.yaml", ["line"] = 4, ["extra"] = "dropped" }),
        };
        var described = BuildRecord.Describe("ws-1", "orders", "sync", "main", Guid.Parse("11111111-2222-3333-4444-555555555555"), "succeeded", null, result);
        described["injected"] = "not signed";
        var item = new BuildItem("package", "runner", "package", null, "artifact", [], "f00d");

        var text = BuildRecord.Text(described, item);

        Assert.Equal("""{"schema":"build-record/1","server":"ws-1","project":"orders","kind":"sync","workspace":"main","build":"11111111-2222-3333-4444-555555555555","commit":"abc123","uncommitted_changes":true,"item":"package","manifest_sha256":"f00d","checks":{"passed":true,"findings":[{"severity":"warning","code":"W1","message":"a slot is unused","file":"config/task.yaml","line":4}]}}""", text);
        Assert.Equal("ws-1", (string?)BuildRecord.Read(text)!["server"]);
        Assert.Null(BuildRecord.Read("{\"schema\":\"build-record/9\"}"));
        described.Remove("project");
        Assert.Contains("\"project\"", Assert.Throws<DeployException>(() => BuildRecord.Text(described, item)).Message);
    }
}

public sealed class PackageSigningTests : IDisposable
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
    private readonly ConsoleIdentity publisher = new("console-1", ConsoleSigningKey.Generate());
    private readonly string folder = Directory.CreateTempSubdirectory("deploy-signing-").FullName;

    public PackageSigningTests()
    {
        File.WriteAllText(Path.Combine(folder, "package.yaml"), Manifest);
        Directory.CreateDirectory(Path.Combine(folder, "files", "bin"));
        File.WriteAllText(Path.Combine(folder, "files", "bin", "Sales.dll"), "not really an assembly");
    }

    public void Dispose() => Directory.Delete(folder, true);

    private FakeLink Link(bool trusted, bool required) => new(Runner.Url)
    {
        Answer = (method, path, _) => (method, FakeLink.PathOf(path)) switch
        {
            ("GET", "trust/integration") => (200, new JsonObject
            {
                ["kind"] = "integration", ["required"] = required,
                ["keys"] = trusted ? new JsonArray(new JsonObject { ["key_id"] = publisher.Key.Fingerprint, ["revoked"] = false }) : new JsonArray(),
            }),
            ("HEAD", _) => (200, null),
            ("PUT", "definitions/integration/sales") => (200, new JsonObject { ["version"] = 1, ["changed"] = true, ["mode"] = "stage", ["signature"] = "verified", ["tasks"] = new JsonArray("setup") }),
            _ => null,
        },
    };

    [Fact]
    public async Task A_runner_that_trusts_the_key_gets_the_version_signed_over_its_manifest_files_and_build_record()
    {
        using var link = Link(trusted: true, required: true);
        const string record = """{"schema":"build-record/1","build":"b1"}""";

        var answer = await ArtifactDeploy.PublishPackageAsync(link, Runner, folder, new DeployOptions(Channel: "dev"), "Ada", _ => { }, signer: publisher, build: record);

        var body = JsonNode.Parse(link.Calls[^1].Body!)!;
        Assert.Equal(record, (string?)body["build"]);
        var files = body["artifacts"]!.AsArray().Select(a => new ArtifactRef((string)a!["path"]!, (string)a["sha256"]!, (long)a["size"]!, null)).ToList();
        var verdict = new TrustStoreVerifierProbe(publisher).Verify(new VersionSignatureRequest("integration", "sales", (string)body["manifest"]!, files, (string?)body["signature"], record));
        Assert.Equal(SignatureState.Verified, verdict);
        // the same signature does not hold over another build record, or over none
        Assert.Equal(SignatureState.Invalid, new TrustStoreVerifierProbe(publisher).Verify(new VersionSignatureRequest("integration", "sales", (string)body["manifest"]!, files, (string?)body["signature"], record.Replace("b1", "b2"))));
        Assert.Equal(SignatureState.Invalid, new TrustStoreVerifierProbe(publisher).Verify(new VersionSignatureRequest("integration", "sales", (string)body["manifest"]!, files, (string?)body["signature"])));
        Assert.Equal("verified", (string?)answer["signature"]);
    }

    [Fact]
    public async Task A_runner_that_does_not_trust_the_key_gets_it_unsigned_unless_it_requires_signatures_and_then_nothing_is_published()
    {
        using var optional = Link(trusted: false, required: false);
        await ArtifactDeploy.PublishPackageAsync(optional, Runner, folder, new DeployOptions(Channel: "dev"), "Ada", _ => { }, signer: publisher, build: "{}");
        Assert.Null(JsonNode.Parse(optional.Calls[^1].Body!)!["signature"]);

        using var required = Link(trusted: false, required: true);
        var refused = await Assert.ThrowsAsync<DeployException>(() => ArtifactDeploy.PublishPackageAsync(required, Runner, folder, new DeployOptions(Channel: "dev"), "Ada", _ => { }, signer: publisher, build: "{}"));
        Assert.Contains($"does not trust the key {publisher.Key.Fingerprint}", refused.Message);
        Assert.DoesNotContain(required.Calls, c => c.Method == "PUT" && c.Path.StartsWith("definitions/", StringComparison.Ordinal));
    }

    /// <summary>The trust store's check of a signature, against a store that trusts only the publisher's key.</summary>
    private sealed class TrustStoreVerifierProbe(ConsoleIdentity publisher)
    {
        public SignatureState Verify(VersionSignatureRequest request)
        {
            var parts = (request.Signature ?? "").Split(':');
            if (parts.Length != 3 || parts[1] != publisher.Key.Fingerprint) return SignatureState.Invalid;
            return ConsoleSigningKey.Verify(publisher.Key.PublicKey, DefinitionSigning.Message(request.Kind, request.Name, request.Manifest, request.Artifacts, request.Build), Url.Decode(parts[2])!)
                ? SignatureState.Verified : SignatureState.Invalid;
        }
    }
}
