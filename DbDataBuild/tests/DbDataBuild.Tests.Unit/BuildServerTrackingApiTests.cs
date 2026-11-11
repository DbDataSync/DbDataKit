using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using DbDataBuild.Tui.Model;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 28c E18, first slice: what the Build server says of its copy of the tracking tables (<c>GET /api/v1/tracking</c>: mode, primary, a checkpoint and a row count for every stream of every identity, the divergences) and the administrator's
/// resolve (<c>POST /api/v1/tracking/divergences/{id}/resolve</c>), over a real server and its state database. The rows are written the way the copier writes them.
/// </summary>
public sealed class BuildServerTrackingApiTests : IAsyncLifetime
{
    private const string Identity = "buildtracking:sqlserver://sql1/warehouse.dbo.tracking@warehouse";
    private const string Stream = "schema_version";

    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-trackingapi-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairedServer paired = null!;

    private string ServerDir => Path.Combine(root, "server");

    private static TrackingRow Row(int n) => new($"obj{n}|{n:D19}", n * 1000L, $"{{\"object_name\":\"obj{n}\"}}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ServerDir);
        var projects = new ProjectsFile(ServerDir);
        var settings = new BuildServerSettings(ServerDir, new Uri("https://127.0.0.1:0"));
        app = BuildServer.Build(settings, BuildServer.Certificate(ServerDir), new ChildProcessCommandRunner(), projects, new TuiCommand.CliHost(_ => null).Commands, _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ServerRole.Admin));
        paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> Send(ServerRole role, HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "ada", role)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static void Copy(StateDatabase state, params int[] rows)
    {
        var store = new BuildTrackingStore(state);
        var stream = BuildStreams.Get(Stream).Stream;
        var progress = new ProgressStore(state, BuildTrackingState.ProgressKey);
        var last = Row(rows[^1]);
        var version = progress.GetCheckpoint(TrackingFollower.CheckpointNameOf(Stream), Identity)?.Version ?? 0;
        Assert.True(progress.Advance(TrackingFollower.CheckpointNameOf(Stream), new Dictionary<string, ProgressValue> { [TrackingFollower.OrderPartName] = ProgressValue.OfInt(last.Order), [TrackingFollower.KeyPartName] = ProgressValue.OfText(last.Key) },
            version, itemKey: Identity, alsoWrite: (c, t) => store.Write(c, t, Identity, stream, [.. rows.Select(Row)])));
    }

    [Fact]
    public async Task A_viewer_reads_the_mode_the_checkpoints_and_the_divergences_and_a_server_with_no_copy_says_none()
    {
        using (var empty = await Send(ServerRole.Viewer, HttpMethod.Get, "/api/v1/tracking"))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            var none = await Json(empty);
            Assert.Equal(("both", "target"), ((string?)none["mode"], (string?)none["primary"]));
            Assert.Empty(none["copies"]!.AsArray());
            Assert.Empty(none["divergences"]!.AsArray());
        }

        var state = BuildSchedulerSchema.Open(ServerDir);
        Copy(state, 1, 2, 3);
        new StateTrackingDivergences(state).Mark(Identity, Stream, "restored", new TrackingPosition(2000, Row(2).Key), new TrackingPosition(2000, Row(2).Key), new TrackingPosition(3000, Row(3).Key));

        var overview = await Json(await Send(ServerRole.Viewer, HttpMethod.Get, "/api/v1/tracking"));

        var copy = Assert.Single(overview["copies"]!.AsArray())!;
        Assert.Equal(Identity, (string?)copy["identity"]);
        var stream = copy["streams"]!.AsArray().Single(s => (string?)s!["stream"] == Stream)!;
        Assert.Equal((3, Row(3).Key, true), ((int)stream["rows"]!, (string?)stream["checkpoint"]!["key"], (bool)stream["diverged"]!));
        Assert.False((bool)copy["streams"]!.AsArray().Single(s => (string?)s!["stream"] == "run_log")!["diverged"]!);
        var divergence = Assert.Single(overview["divergences"]!.AsArray())!;
        Assert.Equal(("diverged", true, Row(2).Key), ((string?)divergence["status"], (bool)divergence["open"]!, (string?)divergence["common"]!["key"]));
    }

    [Fact]
    public async Task Only_an_administrator_resolves_a_divergence_and_the_copy_after_the_common_point_is_archived_or_deleted()
    {
        var state = BuildSchedulerSchema.Open(ServerDir);
        Copy(state, 1, 2, 3);
        var open = new StateTrackingDivergences(state).Mark(Identity, Stream, "restored", new TrackingPosition(2000, Row(2).Key), new TrackingPosition(2000, Row(2).Key), new TrackingPosition(3000, Row(3).Key));
        var path = $"/api/v1/tracking/divergences/{open.Id}/resolve";

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(ServerRole.Viewer, HttpMethod.Post, path, "{\"resolution\":\"archive\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(ServerRole.Admin, HttpMethod.Post, path, "{\"resolution\":\"later\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(ServerRole.Admin, HttpMethod.Post, "/api/v1/tracking/divergences/9999/resolve", "{\"resolution\":\"archive\"}")).StatusCode);
        Assert.Equal(3, new BuildTrackingStore(state).Count(Identity, Stream));

        using var done = await Send(ServerRole.Admin, HttpMethod.Post, path, "{\"resolution\":\"archive\"}");

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var answer = await Json(done);
        Assert.Equal(("archived", false, "ada via test console"), ((string?)answer["divergence"]!["status"], (bool)answer["divergence"]!["open"]!, (string?)answer["divergence"]!["resolved_by"]));
        Assert.Equal([Row(1).Key, Row(2).Key], new BuildTrackingStore(state).Rows(Identity, Stream).Select(r => r.Key));      // the later row is out of the live stream
        Assert.Equal(Row(2).Key, new ProgressStore(state, BuildTrackingState.ProgressKey).GetCheckpoint(TrackingFollower.CheckpointNameOf(Stream), Identity)!.Parts[TrackingFollower.KeyPartName].AsText);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(ServerRole.Admin, HttpMethod.Post, path, "{\"resolution\":\"archive\"}")).StatusCode);   // it is closed now
    }
}
