using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Cli;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 26, AP26: <c>apps publish|instance-save|rollback|list|approve --to</c> against a real Gateway started in this process, over its management API with a paired server client: the pairing's ceiling, the pinned certificate, and the
/// author the Gateway records (the paired client's name, never the operating-system user). The command runs as a person's would, with a client directory of its own that stands for another machine.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class AppsRemoteTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dbgateway-appsremote-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int managementPort;

    private string DataDirectory => Path.Combine(root, "data");

    private Uri ManagementUrl => new($"https://127.0.0.1:{managementPort}");

    private string Address => ManagementUrl.ToString();

    private PairingFiles Files => new(GatewayConfigFile.PathIn(DataDirectory), Path.Combine(DataDirectory, "pairing-codes.json"));

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
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(DataDirectory));
        File.WriteAllText(GatewayConfigFile.PathIn(DataDirectory), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        for (var attempt = 1; ; attempt++)
        {
            managementPort = FreePort();
            app = GatewayHost.Build([
                $"--Gateway:DataDirectory={DataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0",
                "--Gateway:Management:Enabled=true", "--Gateway:Management:Address=127.0.0.1", $"--Gateway:Management:Port={managementPort}", "--Gateway:Management:LocalChannel=false",
            ]);
            try { await app.StartAsync(); break; }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)) { await app.DisposeAsync(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(root);
    }

    private StateDatabase State => app!.Services.GetRequiredService<StateDatabase>();

    private AppInstanceStore Instances => new(new DefinitionStore(State));

    private string NewCode(ServerRole ceiling) => Files.Codes().Create(ServerRoles.Close(ceiling)).Code;

    private async Task<(int Exit, string Out, string Error)> RunAsync(string clientDirectory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.Router().RunAsync([.. args, "--client-dir", clientDirectory], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private string MakeApp(string name, string manifestExtra = "", string version = "1.0.0")
    {
        var folder = Path.Combine(root, name + "-" + version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "app.yaml"), $"schemaVersion: 1\nname: {name}\nversion: {version}\nentry: index.html\nroles: [viewer]\n{manifestExtra}");
        File.WriteAllText(Path.Combine(folder, "index.html"), $"<html><head></head><body>{name} {version}</body></html>");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllText(Path.Combine(folder, "assets", "app.js"), "1");
        return folder;
    }

    [Fact]
    public async Task An_app_is_published_to_a_remote_gateway_by_a_paired_client_and_the_author_recorded_is_that_client()
    {
        var client = Path.Combine(root, "tester-laptop");
        var folder = MakeApp("notes");

        // the first call pairs with the single-use code and pins the certificate; later calls need neither
        var first = await RunAsync(client, "apps", "publish", "--to", Address, "--code", NewCode(ServerRole.Publisher), "--client-name", "ada-laptop", "--dir", folder, "--note", "from afar");
        Assert.True(first.Exit == 0, first.Error);
        Assert.Contains("Paired with", first.Out);
        Assert.Contains("Published notes version 1", first.Out);
        Assert.Contains("as ada-laptop", first.Out);

        var recorded = Instances.AppVersions("notes").Single();
        Assert.Equal("ada-laptop", recorded.PublishedBy);                       // the paired client, not the person logged in to this machine
        Assert.NotEqual(Environment.UserName, recorded.PublishedBy);
        Assert.Equal("from afar", recorded.Note);
        Assert.Equal(3, AppPackage.Parse(recorded.Content).Files.Count + 1);     // the files and the manifest arrived whole

        var second = await RunAsync(client, "apps", "publish", "--to", Address, "--dir", MakeApp("notes", version: "1.1.0"));
        Assert.True(second.Exit == 0, second.Error);
        Assert.DoesNotContain("Paired with", second.Out);
        Assert.Equal([1, 2], Instances.AppVersions("notes").Select(v => v.Version).Order().ToList());

        var saved = await RunAsync(client, "apps", "instance-save", "--to", Address, "--name", "acme", "--app", "notes", "--title", "Acme notes");
        Assert.True(saved.Exit == 0, saved.Error);
        Assert.Equal("notes", Instances.Get("acme")!.App);
        var pinned = await RunAsync(client, "apps", "instance-save", "--to", Address, "--name", "pinned", "--app", "notes", "--pin", "1");
        Assert.True(pinned.Exit == 0, pinned.Error);
        Assert.Equal(1, Instances.Get("pinned")!.Pin);

        var rolled = await RunAsync(client, "apps", "rollback", "--on", Address, "--app", "notes", "--to", "1");
        Assert.True(rolled.Exit == 0, rolled.Error);
        Assert.Contains("now serves version 1", rolled.Out);
        Assert.Equal(1, Instances.Apps().Single(a => a.Name == "notes").Version);

        var listed = await RunAsync(client, "apps", "list", "--to", Address);
        Assert.Equal(0, listed.Exit);
        Assert.Contains("notes  serves version 1  (versions: 1, 2)", listed.Out);

        // the Gateway's audit names the client, for each change
        var audit = new StateAuditLog(State).Read(50);
        Assert.Contains(audit, a => a.Command == "app publish" && a.Target == "notes" && a.UserName == "ada-laptop");
    }

    [Fact]
    public async Task A_refusal_names_the_pairing_or_the_role_that_is_missing()
    {
        var folder = MakeApp("notes");

        // not paired, no code
        var unpaired = await RunAsync(Path.Combine(root, "nobody"), "apps", "publish", "--to", Address, "--dir", folder);
        Assert.Equal(1, unpaired.Exit);
        Assert.Contains("not paired", unpaired.Error);
        Assert.Contains("server pair --max-role publisher", unpaired.Error);
        Assert.Empty(Instances.AppVersions("notes"));

        // a code that is wrong
        var wrong = await RunAsync(Path.Combine(root, "nobody"), "apps", "publish", "--to", Address, "--code", "AAAAA-AAAAA-AAAAA-AAAAA", "--dir", folder);
        Assert.Equal(1, wrong.Exit);
        Assert.Contains("did not accept the code", wrong.Error);

        // a publisher cannot approve, and the message says which pairing does
        var publisher = Path.Combine(root, "publisher");
        Assert.Equal(0, (await RunAsync(publisher, "apps", "publish", "--to", Address, "--code", NewCode(ServerRole.Publisher), "--dir", folder)).Exit);
        var approve = await RunAsync(publisher, "apps", "approve", "--to", Address, "--path", folder, "--by", "Grace Hopper");
        Assert.Equal(1, approve.Exit);
        Assert.Contains("approver role", approve.Error);
        Assert.Contains("--max-role approver", approve.Error);

        // a viewer pairing cannot publish
        var viewer = Path.Combine(root, "viewer");
        var denied = await RunAsync(viewer, "apps", "publish", "--to", Address, "--code", NewCode(ServerRole.Viewer), "--dir", MakeApp("other"));
        Assert.Equal(1, denied.Exit);
        Assert.Contains("publisher role", denied.Error);
        Assert.Empty(Instances.AppVersions("other"));

        // --repo and --to together are a mistake
        var mixed = await RunAsync(publisher, "apps", "publish", "--to", Address, "--repo", DataDirectory, "--dir", folder);
        Assert.Equal(2, mixed.Exit);
    }

    [Fact]
    public async Task A_trusted_app_is_published_remotely_only_after_a_separate_approver_pairing_approves_its_exact_bytes()
    {
        // the people of the identity home: an approver on this server, and one who is not
        var users = new IdentityUserStore(State);
        var options = GatewayConfiguration.LoadOptions(DataDirectory);
        options.DataDirectory = DataDirectory;
        var server = GatewayStoreModule.ServerIdOf(options, State);
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Grace Hopper", "grace@example.invalid", UserRole.Viewer).Id), server, ServerRole.Approver);
        new HomeDirectory(State).Assign(AssignmentSubject.User(users.CreateUser("Bob Babbage", "bob@example.invalid", UserRole.Viewer).Id), server, ServerRole.Viewer | ServerRole.Author);

        var publisher = Path.Combine(root, "publisher");
        var reviewer = Path.Combine(root, "reviewer");
        var folder = MakeApp("vault", "trust: trusted\n");

        // no approval yet: refused by the Gateway, which names the hash; nothing is published
        var refused = await RunAsync(publisher, "apps", "publish", "--to", Address, "--code", NewCode(ServerRole.Publisher), "--client-name", "ada-laptop", "--dir", folder);
        Assert.Equal(1, refused.Exit);
        Assert.Contains("no approval", refused.Error);
        Assert.Matches("hash [0-9a-f]{64}", refused.Error);
        Assert.Empty(Instances.AppVersions("vault"));

        // the approver pairing is a different client; the approver must be a home user who holds the role
        var notApprover = await RunAsync(reviewer, "apps", "approve", "--to", Address, "--code", NewCode(ServerRole.Approver), "--client-name", "reviewer-pc", "--path", folder, "--by", "Bob Babbage");
        Assert.Equal(1, notApprover.Exit);
        Assert.Contains("Bob Babbage", notApprover.Error);
        Assert.Contains("Approver", notApprover.Error);
        var stranger = await RunAsync(reviewer, "apps", "approve", "--to", Address, "--path", folder, "--by", "nobody at all");
        Assert.Equal(1, stranger.Exit);
        Assert.Contains("not a user of the identity home", stranger.Error);

        var approved = await RunAsync(reviewer, "apps", "approve", "--to", Address, "--path", folder, "--by", "Grace Hopper", "--note", "reviewed");
        Assert.True(approved.Exit == 0, approved.Error);
        Assert.Contains("Approved vault", approved.Out);
        var approval = new AppApprovalStore(new DefinitionStore(State)).All().Single();
        Assert.Equal("Grace Hopper", approval.ApprovedBy);

        // the same bytes now publish; a changed byte needs a new approval
        var published = await RunAsync(publisher, "apps", "publish", "--to", Address, "--dir", folder);
        Assert.True(published.Exit == 0, published.Error);
        Assert.Equal("ada-laptop", Instances.AppVersions("vault").Single().PublishedBy);
        File.AppendAllText(Path.Combine(folder, "index.html"), "<!-- changed -->");
        var changed = await RunAsync(publisher, "apps", "publish", "--to", Address, "--dir", folder);
        Assert.Equal(1, changed.Exit);
        Assert.Contains("no approval", changed.Error);

        // approving a published version by number works the same way
        var byVersion = await RunAsync(reviewer, "apps", "approve", "--to", Address, "--app", "vault", "--version", "1", "--by", "Grace Hopper");
        Assert.True(byVersion.Exit == 0, byVersion.Error);
    }

    [Fact]
    public async Task The_local_mode_still_works_with_no_pairing_and_records_the_machines_user()
    {
        var folder = MakeApp("localapp");
        var output = new StringWriter();
        var exit = await Cli.Router().RunAsync(["apps", "publish", "--dir", folder, "--repo", DataDirectory], new CliContext(output, new StringWriter(), TextReader.Null));
        Assert.Equal(0, exit);
        Assert.Equal(Environment.UserName, Instances.AppVersions("localapp").Single().PublishedBy);
    }

    [Fact]
    public async Task The_management_api_refuses_app_calls_without_the_role_and_a_malformed_body()
    {
        var identity = new ConsoleIdentity("raw-client", ConsoleSigningKey.Generate());
        var paired = await PairingClient.PairAsync(ManagementUrl, NewCode(ServerRole.Viewer), identity, ClientKind.Server, "raw");
        async Task<HttpResponseMessage> Call(HttpMethod method, string path, ServerRole role, string? body = null)
        {
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
            using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, path));
            request.Headers.Add(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "x", "X", role)));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return await http.SendAsync(request);
        }

        // AP24a: reading apps is for a viewer too (the console's Apps screen); changing them is not
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/apps", ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/v1/apps/notes", ServerRole.Viewer, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/app-approvals", ServerRole.Viewer, "{}")).StatusCode);
        Assert.Empty(Instances.AppVersions("notes"));

        var publisherIdentity = new ConsoleIdentity("raw-publisher", ConsoleSigningKey.Generate());
        var publisher = await PairingClient.PairAsync(ManagementUrl, NewCode(ServerRole.Publisher), publisherIdentity, ClientKind.Server, "raw");
        async Task<(HttpStatusCode Status, JsonNode? Body)> AsPublisher(HttpMethod method, string path, string? body)
        {
            using var http = new HttpClient(PairingClient.Pinned(publisher.CertificateFingerprint));
            using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, path));
            request.Headers.Add(ManagementAuthentication.TokenHeader, publisherIdentity.Issuer().Issue(new ConsoleTokenRequest(publisher.InstanceId, "x", "X", ServerRole.Publisher)));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await AsPublisher(HttpMethod.Put, "/api/v1/apps/notes", "not json")).Status);
        var wrongName = await AsPublisher(HttpMethod.Put, "/api/v1/apps/other", $"{{\"manifest\": \"schemaVersion: 1\\nname: notes\\nversion: 1.0.0\\nentry: index.html\\nroles: [viewer]\\n\", \"files\": {{\"index.html\": \"{Convert.ToBase64String("x"u8.ToArray())}\"}}}}");
        Assert.Equal(HttpStatusCode.BadRequest, wrongName.Status);
        Assert.Contains("same name", (string)wrongName.Body!["error"]!);
        var notBase64 = await AsPublisher(HttpMethod.Put, "/api/v1/apps/notes", "{\"manifest\": \"schemaVersion: 1\\nname: notes\\nversion: 1.0.0\\nentry: index.html\\nroles: [viewer]\\n\", \"files\": {\"index.html\": \"***\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, notBase64.Status);
        Assert.Empty(Instances.AppVersions("notes"));
    }
}
