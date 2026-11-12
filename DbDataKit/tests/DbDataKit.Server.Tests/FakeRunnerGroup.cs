using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DbDataKit.Server.Tests;

/// <summary>
/// A runner group's shared publish API as far as a promotion uses it (plan 30 PB06), behind the token check of a real server and with the roles it asks for (reading a viewer's, writing an administrator's): one package with versions and channels, the files it holds
/// by hash, the bindings written to it and every call it received with the person the token names. A test fixes what it refuses (<see cref="Refuse"/>).
/// </summary>
internal sealed class FakeRunnerGroup
{
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The files the group holds, by hash.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();

    /// <summary>What was published to it, by channel: the version and the manifest.</summary>
    public ConcurrentDictionary<string, (int Version, string Manifest, string? Signature, string[] Paths)> Published { get; } = new();

    public ConcurrentDictionary<string, JsonObject> Bindings { get; } = new();

    /// <summary>The versions it holds for reading: number to (manifest, files).</summary>
    public Dictionary<int, (string Manifest, (string Path, byte[] Bytes)[] Files, string? Signature)> Versions { get; } = [];

    /// <summary>The version each channel serves.</summary>
    public Dictionary<string, int> Active { get; } = [];

    /// <summary>When set, a publish is refused with this words and status 409 (an unbound slot, a dependency that is not active).</summary>
    public string? Refuse { get; set; }

    /// <summary>The kinds it was asked for (a Gateway's group serves many; a runner's one), in the order of the calls.</summary>
    public ConcurrentQueue<string> Kinds { get; } = new();

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Map(WebApplication server, PairingCodes codes, PairedClientsFile clients)
    {
        IResult Needs(HttpContext c, ServerRole role, Func<IResult> then)
        {
            if (c.Request.RouteValues["kind"] is string asked) Kinds.Enqueue(asked);
            Calls.Enqueue($"{c.Request.Method} {c.Request.Path.Value!["/api/v1/".Length..]} by {c.ManagementToken().SubjectName}");
            return ManagementAuthorization.Authorize(c.ManagementToken(), role).Allowed ? then() : Results.Json(new { error = $"This needs the {role.ToString().ToLowerInvariant()} role." }, statusCode: 403);
        }

        server.MapGet("/api/v1/definitions/{kind}/{name}", (HttpContext c, string kind, string name) => Needs(c, ServerRole.Viewer, () => Results.Json(new
        {
            name, channels = Active.Select(a => new { channel = a.Key, active = a.Value, staged = (int?)null }),
        })));
        server.MapGet("/api/v1/definitions/{kind}/{name}/versions/{n:int}", (HttpContext c, string kind, string name, int n) => Needs(c, ServerRole.Viewer, () =>
            Versions.TryGetValue(n, out var v)
                ? Results.Json(new
                {
                    kind, name, version = n, format = 1, manifest = v.Manifest, signature = v.Signature, depends_on = Array.Empty<string>(),
                    artifacts = v.Files.Select(f => new { path = f.Path, sha256 = Sha(f.Bytes), size = f.Bytes.Length, media_type = "application/octet-stream" }),
                })
                : Results.Json(new { error = $"There is no version {n}." }, statusCode: 404)));
        server.MapMethods("/api/v1/artifacts/{sha}", ["HEAD"], (HttpContext c, string sha) => Needs(c, ServerRole.Viewer, () => Files.ContainsKey(sha) || Versions.Values.Any(v => v.Files.Any(f => Sha(f.Bytes) == sha)) ? Results.Ok() : Results.NotFound()));
        server.MapGet("/api/v1/artifacts/{sha}", (HttpContext c, string sha) => Needs(c, ServerRole.Viewer, () =>
            Files.TryGetValue(sha, out var held) ? Results.Bytes(held, "application/octet-stream")
            : Versions.Values.SelectMany(v => v.Files).FirstOrDefault(f => f.Bytes is not null && Sha(f.Bytes) == sha) is { Bytes: { } found } ? Results.Bytes(found, "application/octet-stream") : Results.NotFound()));
        server.MapPut("/api/v1/artifacts/{sha}", async (HttpContext c, string sha) =>
        {
            using var body = new MemoryStream();
            await c.Request.Body.CopyToAsync(body);
            return Needs(c, ServerRole.Admin, () =>
            {
                if (Sha(body.ToArray()) != sha) return Results.Json(new { error = "The bytes do not hash to " + sha }, statusCode: 400);
                Files[sha] = body.ToArray();
                return Results.Json(new { sha256 = sha, size = body.Length, already_stored = false });
            });
        });
        server.MapPut("/api/v1/bindings/{channel}/{kind}/{name}", async (HttpContext c, string channel, string kind, string name) =>
        {
            var body = JsonNode.Parse(await new StreamReader(c.Request.Body).ReadToEndAsync())!.AsObject();
            return Needs(c, ServerRole.Admin, () => { Bindings[channel] = body; return Results.Json(new { name, channel }); });
        });
        server.MapPut("/api/v1/definitions/{kind}/{name}", async (HttpContext c, string kind, string name) =>
        {
            var body = JsonNode.Parse(await new StreamReader(c.Request.Body).ReadToEndAsync())!.AsObject();
            return Needs(c, ServerRole.Admin, () =>
            {
                if (Refuse is { } words) return Results.Json(new { error = words, code = "refused" }, statusCode: 409);
                var channel = (string)body["channel"]!;
                var manifest = (string)body["manifest"]!;
                var same = Published.TryGetValue(channel, out var before) && before.Manifest == manifest;
                var version = same ? before.Version : Published.Count + 1;
                Published[channel] = (version, manifest, (string?)body["signature"], [.. body["artifacts"]!.AsArray().Select(a => (string)a!["path"]!)]);
                return Results.Json(new { kind, name, channel, version, changed = !same, mode = (string?)body["mode"] ?? "stage", signature = (string?)body["signature"] ?? "none", tasks = Array.Empty<string>() });
            });
        });
    }
}
