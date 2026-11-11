using DbDataKit.State;

namespace DbDataKit.Apps.Tests;

/// <summary>Plan 26 AP19: what the identity home is told an instance's approved manifest allows: its scope, and nothing for an instance that runs no approved version.</summary>
public sealed class AppInstanceGrantsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-grants-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppInstanceStore instances;
    private readonly AppApprovalStore approvals;

    public AppInstanceGrantsTests()
    {
        Directory.CreateDirectory(folder);
        var definitions = new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module]));
        instances = new AppInstanceStore(definitions);
        approvals = new AppApprovalStore(definitions);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private const string Models = "models:\n  - model: territories\n    entities: {territory: [read, write], region: [read]}\n  - model: sales\n    entities: {order: [read]}\n";

    [Fact]
    public void An_instance_of_a_sandboxed_app_is_granted_the_scope_of_its_manifests_models_and_a_missing_one_nothing()
    {
        instances.PublishApp("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\n" + Models, "ada");
        instances.Save(new AppInstance("acme", "notes"), "ada");
        var grants = new AppInstanceGrants(instances, approvals);

        Assert.Equal(["territories/territory:write", "territories/region:read", "sales/order:read"], grants.ScopeOf("acme"));
        Assert.Null(grants.ScopeOf("globex"));   // no such instance: the home issues nothing
    }

    [Fact]
    public void An_app_that_names_no_model_is_granted_an_empty_scope_not_nothing()
    {
        instances.PublishApp("schemaVersion: 1\nname: plain\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\n", "ada");
        instances.Save(new AppInstance("plain", "plain"), "ada");

        Assert.Empty(new AppInstanceGrants(instances, approvals).ScopeOf("plain")!);   // it runs, and may reach no data
    }

    [Fact]
    public void A_trusted_app_is_granted_a_scope_only_while_an_approval_stands_for_its_version()
    {
        instances.PublishApp("schemaVersion: 1\nname: vault\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\ntrust: trusted\n" + Models, "ada");
        instances.Save(new AppInstance("vault", "vault"), "ada");
        var grants = new AppInstanceGrants(instances, approvals);

        Assert.Null(grants.ScopeOf("vault"));   // it asked to be trusted and nobody approved these bytes
        var approval = approvals.Approve("vault", 1, "grace");
        Assert.Equal(3, grants.ScopeOf("vault")!.Count);
        approvals.Revoke(approval.Id, "grace");
        Assert.Null(grants.ScopeOf("vault"));
        Assert.Null(new AppInstanceGrants(instances).ScopeOf("vault"));   // a host that keeps no approvals trusts nothing
    }
}
