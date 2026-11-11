using DbDataKit.Authoring.Server;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataWorkspace.Server;
using Microsoft.AspNetCore.Builder;

namespace DbDataWorkspace.Tests;

/// <summary>
/// A real Workspace server in a directory of its own, on SQLite or on a scratch database of the shared SQL Server or PostgreSQL, paired the way a console pairs. A call asks for its own token (a token is good for one call).
/// </summary>
internal sealed class WorkspaceHarness : IAsyncDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("ddw-").FullName;
    public ConsoleIdentity Console { get; } = new("console-1", ConsoleSigningKey.Generate());
    public List<string> Log { get; } = [];
    public WorkspaceSettings Settings { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public PairedServer Paired { get; private set; } = null!;
    public ScratchStateDatabase? Scratch { get; }
    private WebApplication app = null!;

    private WorkspaceHarness(ScratchStateDatabase? scratch) => Scratch = scratch;

    /// <summary>Starts the server on <paramref name="engine"/> (<c>sqlite</c>, <c>postgres</c> or <c>mssql</c>; the test is skipped when that engine is not set up).</summary>
    public static async Task<WorkspaceHarness> StartAsync(string engine = "sqlite", ServerRole ceiling = ServerRole.Admin | ServerRole.Author | ServerRole.Approver, WorkspaceAppSettings? apps = null)
    {
        var harness = new WorkspaceHarness(engine == "sqlite" ? null : ScratchStateDatabase.For(engine));
        await harness.StartAsync(ceiling, apps);
        return harness;
    }

    private async Task StartAsync(ServerRole ceiling, WorkspaceAppSettings? apps)
    {
        if (Scratch is not null) new StateSettingsWriter().Write(Path.Combine(Directory, "server.yaml"), new StateSettings(Scratch.Engine, Scratch.Connection));
        var resolved = WorkspaceState.Resolve(Directory, Scratch is null ? null : Scratch.Environment);
        Settings = new WorkspaceSettings(Directory, new Uri("https://127.0.0.1:0")) { State = resolved.Settings, StateSources = resolved.Sources, Apps = apps };
        app = WorkspaceServer.Build(Settings, WorkspaceServer.Certificate(Directory), Log.Add);
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = WorkspaceState.Pairing(Directory, Scratch is null ? null : Scratch.Environment).Codes().Create(ServerRoles.Close(ceiling));
        Paired = await PairingClient.PairAsync(url, code.Code, Console, ClientKind.Console, "test console");
        Http = new HttpClient(PairingClient.Pinned(Paired.CertificateFingerprint)) { BaseAddress = url };
    }

    public string Token(string user, ServerRole role, IReadOnlyDictionary<string, ServerRole>? projects = null) =>
        Console.Issuer().Issue(new ConsoleTokenRequest(Paired.InstanceId, user, user, role, projects));

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> Send(string user, ServerRole role, HttpMethod method, string path, string? json = null, IReadOnlyDictionary<string, ServerRole>? projects = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, Token(user, role, projects));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length == 0) return (response, null);
        try { return (response, JsonNode.Parse(text)); }
        catch (System.Text.Json.JsonException) { return (response, null); }       // a page or a plain sentence, not JSON
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> AsAda(HttpMethod method, string path, string? json = null) => Send("ada", ServerRole.Author, method, path, json);

    public Task<(HttpResponseMessage Response, JsonNode? Body)> AsAdmin(HttpMethod method, string path, string? json = null) => Send("root", ServerRole.Admin, method, path, json);

    /// <summary>A store over the server's own state, to read what the server wrote.</summary>
    public WorkspaceStore Store() => new(WorkspaceState.Open(Directory, Settings.State, Scratch is null ? null : Scratch.Environment));

    public IReadOnlyList<AuditEntry> Audit() => new FileAuditLog(Path.Combine(Directory, "management-audit.log")).Read();

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Scratch?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
