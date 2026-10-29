using DbDataProxy.Server.Secrets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Hosting.Local;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
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
        var configDirectory = ProxyConfigFile.DirectoryIn(dataDirectory);
        var provider = configuration.Providers.OfType<ProxyConfigDirectoryProvider>().Single();
        var git = new GitCommitService(configDirectory);
        // The history starts with the configuration as it was found, so the first change made through the API reads as a change to something.
        if (git.GetHistory("", 1).Count == 0 && Directory.EnumerateFiles(configDirectory, "*.yaml", SearchOption.AllDirectories).ToList() is { Count: > 0 } found)
            git.CommitChanges(found, "Configuration as found", new GitAuthor("dbdataproxy", "dbdataproxy@local.invalid"));
        // the data sources need the management object's validation, and it needs them to route to: made in two steps
        ProxyDataSources? dataSources = null;
        var management = new ProxyManagement(configuration, configDirectory, instanceId, ProxyHost.Version);
        var audit = new FileAuditLog(Path.Combine(dataDirectory, "management-audit.log"));   // one for every kind of change: appends go through its lock
        dataSources = new ProxyDataSources(configDirectory, management, provider.ReloadNow, () => app.Services.GetRequiredService<DataSourceCatalog>(), audit, git);
        var identities = new ProxyIdentities(configDirectory, management, provider.ReloadNow, () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue, audit, git);
        var activity = new ProxyActivity(app.Services.GetRequiredService<SessionStore>(), app.Services.GetRequiredService<ActiveQueries>(), app.Services.GetRequiredService<QueryLimits>(),
            app.Services.GetRequiredService<LoginThrottle>(), () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue, audit,
            app.Services.GetRequiredService<DbDataKit.State.NotificationStore>(), app.Services.GetRequiredService<RecentFailures>(), instanceId, ProxyHost.Version);
        var certificate = new ProxyCertificate(holder, () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue, dataDirectory, app.Services.GetRequiredService<RecentFailures>(), audit);
        management = new ProxyManagement(configuration, configDirectory, instanceId, ProxyHost.Version, dataSources, identities, activity, certificate, git,
            new ProxySecrets(SecretsRuntime.OpenStore(options), audit), files.Codes(),
            new ProxySemanticModels(new Serving.PublishedSemanticModels(dataDirectory), app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>(),
                () => app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue, configDirectory, () => app.Services.GetRequiredService<DataSourceCatalog>(), audit));
        var validator = new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache());
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataProxy.Management");

        app.UseManagementApiVersion();
        app.UseManagementAuthentication(validator, refused => log.LogWarning("A management call was refused ({Failure}): {Detail}", refused.Failure, refused.Detail));
        app.MapPairing(new PairingService(instanceId,
            () => holder.Current is { } certificate ? PairingProof.CertificateFingerprint(certificate) : throw new InvalidOperationException("This server has no certificate to pair against."),
            clients, files.Codes()));
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));

        app.MapMethods($"{ProxyManagement.Prefix}/{{**rest}}", ["GET", "POST", "PUT", "DELETE"], async (HttpContext context) =>
        {
            var caller = new ManagementCaller(context.ManagementToken());
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = context.Request.Method is "POST" or "PUT" ? await reader.ReadToEndAsync(context.RequestAborted) : null;
            var result = await management.HandleAsync(context.Request.Method, context.Request.Path.Value! + context.Request.QueryString, body, caller, context.RequestAborted);
            return Results.Json(result.Body, statusCode: result.Status);
        });

        if (options.Management.LocalChannel)
        {
            var channel = new LocalChannelServer(new LocalChannelOptions("dbdataproxy") { Directory = string.IsNullOrWhiteSpace(options.Management.LocalChannelDirectory) ? null : options.Management.LocalChannelDirectory }, (request, ct) => HandleLocalAsync(management, request, ct));
            channel.Start();
            app.Lifetime.ApplicationStopping.Register(() => channel.DisposeAsync().AsTask().GetAwaiter().GetResult());
            log.LogInformation("The local channel is {Endpoint}.", channel.Endpoint);
        }
        return management;
    }

    /// <summary>A local-channel message is <c>{"method":"GET","path":"/api/v1/config","body":"..."}</c>; the answer is <c>{"status":200,"body":{...}}</c>. The operating system decided who could connect, so the caller holds every role.</summary>
    internal static async Task<byte[]> HandleLocalAsync(ProxyManagement management, byte[] request, CancellationToken cancellationToken)
    {
        try
        {
            var message = JsonNode.Parse(request)?.AsObject();
            var result = await management.HandleAsync((string?)message?["method"] ?? "GET", (string?)message?["path"] ?? "", (string?)message?["body"], ManagementCaller.Local(Environment.UserName), cancellationToken);
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = result.Status, ["body"] = result.Body });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = 400, ["body"] = new JsonObject { ["error"] = "The message is not a JSON object with a method and a path." } });
        }
    }
}
