using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api.Tests;

/// <summary>Plan 30 PB03: an instance verifies a version's signature when it prepares it (<see cref="VersionSignatureGate"/>, <see cref="DefinitionFollower"/>).</summary>
public sealed class VersionSignatureGateTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-defs-gate-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly StateDatabase database;
    private readonly DefinitionPublisher publisher;
    private readonly DefinitionTrustStore trust;
    private readonly GroupSettings settings;
    private readonly SignaturePolicy policy;

    public VersionSignatureGateTests()
    {
        database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, TrustModule.Module], new StateScope(Group: "main")));
        publisher = new DefinitionPublisher(database, new DefinitionKinds().Register(new DefinitionKind("page", ["serving"], PublishMode.Serve)), new PayloadStore(database), "serving");
        trust = new DefinitionTrustStore(database);
        settings = new GroupSettings(database);
        policy = new SignaturePolicy(settings);
    }

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    private VersionSignatureGate Gate() => new((kind, name, version) => [.. publisher.Artifacts(kind, name, version).Select(a => new ArtifactRef(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))], trust, policy.RequiredByAdministrator);

    private DefinitionVersion Publish(string manifest, string? signature, string? signer = null) =>
        publisher.Publish(new PublishRequest("page", "home", manifest, "ci", DefinitionStore.Production, null, null, null, [], Signature: signature, Signer: signer)).Version;

    [Fact]
    public void A_signature_that_holds_is_served_and_one_that_does_not_is_refused_naming_the_version_and_why()
    {
        var key = ConsoleSigningKey.Generate();
        trust.Add("page", key.PublicKey, "ci", "root");
        var good = Publish("v1", DefinitionSigning.Sign(key, "page", "home", "v1", []), key.Fingerprint);
        Assert.Null(Gate().Refusal(good));

        // a stored manifest changed after it was signed (a database edited by hand, or a route that skipped the check)
        var forged = Publish("v2 changed", DefinitionSigning.Sign(key, "page", "home", "v2", []), key.Fingerprint);
        var refusal = Gate().Refusal(forged)!;
        Assert.Contains("version 2 of the page 'home'", refusal);
        Assert.Contains("does not hold", refusal);

        // signed by a key the kind does not trust
        var stranger = ConsoleSigningKey.Generate();
        var untrusted = Publish("v3", DefinitionSigning.Sign(stranger, "page", "home", "v3", []));
        Assert.Contains("is not one the group trusts for page", Gate().Refusal(untrusted)!);
    }

    [Fact]
    public void A_key_revoked_after_a_version_was_published_does_not_unseat_it()
    {
        var key = ConsoleSigningKey.Generate();
        trust.Add("page", key.PublicKey, "ci", "root");
        var version = Publish("v1", DefinitionSigning.Sign(key, "page", "home", "v1", []), key.Fingerprint);
        Assert.True(trust.Revoke("page", key.Fingerprint, "root"));
        Assert.Null(Gate().Refusal(version));
    }

    [Fact]
    public void An_unsigned_version_is_refused_only_for_a_kind_an_administrator_set_to_required()
    {
        var unsigned = Publish("v1", null);
        Assert.Null(Gate().Refusal(unsigned));   // the default is required for a new publish, not for what a per-kind route stored
        policy.MarkTestGroup(false);
        Assert.Null(Gate().Refusal(unsigned));
        policy.Set("page", true);
        Assert.Contains("is unsigned and signatures are required for page", Gate().Refusal(unsigned)!);
        policy.Set("page", false);
        Assert.Null(Gate().Refusal(unsigned));
    }

    private sealed class Serving : IDefinitionSubscriber
    {
        public string Kind => "page";
        public List<string> Applied { get; } = [];
        public void Apply(string name, DefinitionVersion? current) => Applied.Add($"{name}@{current?.Version}");
    }

    [Fact]
    public void A_follower_with_the_gate_keeps_serving_the_previous_version_and_reports_a_refused_one()
    {
        var key = ConsoleSigningKey.Generate();
        trust.Add("page", key.PublicKey, "ci", "root");
        var serving = new Serving();
        var problems = new List<(string, int?, Exception)>();
        var follower = new DefinitionFollower(new DefinitionStore(database), [serving], (kind, name, version, ex) => problems.Add((name, version, ex)), gate: Gate().Refusal);
        Publish("v1", DefinitionSigning.Sign(key, "page", "home", "v1", []), key.Fingerprint);
        follower.Start();
        Assert.Equal(["home@1"], serving.Applied);

        Publish("v2 changed", DefinitionSigning.Sign(key, "page", "home", "v2", []), key.Fingerprint);   // does not verify
        follower.Poll();
        Assert.Equal(["home@1"], serving.Applied);   // version 2 was never handed to what serves
        var (name, version, ex) = Assert.Single(problems);
        Assert.Equal(("home", (int?)2), (name, version));
        Assert.IsType<VersionSignatureRefusedException>(ex);

        Publish("v3", DefinitionSigning.Sign(key, "page", "home", "v3", []), key.Fingerprint);
        follower.Poll();
        Assert.Equal(["home@1", "home@3"], serving.Applied);
    }
}
