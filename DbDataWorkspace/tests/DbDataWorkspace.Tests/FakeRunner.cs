using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace DbDataWorkspace.Tests;

/// <summary>
/// A Runner as far as publishing a package goes (plan 33 SC22b): it answers <c>GET /version</c> as <c>dbdatarunner</c>, pairs for real, checks the token as the Runner does (the publisher role) and takes the calls of the shared publishing client:
/// <c>HEAD</c> and <c>PUT /api/v1/artifacts/{sha256}</c> (a file it has is not asked for again, across publishes), <c>PUT /api/v1/definitions/integration/{name}</c> (one version per different manifest and file set) and
/// <c>PUT /api/v1/bindings/{channel}/integration/{name}</c>. It records who called as whom and what it was sent, and can be told to refuse in a sentence of its own.
/// </summary>
internal sealed class FakeRunner : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string directory;
    public Uri Url { get; private set; } = null!;
    public PairingCodes Codes { get; }
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentDictionary<string, long> Artifacts { get; } = new();
    public ConcurrentQueue<JsonObject> Published { get; } = new();
    public ConcurrentQueue<JsonObject> Bindings { get; } = new();
    public ConcurrentQueue<int> UploadsPerPublish { get; } = new();
    public string? RefuseWith { get; set; }

    private readonly Dictionary<string, List<string>> signatures = new();
    private int uploadedSincePublish;

    private FakeRunner(WebApplication app, string directory, PairingCodes codes) => (this.app, this.directory, Codes) = (app, directory, codes);

    public static async Task<FakeRunner> StartAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ddw-runner-").FullName;
        var certificate = DirectoryCertificate.Load(directory);
        var clients = new PairedClientsFile(Path.Combine(directory, "server.yaml"));
        var codes = new PairingCodes(Path.Combine(directory, "pairing-codes.json"));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.UseDbDataHost(host => { host.Product = "dbdatarunner"; host.Version = "9.9.9"; });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        const string instance = "fake-runner-1";
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(instance, PairingProof.CertificateFingerprint(certificate), clients, codes));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instance, clients, new MemoryReplayCache()));
        var runner = new FakeRunner(app, directory, codes);
        app.MapMethods("api/v1/artifacts/{sha}", ["HEAD"], (string sha, HttpContext context) => runner.Handle(context, () => runner.Artifacts.ContainsKey(sha) ? Results.Ok() : Results.NotFound()));
        app.MapPut("api/v1/artifacts/{sha}", async (string sha, HttpContext context) => await runner.HandleAsync(context, async () =>
        {
            using var copy = new MemoryStream();
            await context.Request.Body.CopyToAsync(copy);
            runner.Artifacts[sha] = copy.Length;
            Interlocked.Increment(ref runner.uploadedSincePublish);
            return Results.Json(new { sha256 = sha, size = copy.Length, already_stored = false });
        }));
        app.MapPut("api/v1/definitions/integration/{name}", async (string name, HttpContext context) => await runner.HandleAsync(context, async () =>
        {
            var body = (await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body))!;
            body["name"] = name;
            runner.Published.Enqueue(body);
            runner.UploadsPerPublish.Enqueue(Interlocked.Exchange(ref runner.uploadedSincePublish, 0));
            var channel = (string)body["channel"]!;
            var signature = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((string)body["manifest"]! + string.Join("|", body["artifacts"]!.AsArray().Select(a => (string)a!["sha256"]!)))));
            lock (runner.signatures)
            {
                var key = name + "/" + channel;
                var seen = runner.signatures.TryGetValue(key, out var list) ? list : runner.signatures[key] = [];
                var changed = !seen.Contains(signature);
                if (changed) seen.Add(signature);
                return Results.Json(new { kind = "integration", name, channel, version = seen.IndexOf(signature) + 1, changed, mode = (string)body["mode"]!, signature, tasks = new[] { "orders" } });
            }
        }));
        app.MapPut("api/v1/bindings/{channel}/integration/{name}", async (string channel, string name, HttpContext context) => await runner.HandleAsync(context, async () =>
        {
            var body = (await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body))!;
            body["channel"] = channel;
            runner.Bindings.Enqueue(body);
            return Results.Json(new { name, channel, bindings = Array.Empty<object>() });
        }));
        await app.StartAsync();
        runner.Url = new Uri(app.Urls.First().Replace("[::1]", "127.0.0.1"));
        return runner;
    }

    private IResult Handle(HttpContext context, Func<IResult> work) => HandleAsync(context, () => Task.FromResult(work())).GetAwaiter().GetResult();

    private async Task<IResult> HandleAsync(HttpContext context, Func<Task<IResult>> work)
    {
        var token = context.ManagementToken();
        Calls.Enqueue($"{context.Request.Method} {context.Request.Path} as {token.SubjectId} ({token.SubjectName}) with {string.Join("+", ServerRoles.Names(token.Roles))}");
        if (!ManagementAuthorization.Authorize(token, ServerRole.Publisher).Allowed && !ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed)
            return Results.Json(new { error = "Publishing needs the publisher role." }, statusCode: 403);
        if (RefuseWith is { } refusal && context.Request.Method == "PUT" && context.Request.Path.Value!.Contains("/definitions/", StringComparison.Ordinal)) return Results.Json(new { error = refusal }, statusCode: 400);
        return await work();
    }

    public string NewCode(ServerRole ceiling = ServerRole.Publisher) => Codes.Create(ServerRoles.Close(ceiling)).Code;

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
