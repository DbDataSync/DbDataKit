using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Hosting.Local;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Tls;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DbDataProxy.Server.Management;

/// <summary>
/// The Proxy's management API as a listener (plan 13, P03): HTTPS on a port of its own, never the Flight port. A connection to it is tagged when it is accepted, and a middleware keeps the two apart whatever the port
/// numbers are: a management path on the Flight listener is a 404, and on the management listener only the management paths and the shared <c>/health</c> and <c>/version</c> answer. The calls themselves are
/// <see cref="ProxyManagement"/>'s. Pairing, the token check and the roles are the shared ones every product has.
/// </summary>
public static class ProxyManagementHost
{
    /// <summary>The connection item that says a connection came in on the management listener.</summary>
    public const string ListenerItem = "dbdata.listener.management";

    /// <summary>Adds the management listener to Kestrel (after the Flight one). Only when the options ask for it and TLS is on.</summary>
    public static void Listen(KestrelServerOptions kestrel, ProxyOptions options, TlsCertificateHolder holder)
    {
        if (!options.Management.Enabled || options.Tls.Mode == TlsMode.None) return;
        kestrel.Listen(IPAddress.Parse(options.Management.Address), options.Management.Port, listen =>
        {
            listen.Use(next => async connection =>
            {
                connection.Items[ListenerItem] = true;
                await next(connection);
            });
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(https =>
            {
                https.ServerCertificateSelector = (_, _) => holder.Current;
                https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
            });
        });
    }

    private static bool OnManagementListener(HttpContext context) => context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(ListenerItem) == true;

    /// <summary>Keeps management paths off the Flight listener and everything else off the management one. Runs first, before the shared pipeline answers anything.</summary>
    public static void Guard(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var management = path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/management/", StringComparison.Ordinal);
            var shared = path.StartsWith("/health", StringComparison.Ordinal) || path == "/version";
            var onManagement = OnManagementListener(context);
            if ((management && !onManagement) || (onManagement && !management && !shared))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }

    /// <summary>Maps pairing, the token check and the calls, and starts the local channel if asked. Called once, with the options the server started with.</summary>
    public static ProxyManagement? Map(WebApplication app, ProxyOptions options, string dataDirectory, TlsCertificateHolder holder, IConfigurationRoot configuration)
    {
        if (!options.Management.Enabled) return null;

        var serverYaml = ProxyConfigFile.PathIn(dataDirectory);
        var instanceId = ServerIdentity.InstanceId(serverYaml, "proxy", "dbdataproxy server");
        // the pairing codes wait in the data directory, not in the configuration folder a person keeps under version control
        var files = new PairingFiles(serverYaml, Path.Combine(dataDirectory, "pairing-codes.json"));
        var clients = files.Clients();
        var management = new ProxyManagement(configuration, ProxyConfigFile.DirectoryIn(dataDirectory), instanceId, ProxyHost.Version);
        var validator = new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache());
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataProxy.Management");

        app.UseManagementApiVersion();
        app.UseManagementAuthentication(validator, refused => log.LogWarning("A management call was refused ({Failure}): {Detail}", refused.Failure, refused.Detail));
        app.MapPairing(new PairingService(instanceId,
            () => holder.Current is { } certificate ? PairingProof.CertificateFingerprint(certificate) : throw new InvalidOperationException("This server has no certificate to pair against."),
            clients, files.Codes()));
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));

        app.MapMethods($"{ProxyManagement.Prefix}/{{**rest}}", ["GET", "POST"], async (HttpContext context) =>
        {
            var token = context.ManagementToken();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = context.Request.Method == HttpMethods.Post ? await reader.ReadToEndAsync(context.RequestAborted) : null;
            var result = management.Handle(context.Request.Method, context.Request.Path.Value!, body, role => ManagementAuthorization.Authorize(token, role).Allowed);
            return Results.Json(result.Body, statusCode: result.Status);
        });

        if (options.Management.LocalChannel)
        {
            var channel = new LocalChannelServer(new LocalChannelOptions("dbdataproxy") { Directory = string.IsNullOrWhiteSpace(options.Management.LocalChannelDirectory) ? null : options.Management.LocalChannelDirectory }, (request, ct) => Task.FromResult(HandleLocal(management, request)));
            channel.Start();
            app.Lifetime.ApplicationStopping.Register(() => channel.DisposeAsync().AsTask().GetAwaiter().GetResult());
            log.LogInformation("The local channel is {Endpoint}.", channel.Endpoint);
        }
        return management;
    }

    /// <summary>A local-channel message is <c>{"method":"GET","path":"/api/v1/config","body":"..."}</c>; the answer is <c>{"status":200,"body":{...}}</c>. The operating system decided who could connect, so the caller holds every role.</summary>
    internal static byte[] HandleLocal(ProxyManagement management, byte[] request)
    {
        try
        {
            var message = JsonNode.Parse(request)?.AsObject();
            var result = management.Handle((string?)message?["method"] ?? "GET", (string?)message?["path"] ?? "", (string?)message?["body"], _ => true);
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = result.Status, ["body"] = result.Body });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = 400, ["body"] = new JsonObject { ["error"] = "The message is not a JSON object with a method and a path." } });
        }
    }
}
