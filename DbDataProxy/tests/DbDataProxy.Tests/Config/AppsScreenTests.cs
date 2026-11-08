using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Management;
using DbDataProxy.Server.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataProxy.Tests.Config;

/// <summary>
/// Plan 26, AP24a: the management routes the console's Apps screen calls: reads of apps, versions, instances, navigation sets and approvals (viewer, or publisher), the removal of an app and of an instance within the store's rules,
/// and the revoking of an approval (the approver role, by a user of the identity home who holds it). A person of a console calls them with a token whose roles are theirs; the Proxy decides, never the screen. Each change is audited.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsScreenTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dbproxy-appsscreen-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-apps", ConsoleSigningKey.Generate());
    private WebApplication? app;
    private int managementPort;
    private PairedServer server = null!;

    private string DataDirectory => Path.Combine(root, "data");

    private Uri ManagementUrl => new($"https://127.0.0.1:{managementPort}");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(ProxyConfigFile.DirectoryIn(DataDirectory));
        File.WriteAllText(ProxyConfigFile.PathIn(DataDirectory), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        for (var attempt = 1; ; attempt++)
        {
            managementPort = FreePort();
            app = ProxyHost.Build([
                $"--Proxy:DataDirectory={DataDirectory}", "--Proxy:Listen:Address=127.0.0.1", "--Proxy:Listen:Port=0",
                "--Proxy:Management:Enabled=true", "--Proxy:Management:Address=127.0.0.1", $"--Proxy:Management:Port={managementPort}", "--Proxy:Management:LocalChannel=false",
            ]);
            try { await app.StartAsync(); break; }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)) { await app.DisposeAsync(); }
        }
        var files = new PairingFiles(ProxyConfigFile.PathIn(DataDirectory), Path.Combine(DataDirectory, "pairing-codes.json"));
        server = await PairingClient.PairAsync(ManagementUrl, files.Codes().Create(ServerRoles.Close(ServerRole.Admin | ServerRole.Approver | ServerRole.Publisher)).Code, console);
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(root);
    }

    private StateDatabase State => app!.Services.GetRequiredService<StateDatabase>();

    private DefinitionStore Definitions => new(State);

    private AppInstanceStore Store => new(Definitions);

    /// <summary>A call as the signed-in person Ada, holding exactly these roles on this server.</summary>
    private async Task<(HttpStatusCode Status, JsonNode Body)> CallAsync(ServerRole roles, HttpMethod method, string path, string? body = null)
    {
        using var http = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint));
        using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, path));
        request.Headers.Add(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(server.InstanceId, "ada-id", "Ada", roles)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private static AppPackage Package(string name, string version, string extra = "") =>
        new($"schemaVersion: 1\nname: {name}\nversion: {version}\nentry: index.html\nroles: [viewer]\n{extra}", new Dictionary<string, byte[]> { ["index.html"] = Encoding.UTF8.GetBytes($"<html>{name} {version}</html>") });

    private const string Models = "models:\n  - model: territories\n    entities: {territory: [read, write]}\n";

    private const string Portal = "schemaVersion: 1\nname: front\nversion: 1.0.0\nkind: portal\nportal:\n  mounts: [{app: acme, path: /sales/}]\n";

    private (string Approval, string Hash) Seed()
    {
        var users = new IdentityUserStore(State);
        var options = ProxyConfiguration.LoadOptions(DataDirectory);
        options.DataDirectory = DataDirectory;
        var serverId = ProxyStoreModule.ServerIdOf(options, State);
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Grace Hopper", "grace@example.invalid", UserRole.Viewer).Id), serverId, ServerRole.Approver);
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Bob Babbage", "bob@example.invalid", UserRole.Viewer).Id), serverId, ServerRole.Viewer);

        Store.PublishApp(Package("notes", "1.0.0", Models), "ada-laptop", note: "first");
        Store.PublishApp(Package("notes", "1.1.0", Models), "ada-laptop", note: "second");
        Store.Save(new AppInstance("acme", "notes", Title: "Acme notes"), "ada-laptop");
        Store.Save(new AppInstance("pinned", "notes", Pin: 1, Bindings: new Dictionary<string, string> { ["territories"] = "default" }), "ada-laptop");
        Store.PublishApp(new AppPackage(Portal, new Dictionary<string, byte[]>()), "ada-laptop");
        Store.Save(new AppInstance("front", "front"), "ada-laptop");
        Store.SaveNavigation(new NavigationSet("main", "Main menu", [new NavigationEntry("acme", "Acme"), new NavigationEntry("pinned")]), "ada-laptop");

        // a trusted app, approved by Grace for these bytes, published by the client that wrote it
        var vault = Package("vault", "1.0.0", "trust: trusted\n");
        var approvals = new AppApprovalStore(Definitions, approvers: new InMemoryApprovers().Add("u-grace", "Grace Hopper", approver: true));
        var approval = approvals.Approve(vault, "ada-laptop", "Grace Hopper", "reviewed");
        Store.PublishApp(vault, "ada-laptop", approvals: approvals);
        Store.Save(new AppInstance("safe", "vault"), "ada-laptop");
        return (approval.Id, approval.ContentHash);
    }

    [Fact]
    public async Task A_viewer_reads_apps_versions_instances_navigation_and_approvals_and_the_answer_says_what_the_caller_may_do()
    {
        var (approvalId, hash) = Seed();

        var apps = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps");
        Assert.Equal(HttpStatusCode.OK, apps.Status);
        var notes = apps.Body["apps"]!.AsArray().Single(a => (string?)a!["name"] == "notes")!;
        Assert.Equal(2, (int)notes["version"]!);
        Assert.Equal([1, 2], notes["versions"]!.AsArray().Select(v => (int)v!).Order().ToArray());
        Assert.Equal(["acme", "pinned"], notes["instances"]!.AsArray().Select(v => (string)v!).Order().ToArray());
        Assert.Equal("trusted", (string?)apps.Body["apps"]!.AsArray().Single(a => (string?)a!["name"] == "vault")!["trust"]);
        // what the viewer may do: nothing but read, and the screen says why its buttons are off
        Assert.False((bool)apps.Body["caller"]!["can_publish"]!);
        Assert.False((bool)apps.Body["caller"]!["can_approve"]!);
        Assert.Equal("Ada", (string?)apps.Body["caller"]!["you"]);

        var detail = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/notes");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.Equal(2, (int)detail.Body["serving"]!);
        var versions = detail.Body["versions"]!.AsArray();
        Assert.Equal([2, 1], versions.Select(v => (int)v!["version"]!).ToArray());              // newest first
        Assert.True((bool)versions[0]!["serving"]!);
        Assert.False((bool)versions[1]!["serving"]!);
        Assert.Equal("ada-laptop", (string?)versions[0]!["published_by"]);
        Assert.Equal("second", (string?)versions[0]!["note"]);
        // the about data (AP18): the models and what it does with each entity, who published it
        var about = versions[0]!["about"]!;
        Assert.Equal("territories", (string?)about["models"]![0]!["model"]);
        Assert.Equal("territory", (string?)about["models"]![0]!["entity"]);
        Assert.Equal("read and write", (string?)about["models"]![0]!["operations"]);
        Assert.Equal("ada-laptop", (string?)about["published_by"]);
        Assert.Null(about["approval"]);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/nothing")).Status);

        // the trusted app's only version carries its approval
        var vault = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/vault")).Body["versions"]![0]!;
        Assert.Equal(approvalId, (string?)vault["approval_id"]);
        Assert.Equal(hash, (string?)vault["hash"]);
        Assert.Equal("Grace Hopper", (string?)vault["about"]!["approval"]!["approved_by"]);

        var instances = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances");
        Assert.Equal(HttpStatusCode.OK, instances.Status);
        var rows = instances.Body["instances"]!.AsArray().ToDictionary(i => (string)i!["name"]!);
        Assert.True((bool)rows["acme"]!["follows"]!);
        Assert.Equal(2, (int)rows["acme"]!["app_version"]!);                                      // it follows what the app serves
        Assert.Equal("1.1.0", (string?)rows["acme"]!["manifest_version"]);
        Assert.Equal("/apps/acme/", (string?)rows["acme"]!["path"]);
        Assert.Null(rows["acme"]!["address"]);                                                     // no apps module on this Proxy: no address to open
        Assert.Equal(["front"], rows["acme"]!["mounted_by"]!.AsArray().Select(v => (string)v!).ToArray());
        Assert.Equal(["main"], rows["acme"]!["navigation"]!.AsArray().Select(v => (string)v!).ToArray());
        Assert.False((bool)rows["pinned"]!["follows"]!);
        Assert.Equal(1, (int)rows["pinned"]!["pin"]!);
        Assert.Equal(1, (int)rows["pinned"]!["app_version"]!);                                    // the pin keeps it on version 1 whatever the app serves
        Assert.Equal(2, (int)rows["pinned"]!["serving_version"]!);
        Assert.Equal("default", (string?)rows["pinned"]!["bindings"]!["territories"]);
        Assert.Equal("portal", (string?)rows["front"]!["kind"]);
        Assert.Equal("acme", (string?)rows["front"]!["mounts"]![0]!["instance"]);
        Assert.True((bool)rows["safe"]!["runs_as_asked"]!);
        Assert.Equal(approvalId, (string?)rows["safe"]!["approval_id"]);

        var navigation = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-navigation");
        Assert.Equal(HttpStatusCode.OK, navigation.Status);
        var set = navigation.Body["navigation"]!.AsArray().Single()!;
        Assert.Equal("main", (string?)set["name"]);
        Assert.Equal(["acme", "pinned"], set["entries"]!.AsArray().Select(e => (string)e!["instance"]!).ToArray());

        var approvals = await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-approvals");
        Assert.Equal(HttpStatusCode.OK, approvals.Status);
        var approval = approvals.Body["approvals"]!.AsArray().Single()!;
        Assert.Equal(approvalId, (string?)approval["id"]);
        Assert.Equal("vault", (string?)approval["app"]);
        Assert.Equal("Grace Hopper", (string?)approval["approved_by"]);
        Assert.Equal(hash, (string?)approval["hash"]);
        Assert.Equal(1, (int?)approval["published_version"]);
        Assert.False((bool)approval["revoked"]!);

        // an administrator may publish and an approver may approve: the screen's buttons follow
        Assert.True((bool)(await CallAsync(ServerRole.Admin, HttpMethod.Get, "/api/v1/app-approvals")).Body["caller"]!["can_publish"]!);
        var approver = (await CallAsync(ServerRole.Approver, HttpMethod.Get, "/api/v1/app-approvals")).Body["caller"]!;
        Assert.True((bool)approver["can_approve"]!);
        Assert.False((bool)approver["can_publish"]!);
    }

    [Fact]
    public async Task Reads_need_a_role_and_a_change_needs_the_role_it_changes_with()
    {
        Seed();
        // a publisher reads too (a paired Build or Store server, or the command line's `apps list --to`); a pairing with no role at all does not
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Get, "/api/v1/app-instances")).Status);
        foreach (var path in new[] { "/api/v1/apps", "/api/v1/apps/notes", "/api/v1/app-instances", "/api/v1/app-navigation", "/api/v1/app-approvals" })
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.None, HttpMethod.Get, path)).Status);

        // removing is for the publisher and the administrator; a viewer and an approver (who only reads) are refused and nothing is removed
        foreach (var role in new[] { ServerRole.Viewer, ServerRole.Approver, ServerRole.Operator })
        {
            var refused = await CallAsync(role, HttpMethod.Delete, "/api/v1/app-instances/safe");
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Contains("publisher", (string)refused.Body["error"]!);
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(role, HttpMethod.Delete, "/api/v1/apps/vault")).Status);
        }
        Assert.NotNull(Store.Get("safe"));

        // revoking is the approver's: an administrator without the role is refused, and says which pairing would do
        var approvalId = new AppApprovalStore(Definitions).All().Single().Id;
        var admin = await CallAsync(ServerRole.Admin, HttpMethod.Post, $"/api/v1/app-approvals/{approvalId}/revoke", "{\"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.Forbidden, admin.Status);
        Assert.Contains("approver role", (string)admin.Body["error"]!);
        Assert.Null(new AppApprovalStore(Definitions).All().Single().RevokedAt);
    }

    [Fact]
    public async Task An_instance_and_an_app_are_removed_only_when_the_stores_rules_allow_and_each_refusal_is_a_message()
    {
        Seed();
        var audit = new StateAuditLog(State);

        // the portal mounts acme: the store refuses, naming the portal; nothing changes
        var mounted = await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/acme");
        Assert.Equal(HttpStatusCode.Conflict, mounted.Status);
        Assert.Contains("mounted by the portal instance(s) front", (string)mounted.Body["error"]!);
        Assert.NotNull(Store.Get("acme"));

        // the app still has instances: refused, naming them
        var used = await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/apps/front");
        Assert.Equal(HttpStatusCode.Conflict, used.Status);
        Assert.Contains("still has instances", (string)used.Body["error"]!);
        Assert.Contains("front", (string)used.Body["error"]!);

        // in the right order it works: the portal's instance, then the instance it mounted, then the apps
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/front")).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/acme")).Status);
        Assert.Null(Store.Get("acme"));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/apps/front")).Status);
        Assert.DoesNotContain(Store.Apps(), a => a.Name == "front");
        Assert.Contains(Store.AppVersions("front"), v => v.Version == 1);                         // the version stays on record
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(ServerRole.Admin, HttpMethod.Delete, "/api/v1/app-instances/acme")).Status);

        // a publisher pairing may remove as well
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Publisher, HttpMethod.Delete, "/api/v1/app-instances/pinned")).Status);

        // the list no longer shows what was removed; the audit names the person and the outcome of each, refusals included
        var instances = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().Select(i => (string)i!["name"]!).Order().ToArray();
        Assert.Equal(["safe"], instances);
        var entries = audit.Read(100);
        Assert.Contains(entries, a => a.Command == "app instance-remove" && a.Target == "acme" && a.Outcome == "refused" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app instance-remove" && a.Target == "acme" && a.Outcome == "retired" && a.UserName == "Ada");
        Assert.Contains(entries, a => a.Command == "app remove" && a.Target == "front" && a.Outcome == "refused");
        Assert.Contains(entries, a => a.Command == "app remove" && a.Target == "front" && a.Outcome == "retired");
    }

    [Fact]
    public async Task Rolling_back_and_pinning_from_the_screen_use_the_routes_the_command_line_uses_and_the_list_follows()
    {
        Seed();
        var rolled = await CallAsync(ServerRole.Admin, HttpMethod.Post, "/api/v1/apps/notes/rollback", "{\"to\": 1}");
        Assert.Equal(HttpStatusCode.OK, rolled.Status);
        var rows = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().ToDictionary(i => (string)i!["name"]!);
        Assert.Equal(1, (int)rows["acme"]!["app_version"]!);                                      // an unpinned instance follows the rollback
        Assert.Equal(1, (int)(await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/notes")).Body["serving"]!);

        // pin acme to version 2 (the whole instance is sent, as the command does), then unpin it again
        var pin = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/app-instances/acme", "{\"app\": \"notes\", \"pin\": 2, \"title\": \"Acme notes\"}");
        Assert.Equal(HttpStatusCode.OK, pin.Status);
        rows = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().ToDictionary(i => (string)i!["name"]!);
        Assert.Equal(2, (int)rows["acme"]!["app_version"]!);
        Assert.False((bool)rows["acme"]!["follows"]!);
        Assert.Equal("Acme notes", (string?)rows["acme"]!["title"]);

        var refused = await CallAsync(ServerRole.Admin, HttpMethod.Put, "/api/v1/app-instances/acme", "{\"app\": \"notes\", \"pin\": 9}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("no version 9", (string)refused.Body["error"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.Viewer, HttpMethod.Post, "/api/v1/apps/notes/rollback", "{\"to\": 2}")).Status);
    }

    [Fact]
    public async Task An_approval_is_revoked_by_an_approver_who_is_an_approver_at_the_identity_home_and_the_instance_stops_running_as_trusted()
    {
        var (approvalId, _) = Seed();
        var path = $"/api/v1/app-approvals/{approvalId}/revoke";

        // the person holds the role on this server, but names someone who does not: refused with the home's reason, nothing changes
        var notApprover = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"Bob Babbage\"}");
        Assert.Equal(HttpStatusCode.BadRequest, notApprover.Status);
        Assert.Contains("Bob Babbage", (string)notApprover.Body["error"]!);
        Assert.Contains("Approver", (string)notApprover.Body["error"]!);
        var stranger = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"nobody at all\"}");
        Assert.Equal(HttpStatusCode.BadRequest, stranger.Status);
        Assert.Contains("not a user of the identity home", (string)stranger.Body["error"]!);
        // the person's own name is the default, and Ada is no user of the home
        var unnamed = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{}");
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.Status);
        Assert.Contains("'Ada'", (string)unnamed.Body["error"]!);
        Assert.Null(new AppApprovalStore(Definitions).All().Single().RevokedAt);

        var revoked = await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.OK, revoked.Status);
        Assert.True((bool)revoked.Body["revoked"]!);
        Assert.Equal("Grace Hopper", (string?)revoked.Body["revoked_by"]);
        Assert.NotNull(new AppApprovalStore(Definitions).All().Single().RevokedAt);

        // the list shows it revoked; the instance no longer runs as asked; the version has no standing approval
        var approval = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-approvals")).Body["approvals"]!.AsArray().Single()!;
        Assert.True((bool)approval["revoked"]!);
        Assert.NotNull(approval["revoked_at"]);
        var safe = (await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/app-instances")).Body["instances"]!.AsArray().Single(i => (string?)i!["name"] == "safe")!;
        Assert.False((bool)safe["runs_as_asked"]!);
        Assert.Null(safe["approval_id"]);
        Assert.Null((await CallAsync(ServerRole.Viewer, HttpMethod.Get, "/api/v1/apps/vault")).Body["versions"]![0]!["approval_id"]);

        // revoking again changes nothing and unknown ids are refused
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(ServerRole.Approver, HttpMethod.Post, path, "{\"by\": \"Grace Hopper\"}")).Status);
        var unknown = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals/apr_nothing/revoke", "{\"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Contains("no approval", (string)unknown.Body["error"]!);

        var audit = new StateAuditLog(State).Read(100);
        Assert.Contains(audit, a => a.Command == "app approval-revoke" && a.Target == approvalId && a.Outcome == "refused" && a.UserName == "Ada");
        Assert.Contains(audit, a => a.Command == "app approval-revoke" && a.Target == "vault" && a.Outcome.StartsWith(approvalId, StringComparison.Ordinal) && a.UserName == "Ada");
    }

    [Fact]
    public async Task A_person_approves_a_version_from_the_screen_but_never_their_own_work()
    {
        Seed();
        // a new trusted version written by Grace herself: she may not approve it, a different approver may
        var users = new IdentityUserStore(State);
        var options = ProxyConfiguration.LoadOptions(DataDirectory);
        options.DataDirectory = DataDirectory;
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Heidi Hollerith", "heidi@example.invalid", UserRole.Viewer).Id), ProxyStoreModule.ServerIdOf(options, State), ServerRole.Approver);
        var own = Package("ledger", "1.0.0", "trust: trusted\n");
        // published as sandboxed would be refused for trust, so it is stored the way the gate allows: approved by Heidi first, published by Grace, then a second version approved by Grace herself is refused
        var gate = new AppApprovalStore(Definitions, approvers: new InMemoryApprovers().Add("u-heidi", "Heidi Hollerith", approver: true));
        gate.Approve(own, "Grace Hopper", "Heidi Hollerith");
        Store.PublishApp(own, "Grace Hopper", approvals: gate);

        var self = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", "{\"app\": \"ledger\", \"version\": 1, \"by\": \"Grace Hopper\"}");
        Assert.Equal(HttpStatusCode.BadRequest, self.Status);
        Assert.Contains("is the author of", (string)self.Body["error"]!);

        var other = await CallAsync(ServerRole.Approver, HttpMethod.Post, "/api/v1/app-approvals", "{\"app\": \"vault\", \"version\": 1, \"by\": \"Heidi Hollerith\", \"note\": \"second look\"}");
        Assert.Equal(HttpStatusCode.OK, other.Status);
        Assert.Equal("Heidi Hollerith", (string?)other.Body["approved_by"]);
        Assert.Equal("Ada", (string?)other.Body["author"]);                                         // the signed-in person is named in the audit

        // a viewer cannot approve at all
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(ServerRole.Viewer, HttpMethod.Post, "/api/v1/app-approvals", "{\"app\": \"vault\", \"version\": 1, \"by\": \"Heidi Hollerith\"}")).Status);
    }

    [Fact]
    public async Task The_address_of_an_instance_is_on_the_apps_host_when_the_proxy_serves_one()
    {
        Seed();
        var routes = new ProxyApps(Definitions, () => new InMemoryApprovers(), new StateAuditLog(State), appsAddress: () => new Uri("https://apps.example.test:8818/"));
        var result = await routes.HandleAsync("GET", "app-instances", [], null, ManagementCaller.Local("operator"), CancellationToken.None);
        Assert.Equal(200, result.Status);
        var acme = result.Body["instances"]!.AsArray().Single(i => (string?)i!["name"] == "acme")!;
        Assert.Equal("https://apps.example.test:8818/apps/acme/", (string?)acme["address"]);
        Assert.Equal("https://apps.example.test:8818/", (string?)result.Body["apps_address"]);
    }
}
